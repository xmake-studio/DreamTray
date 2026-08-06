using System.Runtime.InteropServices;

namespace DreamTray.Power;

/// <summary>
/// The two sleep settings Windows puts on the "Power &amp; battery" page: the standby
/// idle timeout and what closing the lid does.
///
/// These live in the *active power scheme*, and every setting in a scheme exists
/// twice — once for AC ("Plugged in") and once for DC ("On battery"). We read and
/// write whichever half matches the current power source, so the widget shows the
/// value that is actually in force right now.
///
/// Written values only take effect once the scheme is re-activated, which is why
/// every write is followed by <c>PowerSetActiveScheme</c> on the same GUID. No
/// elevation is needed: these are per-user settings.
/// </summary>
public sealed class PowerPolicyService(Action<string> log) : IPowerPolicy
{
    /// <summary>Sentinel for "Never" — the value Windows stores for a disabled timeout.</summary>
    public const int NeverSeconds = 0;

    /// <summary>True while the machine is running on mains power.</summary>
    public bool IsOnAcPower =>
        GetSystemPowerStatus(out var status) && status.ACLineStatus == 1;

    /// <summary>False on a desktop, where the AC/DC split is meaningless.</summary>
    public bool HasBattery =>
        GetSystemPowerStatus(out var status) &&
        (status.BatteryFlag & BATTERY_FLAG_NO_BATTERY) == 0;

    /// <summary>True when the active scheme could be read at all.</summary>
    public bool IsAvailable => TryGetActiveScheme(out _);

    // ---- standby timeout ----

    /// <summary>Idle seconds before standby for the given power source, or null when unreadable.</summary>
    public int? GetSleepTimeout(bool onAc) => Read(GuidSleepSubgroup, GuidStandbyTimeout, onAc);

    public bool SetSleepTimeout(bool onAc, int seconds) =>
        Write(GuidSleepSubgroup, GuidStandbyTimeout, onAc, (uint)Math.Max(0, seconds));

    // ---- lid close ----

    public LidAction? GetLidCloseAction(bool onAc)
    {
        int? raw = Read(GuidButtonSubgroup, GuidLidCloseAction, onAc);
        return raw is null or < 0 or > 3 ? null : (LidAction)raw.Value;
    }

    public bool SetLidCloseAction(bool onAc, LidAction action) =>
        Write(GuidButtonSubgroup, GuidLidCloseAction, onAc, (uint)action);

    /// <summary>
    /// True when this machine has a lid at all. There is no direct query, so we use
    /// the presence of a readable lid-close setting as the proxy — desktops leave
    /// the setting unpopulated in the scheme.
    /// </summary>
    public bool HasLid => Read(GuidButtonSubgroup, GuidLidCloseAction, true) != null;

    // ---------------------------------------------------------------- internals

    private int? Read(Guid subgroup, Guid setting, bool onAc)
    {
        if (!TryGetActiveScheme(out var scheme)) return null;
        uint value;
        uint error = onAc
            ? PowerReadACValueIndex(nint.Zero, ref scheme, ref subgroup, ref setting, out value)
            : PowerReadDCValueIndex(nint.Zero, ref scheme, ref subgroup, ref setting, out value);
        if (error != ERROR_SUCCESS)
        {
            log($"power policy read {setting} ({(onAc ? "AC" : "DC")}) failed: {error}");
            return null;
        }
        // Values are DWORDs; a timeout beyond int range would be nonsense, so clamp
        // rather than wrap negative.
        return value > int.MaxValue ? int.MaxValue : (int)value;
    }

    private bool Write(Guid subgroup, Guid setting, bool onAc, uint value)
    {
        if (!TryGetActiveScheme(out var scheme)) return false;
        uint error = onAc
            ? PowerWriteACValueIndex(nint.Zero, ref scheme, ref subgroup, ref setting, value)
            : PowerWriteDCValueIndex(nint.Zero, ref scheme, ref subgroup, ref setting, value);
        if (error != ERROR_SUCCESS)
        {
            log($"power policy write {setting} ({(onAc ? "AC" : "DC")}) = {value} failed: {error}");
            return false;
        }

        // The write lands in the scheme but the power manager keeps using the values
        // it cached when the scheme was activated. Re-activating is the documented
        // way to commit; it is cheap and does not disturb anything else.
        error = PowerSetActiveScheme(nint.Zero, ref scheme);
        if (error != ERROR_SUCCESS)
        {
            log($"power policy activate failed: {error}");
            return false;
        }
        return true;
    }

    private bool TryGetActiveScheme(out Guid scheme)
    {
        scheme = Guid.Empty;
        nint ptr = nint.Zero;
        try
        {
            if (PowerGetActiveScheme(nint.Zero, out ptr) != ERROR_SUCCESS || ptr == nint.Zero)
                return false;
            scheme = Marshal.PtrToStructure<Guid>(ptr);
            return true;
        }
        catch (Exception ex)
        {
            log($"power policy: active scheme unavailable ({ex.Message})");
            return false;
        }
        finally
        {
            if (ptr != nint.Zero) LocalFree(ptr);
        }
    }

    // ---------------------------------------------------------------- interop

    private const uint ERROR_SUCCESS = 0;
    private const byte BATTERY_FLAG_NO_BATTERY = 128;

    private static readonly Guid GuidSleepSubgroup =
        new("238c9fa8-0aad-41ed-83f4-97be242c8f20");
    private static readonly Guid GuidStandbyTimeout =
        new("29f6c1db-86da-48c5-9fdb-f2b67b1f44da");
    private static readonly Guid GuidButtonSubgroup =
        new("4f971e89-eebd-4455-a8de-9e59040e7347");
    private static readonly Guid GuidLidCloseAction =
        new("5ca83367-6e45-459f-a27b-476b1d01c936");

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(nint userRootPowerKey, out nint activePolicyGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveScheme(nint userRootPowerKey, ref Guid schemeGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadACValueIndex(nint rootPowerKey, ref Guid schemeGuid,
        ref Guid subGroupGuid, ref Guid settingGuid, out uint value);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadDCValueIndex(nint rootPowerKey, ref Guid schemeGuid,
        ref Guid subGroupGuid, ref Guid settingGuid, out uint value);

    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteACValueIndex(nint rootPowerKey, ref Guid schemeGuid,
        ref Guid subGroupGuid, ref Guid settingGuid, uint value);

    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteDCValueIndex(nint rootPowerKey, ref Guid schemeGuid,
        ref Guid subGroupGuid, ref Guid settingGuid, uint value);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint mem);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }
}
