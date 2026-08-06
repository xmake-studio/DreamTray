using System.Management;

namespace DreamTray.Sensors;

/// <summary>
/// Static facts about the battery pack, queried once. Used to turn a measured
/// charge rate into a time-to-full estimate, which Windows itself never reports.
/// </summary>
public static class BatteryInfo
{
    private static float? _fullChargeWh;

    /// <summary>
    /// Full-charge energy of the pack in Wh, or 0 when it cannot be determined.
    /// Read from the ACPI battery via WMI and cached — the value only changes as
    /// the pack ages, which is irrelevant within one session.
    /// </summary>
    public static float DesignCapacityWh => _fullChargeWh ??= Query();

    private static float Query()
    {
        // root\WMI BatteryFullChargedCapacity reports mWh and reflects current health,
        // which gives a better time-to-full estimate than the design capacity.
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\WMI", "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity");
            foreach (var mo in searcher.Get())
            {
                using (mo)
                {
                    var raw = mo["FullChargedCapacity"];
                    if (raw != null)
                    {
                        float mWh = Convert.ToSingle(raw);
                        if (mWh > 0) return mWh / 1000f;
                    }
                }
            }
        }
        catch { /* WMI class missing on desktops; fall through */ }
        return 0f;
    }
}
