using System.Globalization;
using System.Windows.Media;

namespace DreamTray.Plugins.CyberLcd;

/// <summary>One gradient stop: position 0..1 and a color, ascending by position.</summary>
public readonly record struct RgbStop(float Pos, Color Color);

/// <summary>
/// The PC → device wire format.
///
/// One newline-terminated ASCII frame per update, '.'-decimal and '|'-separated.
/// The firmware drops data frames with the wrong field count, so this layout
/// must match <c>LcdMonitorRenderer::applyPacket</c> in
/// <c>src/graphics/renderers/lcd_monitor_renderer.h</c> exactly -- a mismatched
/// plugin goes dark rather than showing wrong numbers. Control-frame syntax
/// (<c>C|...</c>) must match <c>SerialIngest::handleControl</c> in
/// <c>src/serial_ingest.h</c>.
/// </summary>
internal static class CyberLcdFrame
{
    /// <summary>The firmware always expects twenty per-thread load values.</summary>
    private const int ThreadCount = 20;

    /// <summary>
    /// Build a data frame from <paramref name="s"/>, clocked at <paramref name="clock"/>
    /// (same reasoning as CyberVFD's frame builder: the clock must never be stale
    /// even when a sample repeats).
    /// </summary>
    public static string Build(SystemSnapshot s, DateTime? clock = null, float? outdoorCelsius = null)
    {
        var now = clock ?? s.Timestamp;
        string time = now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        string outdoor = outdoorCelsius is float value && float.IsFinite(value)
            ? F(value, 1) : "-";

        string threads = string.Join(",", Threads(s).Select(t => F(t, 2)));

        float gpuCoreGHz = s.GpuClock / 1000f;
        var (disk0, disk0Label, disk1, disk1Label) = BusiestTwoDisks(s);

        return "D|" + time + "|" + outdoor
            + "|" + F(s.CpuTemp, 1) + "|" + F(s.CpuClockAvg, 2) + "|" + F(s.CpuClockMax, 2)
            + "|" + F(s.CpuPower, 1)
            + "|" + F(s.RamUsedGb, 1) + "|" + F(s.RamTotalGb, 1) + "|" + F(s.SwapUsedGb, 2)
            + "|" + F(s.GpuLoad, 3) + "|" + F(s.GpuTemp, 1) + "|" + F(gpuCoreGHz, 2) + "|" + F(s.GpuPower, 1)
            + "|" + F(s.VramUsedGb, 2) + "|" + F(s.VramTotalGb, 1)
            + "|" + F(s.NetDownKbs, 1) + "|" + F(s.NetUpKbs, 1)
            + "|" + F(disk0, 3) + "|" + disk0Label + "|" + F(disk1, 3) + "|" + disk1Label
            + "|" + threads;
    }

    /// <summary>
    /// The two busiest physical drives right now, not a fixed "system + one
    /// other" pair -- a machine with more than two drives should see whichever
    /// two are actually loaded. Falls back to <see cref="SystemSnapshot.Disk0Load"/>/
    /// <see cref="SystemSnapshot.Disk1Load"/> if <see cref="SystemSnapshot.DiskLoads"/>
    /// is empty (older host build).
    /// </summary>
    private static (float Load0, string Label0, float Load1, string Label1) BusiestTwoDisks(SystemSnapshot s)
    {
        if (s.DiskLoads.Length == 0)
        {
            string fallbackLabel = string.IsNullOrEmpty(s.Disk1Label) ? "D:" : s.Disk1Label;
            return (s.Disk0Load, "C:", s.Disk1Load, fallbackLabel);
        }

        float load0 = s.DiskLoads[0];
        string label0 = s.DiskLabels.Length > 0 ? s.DiskLabels[0] : "0:";
        if (s.DiskLoads.Length < 2) return (load0, label0, -1f, "");

        return (load0, label0, s.DiskLoads[1], s.DiskLabels.Length > 1 ? s.DiskLabels[1] : "1:");
    }

    /// <summary>Pad or truncate to the fixed width the firmware parses.</summary>
    private static IEnumerable<float> Threads(SystemSnapshot s)
    {
        for (int i = 0; i < ThreadCount; i++)
            yield return i < s.ThreadLoads.Length ? s.ThreadLoads[i] : 0f;
    }

    private static string F(float v, int dp) =>
        float.IsNaN(v) || float.IsInfinity(v)
            ? 0f.ToString("F" + dp, CultureInfo.InvariantCulture)
            : v.ToString("F" + dp, CultureInfo.InvariantCulture);

    private static string Hex(Color c) => $"{c.R:X2}{c.G:X2}{c.B:X2}";

    private static string StopsWire(IEnumerable<RgbStop> stops) =>
        string.Join(",", stops.OrderBy(s => s.Pos)
            .Select(s => s.Pos.ToString("F2", CultureInfo.InvariantCulture) + ":" + Hex(s.Color)));

    // ---- persisted-settings (de)serialization -- reuses the same wire syntax so
    // there is exactly one place that knows the "pos:RRGGBB" grammar. ----

    public static string ColorToHex(Color c) => Hex(c);

    public static bool TryParseHex(string hex, out Color color)
    {
        color = default;
        var t = hex.Trim().TrimStart('#');
        if (t.Length != 6 || !uint.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
            return false;
        color = Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    public static string StopsToWire(IEnumerable<RgbStop> stops) => StopsWire(stops);

    public static List<RgbStop> ParseStops(string wire)
    {
        var result = new List<RgbStop>();
        if (string.IsNullOrWhiteSpace(wire)) return result;
        foreach (var part in wire.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split(':', 2);
            if (kv.Length != 2) continue;
            if (!float.TryParse(kv[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var pos)) continue;
            if (!TryParseHex(kv[1], out var color)) continue;
            result.Add(new RgbStop(pos, color));
        }
        return result;
    }

    // ---- control commands (host → device) ----

    public static string Power(bool on) => $"C|PWR|{(on ? 1 : 0)}";
    public static string DisplayBrightness(int value) => $"C|DBR|{Math.Clamp(value, 0, 255)}";
    public static string DeskBrightness(int value) => $"C|KBR|{Math.Clamp(value, 0, 255)}";

    public static string DisplayModeTemperature() => "C|DM|T";
    public static string DisplayModeSolid(Color c) => $"C|DM|S|{Hex(c)}";
    public static string DisplayModeGradient(IEnumerable<RgbStop> stops) => $"C|DM|G|{StopsWire(stops)}";
    public static string DisplayModeRainbow(float speedDegPerSec, float widthLeds) =>
        $"C|DM|R|{F(speedDegPerSec, 1)}|{F(widthLeds, 1)}";

    public static string DeskModeSolid(Color c) => $"C|KM|S|{Hex(c)}";
    public static string DeskModeGradient(IEnumerable<RgbStop> stops) => $"C|KM|G|{StopsWire(stops)}";
    public static string DeskModeRainbow(float speedDegPerSec, float widthLeds) =>
        $"C|KM|R|{F(speedDegPerSec, 1)}|{F(widthLeds, 1)}";

    public static string DeskLedCount(int count) => $"C|KN|{Math.Max(1, count)}";
}
