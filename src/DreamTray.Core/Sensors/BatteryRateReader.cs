using System.Runtime.InteropServices;

namespace DreamTray.Sensors;

/// <summary>
/// Signed battery power in watts, read from the power manager's battery composite.
///
/// LibreHardwareMonitor cannot be trusted for this one. Its <c>Battery</c> hardware
/// resolves the pack's battery tag (<c>IOCTL_BATTERY_QUERY_TAG</c>) once, while
/// <c>Computer.Open()</c> enumerates hardware, and caches it for the lifetime of the
/// instance. Windows invalidates that tag whenever the pack changes state — notably
/// on an AC to battery transition — and every later status query on the stale tag
/// fails, so LHM leaves its rate sensor at <c>null</c> forever. A session that was
/// started on the charger therefore reports no discharge rate at all once unplugged,
/// which the UI used to render as a confident "0 W".
///
/// This used to be a WMI query against <c>root\WMI BatteryStatus</c>, which fixed
/// that because WMI resolves the tag per query. It also ran a
/// <c>ManagementObjectSearcher.Get()</c> on the sampler thread once a second for as
/// long as the app was running, which is a COM round trip and a fresh object graph
/// per tick, for two integers.
///
/// <c>CallNtPowerInformation(SystemBatteryState)</c> answers the same question from
/// the same place Windows answers it for itself: the power manager re-resolves the
/// battery composite on every call, so it has no tag to go stale, and the whole
/// reply is 32 bytes into a stack struct. It also aggregates multiple packs, which
/// the old query had to sum by hand.
/// </summary>
public sealed class BatteryRateReader : IDisposable
{
    private const int SystemBatteryState = 5;

    /// <summary>The pack could not say. Distinct from a genuine zero.</summary>
    private const int UnknownRate = unchecked((int)0x80000000);

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemBatteryStateInfo
    {
        [MarshalAs(UnmanagedType.U1)] public bool AcOnLine;
        [MarshalAs(UnmanagedType.U1)] public bool BatteryPresent;
        [MarshalAs(UnmanagedType.U1)] public bool Charging;
        [MarshalAs(UnmanagedType.U1)] public bool Discharging;
        public byte Spare0, Spare1, Spare2;
        public byte Tag;
        public uint MaxCapacity;
        public uint RemainingCapacity;
        /// <summary>Declared ULONG, used signed: + charging, − discharging, in mW.</summary>
        public int Rate;
        public uint EstimatedTime;
        public uint DefaultAlert1;
        public uint DefaultAlert2;
    }

    [DllImport("powrprof.dll")]
    private static extern uint CallNtPowerInformation(
        int level, nint input, uint inputSize, out SystemBatteryStateInfo output, uint outputSize);

    /// <summary>
    /// Watts: positive charging, negative discharging. <c>null</c> means the rate is
    /// genuinely unknown and must not be presented as 0 W.
    /// </summary>
    public float? Read()
    {
        if (CallNtPowerInformation(
                SystemBatteryState, nint.Zero, 0,
                out var state, (uint)Marshal.SizeOf<SystemBatteryStateInfo>()) != 0)
            return null;

        if (!state.BatteryPresent) return null;
        if (state.Rate == UnknownRate) return null;

        int milliwatts = state.Rate;

        // Firmware that reports the rate as an unsigned magnitude and leaves the
        // direction to the flags is common enough to be worth catching — the same
        // habit that makes LHM encode the direction in the sensor's *name*. Trust
        // the flag over the sign when the two disagree; a pack that is discharging
        // is not gaining energy whatever the sign bit says.
        if (state.Discharging && milliwatts > 0) milliwatts = -milliwatts;
        else if (state.Charging && milliwatts < 0) milliwatts = -milliwatts;

        return milliwatts / 1000f;
    }

    public void Dispose() { }
}
