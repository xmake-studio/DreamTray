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

    public BrightnessService(Action<string> log)
    {
        _log = log;
        _heal = new Timer(_ => QueueScan(null), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Internal record pairing a public target with its native handle.</summary>
    private sealed class Target
    {
        public required DisplayTarget Public { get; init; }
        // A physical-monitor HANDLE can be zero (observed on the MSI G273Q).
        // Track ownership separately; zero is not an absence marker here.
        public bool HasDdcHandle { get; init; }
        public nint DdcHandle { get; init; }
        /// <summary>
        /// The live <c>WmiMonitorBrightnessMethods</c> instance for the built-in
        /// panel; null for DDC monitors. Held rather than re-queried per write:
        /// a WMI query costs tens of milliseconds, which a slider drag cannot afford.
        /// </summary>
        public ManagementObject? WmiMethods { get; init; }
        public int MinDdc, MaxDdc;
        public bool UseVcp;
        public int VcpReadError, BrightnessReadError;
        public bool VcpReadSucceeded, BrightnessReadSucceeded;
        public uint VcpCurrent, VcpMaximum, BrightnessMinimum, BrightnessCurrent, BrightnessMaximum;
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

    // ------------------------------------------------------------ scan progress

    /// <summary>Cabled monitors that did not answer on the last completed scan.</summary>
    private volatile int _notResponding;
    /// <summary>When the last scan finished, in UTC ticks; 0 when none has.</summary>
    private long _lastScanTicks;

    /// <summary>
    /// What the enumeration is doing, for the UI to explain the wait. Cheap and
    /// lock-light: a widget asks this on every rebuild.
    /// </summary>
    public DisplayScanStatus Status
    {
        get
        {
            bool scanning;
            lock (_requestGate) scanning = _scanRunning;
            long ticks = Interlocked.Read(ref _lastScanTicks);
            return new DisplayScanStatus(scanning, _notResponding, _retryScheduled,
                ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero));
        }
    }

    /// <summary>
    /// Raised on a background thread after every completed scan.
    ///
    /// The panel used to learn about displays only through the callback of the scan
    /// it asked for itself, so a monitor found by the retry ladder two seconds later
    /// — the whole point of that ladder — stayed invisible until the panel was closed
    /// and opened again. Subscribers get every scan and decide for themselves whether
    /// anything they draw actually changed.
    /// </summary>
    public event Action? DisplaysChanged;

    /// <summary>Listen for completed scans; dispose to stop.</summary>
    public IDisposable SubscribeChanges(Action onChanged)
    {
        DisplaysChanged += onChanged;
        return new Unsubscriber(this, onChanged);
    }

    private sealed class Unsubscriber(BrightnessService owner, Action handler) : IDisposable
    {
        private Action? _handler = handler;
        public void Dispose()
        {
            var h = Interlocked.Exchange(ref _handler, null);
            if (h != null) owner.DisplaysChanged -= h;
        }
    }

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
    /// A request that arrives while a scan is running does not collapse into it. The
    /// scan in flight started before this call and may have already read the monitor
    /// the caller is asking about — which is exactly what happens at startup, where
    /// the warm-up scan is still working through a monitor that has not woken up when
    /// the user opens the panel. Collapsing dropped that request *and its callback*,
    /// so the widget kept showing the incomplete list even after the scan in flight
    /// found every monitor. Another pass is queued instead and the callback waits for
    /// it.
    /// </summary>
    public void RefreshAsync(Action? onCompleted = null)
    {
        // An outside trigger — startup, a panel open, a display change — restarts the
        // retry ladder: whatever prompted it is new information about the hardware.
        Interlocked.Exchange(ref _healStep, 0);
        QueueScan(onCompleted);
    }

    private readonly object _requestGate = new();
    private readonly List<Action> _waiting = [];
    private bool _scanRunning;
    private bool _rescanWanted;

    private void QueueScan(Action? onCompleted)
    {
        if (_stop) return;
        lock (_requestGate)
        {
            if (onCompleted != null) _waiting.Add(onCompleted);
            if (_scanRunning)
            {
                _rescanWanted = true;
                return;
            }
            _scanRunning = true;
        }
        ThreadPool.QueueUserWorkItem(_ => ScanLoop());
    }

    private void ScanLoop()
    {
        while (true)
        {
            // Taken before the scan, so a callback only ever fires on a list that was
            // read after its own request came in.
            Action[] batch;
            lock (_requestGate)
            {
                batch = [.. _waiting];
                _waiting.Clear();
            }

            try { Enumerate(); }
            catch (Exception ex) { _log($"display re-scan failed: {ex.Message}"); }

            // Decided before anyone is told, so that a subscriber reading Status from
            // inside its own callback is not told a scan is running when this loop is
            // about to return. Another scan starting here is harmless: it takes
            // _enumGate for itself and this loop only has callbacks left to run.
            bool more;
            lock (_requestGate)
            {
                more = _rescanWanted;
                _rescanWanted = false;
                if (!more) _scanRunning = false;
            }

            foreach (var callback in batch)
            {
                try { callback(); }
                catch (Exception ex) { _log($"display refresh callback threw: {ex.Message}"); }
            }

            // Everyone watching, not just whoever asked for this pass — a retry that
            // finally found a monitor is news to an open panel that requested nothing.
            foreach (var handler in DisplaysChanged?.GetInvocationList() ?? [])
            {
                try { ((Action)handler)(); }
                catch (Exception ex) { _log($"display change handler threw: {ex.Message}"); }
            }

            if (!more) return;
        }
    }

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
        var known = new Dictionary<string, Target>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in previous)
            if (t.HasDdcHandle) known[t.Public.Id] = t;

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
        var externals = new List<(Target Probe, Monitor Monitor, int Brightness)>();
        // Outputs that are cabled monitors — so they ought to speak DDC/CI — and did
        // not. These are what the retry ladder below is for.
        var missing = new List<string>();
        // Handles carried over from the previous list, so the sweep at the end frees
        // only what nothing points at any more.
        var carried = new HashSet<nint>();
        foreach (var monitor in EnumeratePhysicalMonitors())
        {
            // The embedded panel is already driven through WMI. Never probe it over
            // DDC/CI as well: on some laptop GPUs its physical-monitor handle does
            // answer, but the I2C traffic is routed to another output — the panel
            // then showed up a second time under its EDID name, and dragging that
            // slider changed an external monitor's brightness instead.
            if (monitor.IsInternal && wmiMethods != null)
            {
                DestroyPhysicalMonitor(monitor.Handle);
                continue;
            }

            // A monitor we already hold a working handle for is not probed again.
            //
            // Two reasons. It is the only way to stop a scan from *losing* a monitor:
            // the probe is a conversation over a shared, flaky I2C link, and a monitor
            // that has answered every write for the last hour can still fail three
            // reads in a row while it is busy with its own OSD or a mode change — and
            // that was taken as "no slider for it", dropping it from the panel until
            // some later scan happened to catch it in a better mood. It also stops
            // every re-scan from opening a second handle to a monitor whose first
            // handle is still open (the previous list is only freed once the new one
            // is built) — a state some monitors answer badly to, and one that now
            // happens on every single panel open.
            if (known.TryGetValue(MakeId(monitor), out var existing))
            {
                int value;
                // One read on the handle we already have, both to refresh the value
                // and to confirm the handle is still live.
                lock (_ddcGate) value = ReadDdcBrightness(existing);
                if (value >= 0)
                {
                    DestroyPhysicalMonitor(monitor.Handle);   // freshly opened, unused
                    carried.Add(existing.DdcHandle);
                    // Not while a slider drag is still queued: the monitor reports the
                    // value it has actually reached, which lags where the user has
                    // dragged to, and writing that back would drag the slider backwards.
                    bool queued;
                    lock (_gate) queued = _pending.ContainsKey(existing.Public.Id);
                    if (!queued) existing.Public.Brightness = value;
                    _loggedDrops.Remove(monitor.Device);
                    externals.Add((existing, monitor, existing.Public.Brightness));
                    continue;
                }
                // The handle has gone stale — the monitor was re-plugged or the driver
                // reloaded behind an unchanged device name. Fall through and probe the
                // freshly opened one; the dead handle is not in `carried`, so the sweep
                // at the end frees it.
            }

            var probe = new Target
            {
                Public = new DisplayTarget("", "", DisplayKind.External, true),
                HasDdcHandle = true,
                DdcHandle = monitor.Handle,
            };
            // A monitor that will not report brightness cannot be set either — most
            // often the internal panel, already covered by WMI above.
            int current = ProbeDdcBrightness(probe, monitor);
            if (current < 0)
            {
                DestroyPhysicalMonitor(monitor.Handle);
                if (!monitor.IsInternal) missing.Add(monitor.Device);
                continue;
            }
            // It answered, so forget that it was ever written off: if it drops out
            // again later that is news and deserves a line of its own.
            _loggedDrops.Remove(monitor.Device);
            externals.Add((probe, monitor, current));
        }

        // Numbering only makes sense once we know how many survived the probe: a
        // lone external monitor is just "External display", not "External display 1".
        for (int i = 0; i < externals.Count; i++)
        {
            var (probe, monitor, current) = externals[i];
            string id = MakeId(monitor);
            string name = Describe(monitor.Description, externals.Count == 1 ? null : i + 1);
            // A carried-over monitor keeps its DisplayTarget instance, so an optimistic
            // value set by a slider that is still being dragged survives the re-scan.
            // Only a change of name forces a new one — which is what happens when the
            // number of externals crosses one and they gain or lose their numbering.
            list.Add(new Target
            {
                Public = probe.Public.Id == id && probe.Public.Name == name
                    ? probe.Public
                    : new DisplayTarget(id, name, DisplayKind.External, true) { Brightness = current },
                DdcHandle = probe.DdcHandle,
                HasDdcHandle = true,
                MinDdc = probe.MinDdc,
                MaxDdc = probe.MaxDdc,
                UseVcp = probe.UseVcp,
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
                if (t.HasDdcHandle && !carried.Contains(t.DdcHandle)) DestroyPhysicalMonitor(t.DdcHandle);
                // The WMI panel object is re-queried every scan and never carried over,
                // so the previous one is always ours to release.
                t.WmiMethods?.Dispose();
            }
        }

        _notResponding = missing.Count;
        Interlocked.Exchange(ref _lastScanTicks, DateTimeOffset.UtcNow.UtcTicks);

        // Re-scans are frequent (every display-settings event, every panel open) and
        // almost always find the same displays; only say something when it changed.
        var summary = $"brightness: {list.Count} controllable display(s): " +
                      string.Join(", ", list.Select(t => $"{t.Public.Id}={t.Public.Name}"));
        if (summary != _lastSummary)
        {
            _lastSummary = summary;
            _log(summary);
        }

        ScheduleHealScan(missing);
    }

    // ---------------------------------------------------------------- self-healing

    /// <summary>
    /// Backoff for re-scans after a cabled monitor failed to answer. Roughly a minute
    /// of patience in total, front-loaded — a monitor that is merely slow to wake is
    /// back within seconds, and one that is switched to another input or has DDC/CI
    /// turned off in its menu is never coming back, so retrying forever only costs
    /// I2C traffic.
    /// </summary>
    private static readonly int[] HealDelaysMs = [2_000, 5_000, 10_000, 20_000, 30_000];

    private readonly Timer _heal;
    /// <summary>How far down <see cref="HealDelaysMs"/> the current run has gone.</summary>
    private int _healStep;
    /// <summary>True while another automatic retry is still booked.</summary>
    private volatile bool _retryScheduled;

    /// <summary>
    /// The scan is the only thing that ever discovers a monitor, and until now the
    /// only things that ran one were startup, a panel open and a display-settings
    /// change. So a monitor that was still dozing during the boot scan stayed missing
    /// — for hours, until something unrelated happened to poke the display config.
    /// That is the case this exists for: when a cabled output did not answer, come
    /// back and ask again on a backoff instead of waiting to be asked.
    ///
    /// Only ever called from <see cref="EnumerateCore"/>, which _enumGate serialises.
    /// </summary>
    private void ScheduleHealScan(List<string> missing)
    {
        if (missing.Count == 0)
        {
            _healStep = 0;
            _retryScheduled = false;
            _heal.Change(Timeout.Infinite, Timeout.Infinite);
            return;
        }

        int step = _healStep;
        if (step >= HealDelaysMs.Length)
        {
            // Ladder exhausted; wait for a real trigger. Said out loud through the
            // status, because from here on nothing more will happen on its own and
            // the user is otherwise left waiting for a retry that is not coming.
            _retryScheduled = false;
            return;
        }
        _healStep = step + 1;
        _retryScheduled = true;

        if (step == 0)
            _log($"brightness: {missing.Count} monitor(s) did not answer DDC/CI " +
                 $"({string.Join(", ", missing)}) — retrying for the next minute");
        // The timer runs QueueScan, not RefreshAsync: a retry must not reset the
        // ladder it is itself walking down.
        _heal.Change(HealDelaysMs[step], Timeout.Infinite);
    }

    /// <summary>Last logged display summary; only written from <see cref="EnumerateCore"/>, which _enumGate serialises.</summary>
    private string? _lastSummary;

    /// <summary>
    /// A stable id for a monitor across scans. The old scheme numbered survivors in
    /// enumeration order, so a scan where one monitor did not answer renamed the
    /// other one — "ddc1" meant a different physical monitor from one scan to the
    /// next, and a queued write could land on the wrong screen. The GDI device name
    /// belongs to the adapter output, so it survives a monitor missing a probe.
    /// </summary>
    private static string MakeId(Monitor m) =>
        string.IsNullOrEmpty(m.Device)
            ? $"ddc:{m.Handle}"
            : m.Ordinal == 0 ? $"ddc:{m.Device}" : $"ddc:{m.Device}#{m.Ordinal}";

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
    private string? _lastWmiError;

    private ManagementObject? FindWmiPanel()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
            foreach (var mo in searcher.Get())
                return (ManagementObject)mo; // caller owns it; disposed on re-enumerate
        }
        catch (Exception ex)
        {
            // A desktop has no backlight interface at all, so this throws the same
            // "not supported" on every single scan. Worth saying once, not forever.
            if (_lastWmiError != ex.Message)
            {
                _lastWmiError = ex.Message;
                _log($"no WMI backlight interface: {ex.Message}");
            }
        }
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

    private const byte BrightnessVcpCode = 0x10;

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetVCPFeatureAndVCPFeatureReply(
        nint hMonitor, byte code, nint codeType, out uint current, out uint maximum);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool SetVCPFeature(nint hMonitor, byte code, uint value);

    /// <summary>
    /// One physical monitor behind an HMONITOR, with what identifies it.
    /// <c>IsInternal</c> comes from the connector technology: an embedded panel is
    /// *expected* to ignore DDC/CI (it is driven through WMI instead), so it must not
    /// be counted as a monitor that went missing.
    /// </summary>
    private readonly record struct Monitor(nint Handle, string Description, string Device,
                                           int Ordinal, bool IsInternal);

    private static List<Monitor> EnumeratePhysicalMonitors()
    {
        var result = new List<Monitor>();

        // dxva2 reports the driver's description, which for most monitors is the
        // useless "Generic PnP Monitor". The CCD API has the EDID name ("PHL 288E2"),
        // keyed by GDI device name — which is what GetMonitorInfo gives us for the
        // HMONITOR we are already walking.
        var config = DisplayConfigNames.Query();

        EnumDisplayMonitors(nint.Zero, nint.Zero, (hMonitor, _, _, _) =>
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            string device = GetMonitorInfo(hMonitor, ref mi) ? mi.szDevice ?? "" : "";
            config.TryGetValue(device, out var entry);   // absent leaves the default entry
            string friendly = entry.FriendlyName ?? "";

            if (GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out uint count) && count > 0)
            {
                var buf = new PhysicalMonitor[count];
                if (GetPhysicalMonitorsFromHMONITOR(hMonitor, count, buf))
                    for (int i = 0; i < buf.Length; i++)
                        result.Add(new Monitor(buf[i].hPhysicalMonitor,
                                               string.IsNullOrWhiteSpace(friendly)
                                                   ? buf[i].szPhysicalMonitorDescription
                                                   : friendly,
                                               device,
                                               i,
                                               entry.IsInternal));
            }
            return true;
        }, nint.Zero);
        return result;
    }

    /// <summary>Outputs already reported as DDC/CI-deaf; only touched under _enumGate.</summary>
    private readonly HashSet<string> _loggedDrops = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Waits between the tries a monitor gets to answer the enumeration read. Growing,
    /// because the failures that matter cluster right after a power-on: a flat 150 ms
    /// gap gave a monitor 300 ms in total to wake up, which a cold boot does not
    /// reliably fit into.
    /// </summary>
    private static readonly int[] ProbeRetryDelaysMs = [150, 450];
    private static int ProbeAttempts => ProbeRetryDelaysMs.Length + 1;

    /// <summary>
    /// The enumeration-time read that decides whether a monitor is controllable at all.
    ///
    /// Unlike a value refresh this one is given several tries, because a single
    /// <c>GetMonitorBrightness</c> failure is not evidence that the monitor cannot do
    /// DDC/CI. The I2C link is shared and unreliable: a monitor that has just powered
    /// on, finished a resolution change, or is mid-conversation with its own OEM
    /// utility answers with a failure and then answers correctly 150 ms later. Giving
    /// up on the first failure is what dropped one of two working monitors — usually
    /// on the startup scan, where every monitor is at its least responsive — and left
    /// it dropped, since the scan result is what the panel shows.
    /// </summary>
    private int ProbeDdcBrightness(Target t, Monitor m)
    {
        for (int attempt = 1; ; attempt++)
        {
            int value = ReadDdcBrightness(t);
            if (value >= 0) return value;
            if (attempt >= ProbeAttempts)
            {
                // Once per output, not once per scan: on a laptop the internal panel
                // lands here on every single scan (it is driven through WMI instead),
                // and re-scans are frequent enough to drown the log.
                if (_loggedDrops.Add(m.Device))
                    _log($"brightness: {Describe(m.Description, null)} ({m.Device}) did not answer " +
                         $"DDC/CI in {ProbeAttempts} tries " +
                         $"(VCP error {t.VcpReadError}, brightness error {t.BrightnessReadError}) " +
                         $"[VCP {t.VcpReadSucceeded}: {t.VcpCurrent}/{t.VcpMaximum}; " +
                         $"high {t.BrightnessReadSucceeded}: {t.BrightnessMinimum}/" +
                         $"{t.BrightnessCurrent}/{t.BrightnessMaximum}] " +
                         "— no slider for it");
                return -1;
            }
            Thread.Sleep(ProbeRetryDelaysMs[attempt - 1]);
        }
    }

    /// <summary>Current brightness as 0..100, or -1 when the monitor refuses DDC/CI.</summary>
    private int ReadDdcBrightness(Target t)
    {
        if (!t.HasDdcHandle) return -1;
        // Query the brightness VCP code directly. Some monitors answer this command
        // while Windows' high-level brightness wrapper rejects the same display.
        bool vcpRead = GetVCPFeatureAndVCPFeatureReply(t.DdcHandle, BrightnessVcpCode,
            nint.Zero, out uint vcpCurrent, out uint vcpMax);
        t.VcpReadSucceeded = vcpRead;
        t.VcpCurrent = vcpCurrent;
        t.VcpMaximum = vcpMax;
        t.VcpReadError = vcpRead ? 0 : Marshal.GetLastWin32Error();
        if (vcpRead && vcpMax > 0 && vcpMax <= int.MaxValue)
        {
            t.MinDdc = 0;
            t.MaxDdc = (int)vcpMax;
            t.UseVcp = true;
            return (int)Math.Round(vcpCurrent * 100.0 / vcpMax);
        }

        bool brightnessRead = GetMonitorBrightness(t.DdcHandle, out uint min, out uint cur, out uint max);
        t.BrightnessReadSucceeded = brightnessRead;
        t.BrightnessMinimum = min;
        t.BrightnessCurrent = cur;
        t.BrightnessMaximum = max;
        if (!brightnessRead)
        {
            t.BrightnessReadError = Marshal.GetLastWin32Error();
            return -1;
        }
        t.BrightnessReadError = 0;
        if (max <= min) return -1;
        t.MinDdc = (int)min; t.MaxDdc = (int)max;
        t.UseVcp = false;
        return (int)Math.Round((cur - min) * 100.0 / (max - min));
    }

    private void WriteDdcBrightness(Target t, int percent)
    {
        // Monitors rarely use 0..100 natively; map through the range they reported.
        int min = t.MaxDdc > t.MinDdc ? t.MinDdc : 0;
        int max = t.MaxDdc > t.MinDdc ? t.MaxDdc : 100;
        uint raw = (uint)Math.Round(min + (max - min) * percent / 100.0);
        bool written = t.UseVcp
            ? SetVCPFeature(t.DdcHandle, BrightnessVcpCode, raw)
            : SetMonitorBrightness(t.DdcHandle, raw);
        if (!written)
            _log($"DDC/CI write rejected by {t.Public.Name}");
    }

    public void Dispose()
    {
        _stop = true;
        _heal.Change(Timeout.Infinite, Timeout.Infinite);
        _heal.Dispose();
        _wake.Set();
        _worker?.Join(1000);
        lock (_ddcGate)
        {
            foreach (var t in _targets)
            {
                if (t.HasDdcHandle) DestroyPhysicalMonitor(t.DdcHandle);
                t.WmiMethods?.Dispose();
            }
            _targets = [];
        }
        _wake.Dispose();
    }
}
