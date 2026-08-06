using System.Globalization;

namespace DreamTray.Plugins.CyberVfd;

/// <summary>
/// The PC → ESP32 wire format.
///
/// One newline-terminated ASCII frame per update, '.'-decimal and '|'-separated.
/// The firmware drops frames with the wrong field count, so this layout must match
/// <c>applyPacket</c> in <c>src/graphics/renderers/vfd_monitor_renderer.h</c>
/// exactly — a mismatched agent goes dark rather than showing wrong numbers.
/// </summary>
internal static class VfdFrame
{
    /// <summary>The firmware always expects sixteen per-thread load values.</summary>
    private const int ThreadCount = 16;

    public static string Build(SystemSnapshot s)
    {
        var now = s.Timestamp;
        string time = now.ToString("HH:mm", CultureInfo.InvariantCulture);
        // English uppercase day name + dd/MM, e.g. "THURSDAY 02/07".
        string date = now.DayOfWeek.ToString().ToUpperInvariant()
                      + " " + now.ToString("dd/MM", CultureInfo.InvariantCulture);

        string threads = string.Join(",", Threads(s).Select(t => F(t, 2)));

        return "D|" + time + "|" + date
            + "|" + F(s.CpuTemp, 1) + "|" + F(s.CpuClockAvg, 2) + "|" + F(s.CpuClockMax, 2)
            + "|" + F(s.CpuPower, 1)
            + "|" + F(s.RamUsedGb, 1) + "|" + F(s.RamTotalGb, 1) + "|" + F(s.SwapUsedGb, 2)
            + "|" + F(s.GpuLoad, 3) + "|" + F(s.GpuTemp, 1) + "|" + F(s.GpuPower, 1)
            + "|" + F(s.VramUsedGb, 2) + "|" + F(s.VramTotalGb, 1)
            + "|" + F(s.NetDownKbs, 1) + "|" + F(s.NetUpKbs, 1)
            + "|" + F(s.Disk0Load, 3) + "|" + F(s.Disk1Load, 3) + "|" + s.Disk1Label
            + "|" + F(s.BatteryPower, 1) + "|" + threads;
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

    // ---- control commands (host → device) ----

    public static string Power(bool on) => $"C|PWR|{(on ? 1 : 0)}";
    public static string Backlight(bool on) => $"C|BL|{(on ? 1 : 0)}";
    public static string Brightness(int value) => $"C|BR|{Math.Clamp(value, 0, 255)}";
}
