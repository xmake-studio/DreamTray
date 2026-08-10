using System.Management;
using System.Runtime.InteropServices;

namespace DreamTray.Display;

/// <summary>
/// Brightness control for every attached display.
///
/// Two completely different mechanisms are needed and both are covered here:
/// <list type="bullet">
/// <item><b>Laptop panel</b> — the embedded backlight is driven through the ACPI
/// WMI interface (<c>WmiMonitorBrightnessMethods</c>). It does not answer DDC/CI.</item>
/// <item><b>External monitors</b> — DDC/CI over the video cable via <c>dxva2.dll</c>.
/// Slow (tens of ms per write, monitor-dependent) and rate-limited by the monitor
/// firmware, which is why writes are coalesced on a worker thread.</item>
/// </list>
///
/// Writes never block the caller: the newest value per display wins and stale
/// intermediate values from a slider drag are dropped.
/// </summary>
public sealed class BrightnessService : IDisposable
{
    private readonly Action<string> _log;
    /// <summary>Guards <see cref="_pending"/> only — never held across a hardware call.</summary>
    private readonly object _gate = new();
    /// <summary>Serialises enumeration so two scans cannot open handles at once.</summary>
    private readonly object _enumGate = new();
    /// <summary>
    /// Held while native handles are used or destroyed, so a re-scan cannot free a
    /// handle out from under the write worker. Only background threads ever take it.
    /// </summary>
    private readonly object _ddcGate = new();

    /// <summary>
    /// The published display list. Replaced wholesale, never mutated, so readers —
    /// the UI thread among them — can take it without a lock. This is the whole
    /// reason the panel opens on time: a scan that is stuck waiting on a sleeping
    /// monitor must not be able to block the click that opens the panel.
    /// </summary>
    private volatile List<Target> _targets = [];
    private readonly Dictionary<string, int> _pending = [];
    private readonly AutoResetEvent _wake = new(false);
    private Thread? _worker;
    private volatile bool _stop;

    public BrightnessService(Action<string> log) => _log = log;

    /// <summary>Internal record pairing a public target with its native handle.</summary>
    private sealed class Target
    {
        public required DisplayTarget Public { get; init; }
        public nint DdcHandle { get; init; }          // 0 for the WMI-driven panel
        /// <summary>
        /// The live <c>WmiMonitorBrightnessMethods</c> instance for the built-in
        /// panel; null for DDC monitors. Held rather than re-queried per write:
        /// a WMI query costs tens of milliseconds, which a slider drag cannot afford.
        /// </summary>
        public ManagementObject? WmiMethods { get; init; }
        public int MinDdc, MaxDdc;
    }

    // ---------------------------------------------------------------- enumeration

    /// <summary>
    /// The displays known right now. Without <paramref name="refresh"/> this never
    /// touches hardware and never blocks, so it is safe on the UI thread; the first
    /// scan is kicked off in the background by <see cref="WarmUp"/> at startup.
    /// </summary>
    public IReadOnlyList<DisplayTarget> GetDisplays(bool refresh = false)
    {
        if (refresh) Enumerate();
        else if (!_scanned) RefreshAsync(); // nothing known yet; get a scan moving
        return _targets.Select(t => t.Public).ToList();
    }

    /// <summary>True once a scan has completed, so callers know the list is real.</summary>
    private volatile bool _scanned;

    /// <summary>Start the first scan at app start, off the UI thread.</summary>
    public void WarmUp() => RefreshAsync();

    /// <summary>
    /// Re-enumerate off the calling thread and call <paramref name="onCompleted"/>
    /// (on a pool thread) once the new list is in place.
    ///
    /// Enumeration is *not* cheap and it is not bounded: the WMI query for the
    /// backlight interface costs tens of milliseconds on a warm service and far more
    /// on a cold one, and every external monitor adds a DDC/CI round trip over I2C —
    /// which a monitor that is asleep, on another input, or simply slow can stretch
    /// to seconds. Doing that on the UI thread is what made the panel appear late.
    ///
    /// Overlapping calls collapse into the one already running: the caller is asking
    /// for "current", and a second scan started 20 ms later cannot be more current
    /// than the one in flight.
    /// </summary>
    public void RefreshAsync(Action? onCompleted = null)
    {
        if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { GetDisplays(refresh: true); }
            catch (Exception ex) { _log($"display re-scan failed: {ex.Message}"); }
            finally { Interlocked.Exchange(ref _refreshing, 0); }
            onCompleted?.Invoke();
        });
    }

    private int _refreshing;

    /// <summary>Re-read the current brightness of every display (a few ms each).</summary>
    public void RefreshValues()
    {
        lock (_ddcGate)
        {
            foreach (var t in _targets)
            {
                int v = t.WmiMethods != null ? ReadWmiBrightness() : ReadDdcBrightness(t);
                if (v >= 0) t.Public.Brightness = v;
            }
        }
    }

    private void Enumerate()
    {
        lock (_enumGate) EnumerateCore();
    }

    private void EnumerateCore()
    {
        // The previous targets stay live and published until the new list is ready:
        // a scan can take seconds, and readers asking meanwhile should get the last
        // known displays rather than an empty panel.
        var previous = _targets;

        var list = new List<Target>();

        // --- laptop panel (WMI) ---
        var wmiMethods = FindWmiPanel();
        if (wmiMethods != null)
        {
            list.Add(new Target
            {
                Public = new DisplayTarget("internal", "Built-in display", DisplayKind.Internal, true)
                {
                    Brightness = ReadWmiBrightness(),
                },
                WmiMethods = wmiMethods,
            });
        }

        // --- external monitors (DDC/CI) ---
        var externals = new List<(Target Probe, string Description, int Brightness)>();
        foreach (var (handle, description) in EnumeratePhysicalMonitors())
        {
            var probe = new Target
            {
                Public = new DisplayTarget("", "", DisplayKind.External, true),
                DdcHandle = handle,
            };
            // A monitor that will not report brightness cannot be set either — most
            // often the internal panel, already covered by WMI above.
            int current = ReadDdcBrightness(probe);
            if (current < 0)
            {
                DestroyPhysicalMonitor(handle);
                continue;
            }
            externals.Add((probe, description, current));
        }

        // Numbering only makes sense once we know how many survived the probe: a
        // lone external monitor is just "External display", not "External display 1".
        for (int i = 0; i < externals.Count; i++)
        {
            var (probe, description, current) = externals[i];
            list.Add(new Target
            {
                Public = new DisplayTarget($"ddc{i + 1}",
                                           Describe(description, externals.Count == 1 ? null : i + 1),
                                           DisplayKind.External, true)
                {
                    Brightness = current,
                },
                DdcHandle = probe.DdcHandle,
                MinDdc = probe.MinDdc,
                MaxDdc = probe.MaxDdc,
            });
        }

        // Publish, then release what the old list owned — under _ddcGate so the write
        // worker cannot be part-way through a call on a handle being destroyed.
        lock (_ddcGate)
        {
            _targets = list;
            _scanned = true;
            foreach (var t in previous)
            {
                if (t.DdcHandle != 0) DestroyPhysicalMonitor(t.DdcHandle);
                t.WmiMethods?.Dispose();
            }
        }

        _log($"brightness: {list.Count} controllable display(s): " +
             string.Join(", ", list.Select(t => $"{t.Public.Id}={t.Public.Name}")));
    }

    private static string Describe(string description, int? index) =>
        string.IsNullOrWhiteSpace(description) || description == "Generic PnP Monitor"
            ? (index is null ? "External display" : $"External display {index}")
            : description;

    // ---------------------------------------------------------------- writing

    /// <summary>
    /// Queue a brightness change. Returns false only when the id is unknown; the
    /// actual hardware write happens on the worker thread a moment later.
    /// </summary>
    public bool SetBrightness(string displayId, int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        var t = _targets.FirstOrDefault(x => x.Public.Id == displayId);
        if (t == null) return false;
        t.Public.Brightness = percent; // optimistic: the UI reflects it immediately
        lock (_gate)
        {
            _pending[displayId] = percent;
            EnsureWorker();
        }
        _wake.Set();
        return true;
    }

    public void SetAll(int percent)
    {
        var targets = _targets;
        lock (_gate)
        {
            foreach (var t in targets)
            {
                t.Public.Brightness = Math.Clamp(percent, 0, 100);
                _pending[t.Public.Id] = t.Public.Brightness;
            }
            if (targets.Count > 0) EnsureWorker();
        }
        _wake.Set();
    }

    private void EnsureWorker()
    {
        if (_worker != null) return;
        _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "dreamtray-brightness" };
        _worker.Start();
    }

    private void WorkerLoop()
    {
        while (!_stop)
        {
            KeyValuePair<string, int>[] batch;
            lock (_gate)
            {
                batch = _pending.ToArray();
                _pending.Clear();
            }

            if (batch.Length == 0)
            {
                _wake.WaitOne(1000);
                continue;
            }

            foreach (var (id, value) in batch)
            {
                // Under _ddcGate for the whole write: a re-scan that lands mid-batch
                // frees the handles this loop is holding.
                lock (_ddcGate)
                {
                    var t = _targets.FirstOrDefault(x => x.Public.Id == id);
                    if (t == null) continue;

                    try
                    {
                        if (t.WmiMethods != null) WriteWmiBrightness(t.WmiMethods, value);
                        else WriteDdcBrightness(t, value);
                    }
                    catch (Exception ex) { _log($"brightness write to {id} failed: {ex.Message}"); }
                }
            }

            // Monitors dislike back-to-back DDC writes; this also coalesces a drag
            // into ~10 writes/second instead of one per mouse-move event.
            Thread.Sleep(100);
        }
    }

    // ---------------------------------------------------------------- WMI backend

    /// <summary>
    /// The built-in panel's brightness-methods object, or null on a desktop.
    /// The instance is returned whole rather than by name: rebuilding a WMI object
    /// path from an InstanceName means escaping backslashes into a quoted key, and
    /// getting that subtly wrong yields a "not found" at invoke time instead of an
    /// error you can see coming.
    /// </summary>
    private ManagementObject? FindWmiPanel()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
            foreach (var mo in searcher.Get())
                return (ManagementObject)mo; // caller owns it; disposed on re-enumerate
        }
        catch (Exception ex) { _log($"no WMI backlight interface: {ex.Message}"); }
        return null;
    }

    private int ReadWmiBrightness()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            foreach (var mo in searcher.Get())
                using (mo)
                    return Convert.ToInt32(mo["CurrentBrightness"]);
        }
        catch { /* panel may be off */ }
        return -1;
    }

    private static void WriteWmiBrightness(ManagementObject methods, int percent)
    {
        // Named parameters, not a positional array: WmiSetBrightness takes
        // (uint32 Timeout, uint8 Brightness) and the types must match exactly.
        // Timeout 0 means apply immediately and do not revert.
        using var args = methods.GetMethodParameters("WmiSetBrightness");
        args["Timeout"] = (uint)0;
        args["Brightness"] = (byte)percent;
        methods.InvokeMethod("WmiSetBrightness", args, null);
    }

    // ---------------------------------------------------------------- DDC/CI backend

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PhysicalMonitor
    {
        public nint hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szPhysicalMonitorDescription;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public int left, top, right, bottom;          // monitor rect
        public int workLeft, workTop, workRight, workBottom;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    private delegate bool MonitorEnumProc(nint hMonitor, nint hdc, nint rect, nint data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc proc, nint data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    private static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFOEX mi);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(nint hMonitor, out uint count);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(nint hMonitor, uint count, [Out] PhysicalMonitor[] monitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool DestroyPhysicalMonitor(nint hMonitor);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetMonitorBrightness(nint hMonitor, out uint min, out uint current, out uint max);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool SetMonitorBrightness(nint hMonitor, uint brightness);

    private static List<(nint Handle, string Description)> EnumeratePhysicalMonitors()
    {
        var result = new List<(nint, string)>();

        // dxva2 reports the driver's description, which for most monitors is the
        // useless "Generic PnP Monitor". The CCD API has the EDID name ("PHL 288E2"),
        // keyed by GDI device name — which is what GetMonitorInfo gives us for the
        // HMONITOR we are already walking.
        var config = DisplayConfigNames.Query();

        EnumDisplayMonitors(nint.Zero, nint.Zero, (hMonitor, _, _, _) =>
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            string friendly = GetMonitorInfo(hMonitor, ref mi) &&
                              config.TryGetValue(mi.szDevice, out var entry)
                ? entry.FriendlyName
                : "";

            if (GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out uint count) && count > 0)
            {
                var buf = new PhysicalMonitor[count];
                if (GetPhysicalMonitorsFromHMONITOR(hMonitor, count, buf))
                    foreach (var pm in buf)
                        result.Add((pm.hPhysicalMonitor,
                                    string.IsNullOrWhiteSpace(friendly)
                                        ? pm.szPhysicalMonitorDescription
                                        : friendly));
            }
            return true;
        }, nint.Zero);
        return result;
    }

    /// <summary>Current brightness as 0..100, or -1 when the monitor refuses DDC/CI.</summary>
    private int ReadDdcBrightness(Target t)
    {
        if (t.DdcHandle == 0) return -1;
        if (!GetMonitorBrightness(t.DdcHandle, out uint min, out uint cur, out uint max)) return -1;
        if (max <= min) return -1;
        t.MinDdc = (int)min; t.MaxDdc = (int)max;
        return (int)Math.Round((cur - min) * 100.0 / (max - min));
    }

    private void WriteDdcBrightness(Target t, int percent)
    {
        // Monitors rarely use 0..100 natively; map through the range they reported.
        int min = t.MaxDdc > t.MinDdc ? t.MinDdc : 0;
        int max = t.MaxDdc > t.MinDdc ? t.MaxDdc : 100;
        uint raw = (uint)Math.Round(min + (max - min) * percent / 100.0);
        if (!SetMonitorBrightness(t.DdcHandle, raw))
            _log($"DDC/CI write rejected by {t.Public.Name}");
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        _worker?.Join(1000);
        lock (_ddcGate)
        {
            foreach (var t in _targets)
            {
                if (t.DdcHandle != 0) DestroyPhysicalMonitor(t.DdcHandle);
                t.WmiMethods?.Dispose();
            }
            _targets = [];
        }
        _wake.Dispose();
    }
}
