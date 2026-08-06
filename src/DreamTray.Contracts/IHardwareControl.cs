namespace DreamTray;

/// <summary>
/// The write side of the hardware layer: everything DreamTray can change rather
/// than just observe. Exposed to plugins so, for example, a device plugin can dim
/// the screen or drop the TDP without duplicating the platform code.
/// </summary>
public interface IHardwareControl
{
    // ---- Display brightness ----
    /// <summary>Displays that accept a brightness command, re-enumerated on demand.</summary>
    IReadOnlyList<DisplayTarget> GetDisplays(bool refresh = false);
    /// <summary>Set brightness 0..100 on one display. Returns false if the display refused.</summary>
    bool SetBrightness(string displayId, int percent);
    /// <summary>Set brightness 0..100 on every controllable display.</summary>
    void SetAllBrightness(int percent);

    // ---- APU power limits ----
    /// <summary>Null when no supported CPU/driver is present (limits are unavailable).</summary>
    ITdpControl? Tdp { get; }

    // ---- Display modes ----
    /// <summary>
    /// Attached displays as the graphics stack names them, primary first. This is a
    /// different identity space from <see cref="GetDisplays"/>: brightness is per
    /// physical panel, display modes are per GDI adapter output.
    /// </summary>
    IReadOnlyList<DisplayDevice> GetDisplayDevices();
    IReadOnlyList<DisplayMode> GetModes(string deviceName);
    DisplayMode? GetCurrentMode(string deviceName);
    bool SetMode(string deviceName, DisplayMode mode);

    // ---- Windows theme ----
    /// <summary>Switch the Windows apps+system theme. Returns false if the write failed.</summary>
    bool SetWindowsDarkMode(bool dark);

    // ---- Machine shape ----
    /// <summary>
    /// False on a desktop. Anything that distinguishes "on charger" from "on
    /// battery" — readouts, automatic rules, per-source defaults — is meaningless
    /// there and should hide itself rather than show controls that can never fire.
    /// Unlike <see cref="SystemSnapshot.BatteryPresent"/> this answers without a
    /// sensor sample, so it can be used to decide whether a widget exists at all.
    /// </summary>
    bool HasBattery { get; }

    // ---- Sleep policy ----
    /// <summary>Standby timeout and lid-close action, or null when no power scheme is readable.</summary>
    IPowerPolicy? PowerPolicy { get; }
}

/// <summary>
/// The sleep half of the active Windows power scheme. Every value exists twice —
/// once for mains power and once for battery — exactly as it does in the Windows
/// settings page, and <paramref name="onAc"/> selects which half is addressed.
/// </summary>
public interface IPowerPolicy
{
    /// <summary>True while the machine is on mains power.</summary>
    bool IsOnAcPower { get; }
    /// <summary>False on a desktop, where the AC/battery split does not exist.</summary>
    bool HasBattery { get; }
    /// <summary>True when this machine exposes a lid-close action.</summary>
    bool HasLid { get; }

    /// <summary>Idle seconds before standby (0 = never), or null when unreadable.</summary>
    int? GetSleepTimeout(bool onAc);
    bool SetSleepTimeout(bool onAc, int seconds);

    LidAction? GetLidCloseAction(bool onAc);
    bool SetLidCloseAction(bool onAc, LidAction action);
}

/// <summary>What closing the lid does. Values match the Windows power setting.</summary>
public enum LidAction
{
    DoNothing = 0,
    Sleep = 1,
    Hibernate = 2,
    ShutDown = 3,
}

/// <summary>One physical display that can be controlled.</summary>
public sealed record DisplayTarget(
    string Id,
    string Name,
    DisplayKind Kind,
    bool SupportsBrightness)
{
    /// <summary>Current brightness 0..100, or -1 when it cannot be read.</summary>
    public int Brightness { get; set; } = -1;
}

public enum DisplayKind
{
    /// <summary>Laptop panel — driven through the WMI backlight interface.</summary>
    Internal,
    /// <summary>External monitor — driven through DDC/CI over the video cable.</summary>
    External,
}

/// <summary>One attached display output.</summary>
/// <param name="DeviceName">GDI adapter name, e.g. <c>\\.\DISPLAY1</c> — the id used for modes.</param>
public sealed record DisplayDevice(string DeviceName, string FriendlyName, bool IsPrimary);

/// <summary>A display resolution + refresh rate pair.</summary>
public sealed record DisplayMode(int Width, int Height, int RefreshHz)
{
    public override string ToString() => $"{Width}×{Height} @ {RefreshHz} Hz";
}

/// <summary>APU sustained/boost power limits, in watts.</summary>
public interface ITdpControl
{
    /// <summary>Human-readable backend state, e.g. "RyzenAdj ready (family 25)" or why it is unavailable.</summary>
    string StatusText { get; }
    bool IsAvailable { get; }

    /// <summary>Safe slider bounds in W.</summary>
    int MinWatts { get; }
    int MaxWatts { get; }

    /// <summary>Last value DreamTray applied, in W (0 when nothing applied yet).</summary>
    int AppliedWatts { get; }

    /// <summary>
    /// Apply a sustained limit. Sets STAPM + slow + fast limits together so OEM
    /// software cannot leave one of them lower than the others.
    /// </summary>
    bool Apply(int watts);

    /// <summary>Live limits read back from the SMU, or null when unreadable.</summary>
    TdpReadback? Read();
}

/// <summary>Limits and actuals as reported by the SMU.</summary>
public sealed record TdpReadback(
    float StapmLimit, float StapmValue,
    float FastLimit, float FastValue,
    float SlowLimit, float SlowValue);
