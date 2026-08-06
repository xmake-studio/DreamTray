using System.Management;

namespace DreamTray.Sensors;

/// <summary>
/// Signed battery power in watts, read straight from the ACPI battery.
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
/// WMI resolves the tag per query, so it keeps answering across transitions.
/// </summary>
public sealed class BatteryRateReader : IDisposable
{
    private ManagementObjectSearcher? _searcher;

    /// <summary>
    /// Watts: positive charging, negative discharging. <c>null</c> means the rate is
    /// genuinely unknown and must not be presented as 0 W.
    /// </summary>
    public float? Read()
    {
        try
        {
            // root\WMI BatteryStatus reports the two rates as unsigned mW, with the
            // direction implied by which one is non-zero.
            _searcher ??= new ManagementObjectSearcher(
                @"root\WMI", "SELECT ChargeRate, DischargeRate FROM BatteryStatus");

            float milliwatts = 0f;
            bool answered = false;

            using var results = _searcher.Get();
            foreach (var mo in results)
            {
                using (mo)
                {
                    // Summed, so a machine with two packs reports its combined flow.
                    milliwatts += ToFloat(mo["ChargeRate"]) - ToFloat(mo["DischargeRate"]);
                    answered = true;
                }
            }

            return answered ? milliwatts / 1000f : null;
        }
        catch
        {
            // The class is absent on desktops, and querying can throw transiently
            // while the battery is being re-enumerated. Drop the searcher so the
            // next call rebuilds it rather than latching the failure.
            _searcher?.Dispose();
            _searcher = null;
            return null;
        }
    }

    private static float ToFloat(object? raw) => raw is null ? 0f : Convert.ToSingle(raw);

    public void Dispose()
    {
        _searcher?.Dispose();
        _searcher = null;
    }
}
