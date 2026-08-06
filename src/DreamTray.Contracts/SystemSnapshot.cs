namespace DreamTray;

/// <summary>
/// One immutable reading of every sensor DreamTray tracks. Produced by the shared
/// sampler and handed to every widget and plugin, so the whole app polls the
/// hardware once per tick no matter how many consumers there are.
///
/// Fields read 0 (or NaN-free defaults) when a sensor is unavailable — consumers
/// should treat 0 as "unknown" for temperatures and power rather than a real value.
/// </summary>
public sealed class SystemSnapshot
{
    /// <summary>When this snapshot was taken (local time).</summary>
    public DateTime Timestamp { get; init; } = DateTime.Now;

    // ---- CPU ----
    /// <summary>Per-logical-processor busy fraction, 0..1. Length = logical core count.</summary>
    public float[] ThreadLoads { get; init; } = [];
    /// <summary>Overall CPU busy fraction, 0..1.</summary>
    public float CpuLoad { get; init; }
    /// <summary>CPU die temperature, °C (0 = unavailable, needs elevation on AMD).</summary>
    public float CpuTemp { get; init; }
    /// <summary>Average / maximum effective core clock, GHz.</summary>
    public float CpuClockAvg { get; init; }
    public float CpuClockMax { get; init; }
    /// <summary>x86 core power only, W — tracks CPU activity rather than whole-APU draw.</summary>
    public float CpuPower { get; init; }
    /// <summary>Whole-APU / socket package power, W.</summary>
    public float PackagePower { get; init; }

    // ---- GPU ----
    public float GpuLoad { get; init; }        // 0..1
    public float GpuTemp { get; init; }        // °C
    /// <summary>Estimated GPU power, W. On an APU: package minus x86 cores.</summary>
    public float GpuPower { get; init; }
    public float GpuClock { get; init; }       // MHz
    public float VramUsedGb { get; init; }
    public float VramTotalGb { get; init; }

    // ---- Memory ----
    public float RamUsedGb { get; init; }
    public float RamTotalGb { get; init; }
    public float SwapUsedGb { get; init; }

    // ---- I/O ----
    public float NetDownKbs { get; init; }
    public float NetUpKbs { get; init; }
    /// <summary>System-drive active time, 0..1.</summary>
    public float Disk0Load { get; init; }
    /// <summary>Second physical drive's active time, or -1 when there is only one.</summary>
    public float Disk1Load { get; init; } = -1f;
    /// <summary>Drive letter of the second drive (e.g. "D:"), empty when absent.</summary>
    public string Disk1Label { get; init; } = "";

    // ---- Battery / power ----
    /// <summary>Battery power flow in W: positive charging, negative discharging.</summary>
    public float BatteryPower { get; init; }
    /// <summary>State of charge, 0..1 (-1 when there is no battery).</summary>
    public float BatteryLevel { get; init; } = -1f;
    public bool OnAcPower { get; init; }
    public bool BatteryPresent { get; init; }
    /// <summary>Estimated time to empty (discharging) or to full (charging); null when unknown.</summary>
    public TimeSpan? BatteryTimeRemaining { get; init; }

    /// <summary>
    /// Best available estimate of whole-system draw in W: the battery flow while on
    /// battery, otherwise 0 — a laptop on AC cannot measure wall draw, so UI should
    /// fall back to showing charge rate. See <see cref="SystemPowerKind"/>.
    /// </summary>
    public float SystemPower { get; init; }
    public SystemPowerKind SystemPowerKind { get; init; }
}

/// <summary>What <see cref="SystemSnapshot.SystemPower"/> actually measured.</summary>
public enum SystemPowerKind
{
    /// <summary>No battery telemetry available.</summary>
    Unknown,
    /// <summary>Running on battery: the value is total system draw.</summary>
    Discharging,
    /// <summary>On AC and charging: the value is the charge rate into the battery.</summary>
    Charging,
    /// <summary>On AC, battery full/idle: nothing meaningful flows through the battery.</summary>
    AcIdle,
}
