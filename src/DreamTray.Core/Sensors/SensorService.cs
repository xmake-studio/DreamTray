using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;

namespace DreamTray.Sensors;

/// <summary>
/// Reads one <see cref="SystemSnapshot"/> per call. LibreHardwareMonitor supplies
/// CPU temp/power, GPU load/clock/VRAM, RAM and battery charge rate (its kernel
/// driver needs elevation for the AMD SMU values); the PDH/NT readers in
/// <see cref="LowLevelReaders"/> cover what LHM gets wrong on this hardware.
///
/// The instance is expensive to construct (loads a driver) but cheap to keep, and
/// costs nothing while <see cref="Read"/> is not called — so
/// <see cref="SensorSampler"/> creates it lazily and disposes it when the last
/// subscriber goes away.
/// </summary>
public sealed class SensorService : IDisposable
{
    private readonly Computer _computer;
    private readonly UpdateVisitor _visitor = new();
    private readonly CpuLoadReader _cpuLoad = new();
    private readonly CpuFreqReader _cpuFreq = new();
    private readonly DiskLoadReader _diskLoad = new();
    private readonly BatteryRateReader _battRate = new();
    private readonly PagefileReader _pagefile = new();
    private readonly float[] _threads;

    public SensorService()
    {
        _threads = new float[_cpuLoad.CoreCount];
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsNetworkEnabled = true,
            IsStorageEnabled = false, // disk load comes from PDH; LHM's storage polling is costly
            IsBatteryEnabled = true,
        };
        _computer.Open();
    }

    public SystemSnapshot Read()
    {
        _computer.Accept(_visitor); // refresh every hardware + sub-hardware

        var b = new Builder();
        b.CpuLoad = _cpuLoad.Read(_threads);
        b.Threads = (float[])_threads.Clone();
        // LHM reports NaN per-core clocks on several AMD mobile parts; the Win32
        // power API answers everywhere, so it is used unconditionally.
        _cpuFreq.Read(out b.ClockAvg, out b.ClockMax);
        // One PDH collection, two views derived from it -- see Collect()'s doc
        // comment for why calling it twice here corrupted D:/E: to a false 100%.
        var diskSample = _diskLoad.Collect();
        DiskLoadReader.SystemDriveAndOther(diskSample, out b.Disk0, out b.Disk1, out b.Disk1Label);
        b.DiskAll = DiskLoadReader.ByLoadDescending(diskSample);
        // LHM exposes commit charge but not pagefile usage, and the two are far apart.
        b.Swap = _pagefile.Read();

        foreach (var hw in _computer.Hardware)
        {
            switch (hw.HardwareType)
            {
                case HardwareType.Cpu: ReadCpu(hw, b); break;
                case HardwareType.GpuAmd:
                case HardwareType.GpuNvidia:
                case HardwareType.GpuIntel: ReadGpu(hw, b); break;
                case HardwareType.Memory: ReadMemory(hw, b); break;
                case HardwareType.Network: ReadNetwork(hw, b); break;
                case HardwareType.Battery: ReadBattery(hw, b); break;
            }
        }

        // Preferred over whatever LHM produced above: LHM stops answering after an
        // AC/battery transition. See BatteryRateReader.
        b.BattW = _battRate.Read() ?? b.BattW;

        ReadPowerStatus(b);
        return b.Build();
    }

    /// <summary>Mutable scratch object — <see cref="SystemSnapshot"/> itself is init-only.</summary>
    private sealed class Builder
    {
        public float[] Threads = [];
        public float CpuLoad, CpuTemp, ClockAvg, ClockMax, CpuW, PackageW;
        public float GpuLoad, GpuTemp, GpuW, GpuClock, VramUsed, VramTotal;
        public float RamUsed, RamTotal, Swap;
        public float NetDown, NetUp;
        public float Disk0, Disk1 = -1f;
        public string Disk1Label = "";
        public List<(string Label, float Load)> DiskAll = [];
        /// <summary>Null until something actually measures a rate — never assume 0 W.</summary>
        public float? BattW;
        public float BattLevel = -1f;
        public bool OnAc, HasBattery;
        public TimeSpan? Remaining;

        public SystemSnapshot Build()
        {
            // A laptop cannot measure wall draw. On battery the pack current *is* the
            // whole-system draw; on AC the honest thing to report is the charge rate.
            SystemPowerKind kind;
            float power;
            if (!HasBattery) { kind = SystemPowerKind.Unknown; power = 0; }
            // Nothing measured a rate this tick. Saying "0 W" would be a lie that
            // reads exactly like a real idle reading, so report it as unknown.
            else if (BattW is not float w) { kind = SystemPowerKind.Unknown; power = 0; }
            else if (!OnAc) { kind = SystemPowerKind.Discharging; power = MathF.Abs(w); }
            else if (w > 0.5f) { kind = SystemPowerKind.Charging; power = w; }
            else { kind = SystemPowerKind.AcIdle; power = 0; }

            return new SystemSnapshot
            {
                ThreadLoads = Threads,
                CpuLoad = CpuLoad,
                CpuTemp = CpuTemp,
                CpuClockAvg = ClockAvg,
                CpuClockMax = ClockMax,
                CpuPower = CpuW,
                PackagePower = PackageW,
                GpuLoad = GpuLoad,
                GpuTemp = GpuTemp,
                GpuPower = GpuW,
                GpuClock = GpuClock,
                VramUsedGb = VramUsed,
                VramTotalGb = VramTotal,
                RamUsedGb = RamUsed,
                RamTotalGb = RamTotal,
                SwapUsedGb = Swap,
                NetDownKbs = NetDown,
                NetUpKbs = NetUp,
                Disk0Load = Disk0,
                Disk1Load = Disk1,
                Disk1Label = Disk1Label,
                DiskLoads = DiskAll.Select(d => d.Load).ToArray(),
                DiskLabels = DiskAll.Select(d => d.Label).ToArray(),
                BatteryPower = BattW ?? 0f,
                BatteryLevel = BattLevel,
                OnAcPower = OnAc,
                BatteryPresent = HasBattery,
                BatteryTimeRemaining = Remaining,
                SystemPower = power,
                SystemPowerKind = kind,
            };
        }
    }

    private static void ReadCpu(IHardware hw, Builder b)
    {
        float bestTemp = 0; bool haveTemp = false;
        float package = 0, coreSum = 0;
        foreach (var sen in hw.Sensors)
        {
            if (!sen.Value.HasValue || float.IsNaN(sen.Value.Value)) continue;
            float v = sen.Value.Value;
            switch (sen.SensorType)
            {
                case SensorType.Temperature:
                    // Prefer the die/package sensor; else keep the hottest reading.
                    bool preferred = sen.Name.Contains("Tdie") || sen.Name.Contains("Tctl")
                                     || sen.Name.Contains("Package") || sen.Name.Contains("Core (");
                    if (preferred) { bestTemp = v; haveTemp = true; }
                    else if (!haveTemp && v > bestTemp) bestTemp = v;
                    break;
                case SensorType.Power:
                    if (sen.Name.Contains("Package")) package = v;       // whole APU/CPU
                    else if (sen.Name.Contains("Core #")) coreSum += v;  // AMD: per-core (SMU), sum needed
                    else if (sen.Name.Contains("Cores")) coreSum = v;    // Intel: already the x86-core aggregate
                    break;
            }
        }
        b.CpuTemp = bestTemp;
        b.PackageW = package;
        // CPU power = the x86 cores only, so it reflects CPU activity rather than
        // GPU/SoC draw.
        b.CpuW = coreSum;
        // Estimated iGPU power = APU package minus the x86 cores. This is really the
        // uncore+GPU+SoC remainder, but unlike LHM's mislabeled "GPU Core" power it
        // does NOT track CPU load (both grow together and cancel), so it's stable.
        // ReadGpu overwrites this for a discrete GPU, never for an AMD iGPU.
        b.GpuW = MathF.Max(0, package - coreSum);
    }

    private static void ReadGpu(IHardware hw, Builder b)
    {
        float gpuTemp = 0; int tempRank = -1; // prefer Hot Spot > Core > anything else
        float gpuPower = 0; int powerRank = -1; // prefer Package > anything else
        foreach (var sen in hw.Sensors)
        {
            if (!sen.Value.HasValue || float.IsNaN(sen.Value.Value)) continue;
            float v = sen.Value.Value;
            if (sen.SensorType == SensorType.Load && sen.Name.Contains("GPU Core"))
                b.GpuLoad = v / 100f;
            else if (sen.SensorType == SensorType.Clock && sen.Name.Contains("GPU Core"))
                b.GpuClock = v; // MHz
            else if (sen.SensorType == SensorType.SmallData && sen.Name.Contains("Memory Used")
                     && sen.Name.Contains("GPU"))
                b.VramUsed = v / 1024f; // MB -> GB
            else if (sen.SensorType == SensorType.SmallData && sen.Name.Contains("Memory Total")
                     && sen.Name.Contains("GPU"))
                b.VramTotal = v / 1024f;
            else if (sen.SensorType == SensorType.Temperature)
            {
                int rank = sen.Name.Contains("Hot Spot") ? 2 : sen.Name.Contains("Core") ? 1 : 0;
                if (rank > tempRank) { tempRank = rank; gpuTemp = v; }
            }
            // Note this is SensorType.Power, not the like-named "GPU Power" under
            // SensorType.Load -- that one is a percentage of the card's TDP limit,
            // not watts, and averaging the two together would be nonsense.
            else if (sen.SensorType == SensorType.Power)
            {
                int rank = sen.Name.Contains("Package") ? 1 : 0;
                if (rank > powerRank) { powerRank = rank; gpuPower = v; }
            }
        }
        b.GpuTemp = gpuTemp;
        // An AMD iGPU sits inside the package ReadCpu already measured, and ReadCpu
        // has set GpuW to package-minus-cores. ADL's power sensors on an APU are not
        // the graphics block alone -- they track CPU load too -- so letting them
        // overwrite the estimate showed e.g. 40 W cores + 30 W GPU inside a 40 W APU.
        if (!IsAmdIntegrated(hw)) b.GpuW = gpuPower;
    }

    /// <summary>
    /// LHM exposes no integrated/discrete flag for AMD, so this goes by the ADL
    /// adapter name: APU graphics are named "... Graphics" ("AMD Radeon 780M
    /// Graphics", "AMD Radeon(TM) Graphics", "... Vega 8 Graphics"), discrete cards
    /// "AMD Radeon RX ...". The RX exclusion also covers Kaby Lake-G's on-package
    /// "Radeon RX Vega M ... Graphics", which is a separate die with its own power.
    /// </summary>
    private static bool IsAmdIntegrated(IHardware hw) =>
        hw.HardwareType == HardwareType.GpuAmd
        && hw.Name.TrimEnd().EndsWith("Graphics", StringComparison.OrdinalIgnoreCase)
        && !hw.Name.Contains(" RX ", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Called once per Memory node. LHM 0.9.6 split memory into *two* nodes,
    /// "Total Memory" (physical) and "Virtual Memory" (commit charge), which both
    /// expose sensors named plainly "Memory Used"/"Memory Available" — up to 0.9.4
    /// there was a single node and the commit sensors carried longer "Virtual
    /// Memory ..." names. Keying off the sensor name alone would therefore let the
    /// commit node overwrite physical RAM, so the node name decides instead.
    ///
    /// A newer LHM also reports one further Memory node per installed DIMM, named
    /// for its manufacturer/part number (e.g. "Kingston - KF3600C18D4/16GX (#1)")
    /// and carrying only SPD timing data — no Used/Available sensors. Excluding
    /// just "Virtual" let those through too, and each one silently zeroed the
    /// reading the "Total Memory" node had just set. Matching the physical node's
    /// name directly, instead of excluding everything else, is what keeps this to
    /// the one node that actually has something to report.
    /// </summary>
    private static void ReadMemory(IHardware hw, Builder b)
    {
        if (!hw.Name.Equals("Total Memory", StringComparison.OrdinalIgnoreCase)) return;

        float used = 0, avail = 0;
        foreach (var sen in hw.Sensors)
        {
            if (sen.SensorType != SensorType.Data || !sen.Value.HasValue) continue;
            float v = sen.Value.Value; // GB
            if (sen.Name == "Memory Used") used = v;
            else if (sen.Name == "Memory Available") avail = v;
        }
        b.RamUsed = used;
        b.RamTotal = used + avail;
    }

    private static void ReadNetwork(IHardware hw, Builder b)
    {
        foreach (var sen in hw.Sensors)
        {
            if (sen.SensorType != SensorType.Throughput || !sen.Value.HasValue) continue;
            float kb = sen.Value.Value / 1024f; // B/s -> KB/s
            if (sen.Name.Contains("Download")) b.NetDown += kb;
            else if (sen.Name.Contains("Upload")) b.NetUp += kb;
        }
    }

    private static void ReadBattery(IHardware hw, Builder b)
    {
        // There is one rate sensor, and it is not signed: LHM stores the magnitude
        // and encodes the direction by rewriting the sensor's *name* on every poll —
        // "Charge Rate", "Discharge Rate", or "Charge/Discharge Rate" while the rate
        // is exactly zero. Test for discharge first, since the zero-rate name also
        // ends in "Discharge Rate" (and zero negated is still zero).
        foreach (var sen in hw.Sensors)
        {
            if (sen.SensorType != SensorType.Power) continue;
            if (sen.Value is not float w || float.IsNaN(w)) continue;
            if (sen.Name.Contains("Discharge Rate")) b.BattW = -MathF.Abs(w);
            else if (sen.Name.Contains("Charge Rate")) b.BattW = MathF.Abs(w);
        }
    }

    // ---- AC line + charge level + remaining time (no elevation needed) ----

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;       // 0 offline, 1 online, 255 unknown
        public byte BatteryFlag;        // 128 = no system battery
        public byte BatteryLifePercent; // 0..100, 255 unknown
        public byte SystemStatusFlag;
        public int BatteryLifeTime;     // seconds to empty, -1 unknown
        public int BatteryFullLifeTime; // seconds, -1 unknown
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    private static void ReadPowerStatus(Builder b)
    {
        if (!GetSystemPowerStatus(out var st)) return;

        b.HasBattery = (st.BatteryFlag & 128) == 0;
        b.OnAc = st.ACLineStatus == 1;
        if (st.BatteryLifePercent != 255) b.BattLevel = st.BatteryLifePercent / 100f;

        if (!b.HasBattery) return;

        if (!b.OnAc && st.BatteryLifeTime > 0)
        {
            b.Remaining = TimeSpan.FromSeconds(st.BatteryLifeTime);
        }
        else if (b.OnAc && b.BattW > 0.5f && b.BattLevel is > 0 and < 0.999f)
        {
            // Windows does not estimate time-to-full, so derive it from the measured
            // charge rate. Needs the pack's design energy, which LHM does not expose
            // per-Wh; approximate from the remaining fraction and observed rate over
            // a nominal 50 Wh pack only if we know nothing better.
            float remainingFraction = 1f - b.BattLevel;
            float packWh = BatteryInfo.DesignCapacityWh;
            if (packWh > 0)
                b.Remaining = TimeSpan.FromHours(packWh * remainingFraction / b.BattW.Value);
        }
    }

    /// <summary>Print every hardware node and sensor for diagnostics (`--dump`).</summary>
    public string Dump()
    {
        _computer.Accept(_visitor);
        var sb = new System.Text.StringBuilder();
        foreach (var hw in _computer.Hardware)
        {
            sb.AppendLine($"# {hw.HardwareType}  {hw.Name}");
            foreach (var sen in hw.Sensors.OrderBy(s => s.SensorType))
                sb.AppendLine($"    [{sen.SensorType,-12}] {sen.Name,-28} = {sen.Value}");
            foreach (var sub in hw.SubHardware)
            {
                sb.AppendLine($"  ## {sub.HardwareType}  {sub.Name}");
                foreach (var sen in sub.Sensors.OrderBy(s => s.SensorType))
                    sb.AppendLine($"      [{sen.SensorType,-12}] {sen.Name,-28} = {sen.Value}");
            }
        }
        return sb.ToString();
    }

    public void Dispose()
    {
        _computer.Close();
        _cpuFreq.Dispose();
        _diskLoad.Dispose();
        _battRate.Dispose();
        _pagefile.Dispose();
    }

    /// <summary>
    /// Updates every hardware node, and drops the sample series LHM records behind
    /// each sensor as it goes.
    ///
    /// That series exists so a monitoring UI can draw a graph without sampling for
    /// itself. Nothing here draws graphs — <see cref="SensorService"/> reads
    /// <c>ISensor.Value</c> once and throws the sample away. Left at its default the
    /// series grows for the life of the process, across the hundred-odd sensors an
    /// APU laptop with an iGPU exposes, at whatever rate the fastest subscriber
    /// asked for. That is the app sitting in the tray all afternoon and ending up
    /// a few hundred MB heavy, and it is retention rather than a leak: nothing ever
    /// asked for the history back.
    ///
    /// <c>ISensor.Value</c> is a separate field from the series, so clearing it
    /// costs nothing this class reads.
    /// </summary>
    private sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer c) => c.Traverse(this);
        public void VisitHardware(IHardware h)
        {
            h.Update();
            foreach (var sen in h.Sensors)
            {
                // Both, deliberately. The window is what Sensor's own append path
                // consults, and it has to be re-applied here rather than once at
                // construction because sensors appear later than the Computer does —
                // a GPU coming out of runtime suspend, a pack re-enumerated across an
                // AC transition. ClearValues is the guarantee that does not depend on
                // how that path chooses to read a zero window.
                sen.ValuesTimeWindow = TimeSpan.Zero;
                sen.ClearValues();
            }
            foreach (var sub in h.SubHardware) sub.Accept(this);
        }
        public void VisitSensor(ISensor s) { }
        public void VisitParameter(IParameter p) { }
    }
}
