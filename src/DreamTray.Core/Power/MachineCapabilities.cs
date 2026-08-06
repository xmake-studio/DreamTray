using System.Runtime.InteropServices;

namespace DreamTray.Power;

/// <summary>
/// The handful of machine facts that decide whether a whole piece of UI makes
/// sense at all — today: is there a battery.
///
/// This is deliberately not read off a <see cref="SystemSnapshot"/>. The sampler
/// only runs while something is subscribed, so on a closed panel (and on the very
/// first launch) the latest snapshot is null, and a widget asking "is there a
/// battery?" would have to guess. <c>GetSystemPowerStatus</c> costs nothing and
/// always answers.
/// </summary>
public static class MachineCapabilities
{
    /// <summary>False on a desktop. Every AC/battery distinction in the UI hangs off this.</summary>
    public static bool HasBattery =>
        GetSystemPowerStatus(out var status) && (status.BatteryFlag & BatteryFlagNoBattery) == 0;

    /// <summary>True while on mains power — and always true on a machine with no battery.</summary>
    public static bool IsOnAcPower =>
        !GetSystemPowerStatus(out var status) || status.ACLineStatus != 0;

    private const byte BatteryFlagNoBattery = 128;

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}
