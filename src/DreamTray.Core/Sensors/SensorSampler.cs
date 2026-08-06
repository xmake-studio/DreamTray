using System.Windows.Threading;

namespace DreamTray.Sensors;

/// <summary>
/// The single place the hardware is polled. Every widget and plugin subscribes
/// here instead of opening its own LibreHardwareMonitor instance, so N consumers
/// still cost one sensor read per tick.
///
/// Resource behaviour, which is the whole point of this class:
/// <list type="bullet">
/// <item>With no subscribers the worker thread blocks on an event and the
/// <see cref="SensorService"/> (and its kernel driver) is released entirely —
/// idle cost is zero, not "one cheap timer".</item>
/// <item>The tick period is the fastest interval any subscriber asked for, so a
/// closed panel with only a 5 s background rule polls at 5 s.</item>
/// <item>Callbacks are marshalled to the UI dispatcher, so widgets can touch
/// their controls directly.</item>
/// </list>
/// </summary>
public sealed class SensorSampler : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Action<string> _log;
    private readonly object _gate = new();
    private readonly List<Subscription> _subs = [];
    private readonly AutoResetEvent _wake = new(false);

    private SensorService? _service;
    private Thread? _thread;
    private volatile bool _stop;
    private volatile int _periodMs = 1000;

    public SystemSnapshot? Latest { get; private set; }

    /// <summary>Raised on the UI thread after every sample, for app-level consumers.</summary>
    public event Action<SystemSnapshot>? Sampled;

    public SensorSampler(Dispatcher dispatcher, Action<string> log)
    {
        _dispatcher = dispatcher;
        _log = log;
    }

    /// <summary>
    /// Start receiving samples at no slower than <paramref name="interval"/>.
    /// Dispose the handle to stop; the sampler shuts down when the last one goes.
    /// </summary>
    public IDisposable Subscribe(TimeSpan interval, Action<SystemSnapshot> onSample)
    {
        var sub = new Subscription(this, Math.Max(250, (int)interval.TotalMilliseconds), onSample);
        lock (_gate)
        {
            _subs.Add(sub);
            Recompute();
            if (_thread == null)
            {
                _thread = new Thread(Run) { IsBackground = true, Name = "dreamtray-sensors" };
                _thread.Start();
            }
        }
        _wake.Set();
        return sub;
    }

    private void Remove(Subscription sub)
    {
        lock (_gate)
        {
            if (!_subs.Remove(sub)) return;
            Recompute();
        }
        _wake.Set();
    }

    /// <summary>Tick at the fastest requested rate; callers slower than that are decimated.</summary>
    private void Recompute()
    {
        _periodMs = _subs.Count == 0 ? 1000 : _subs.Min(s => s.PeriodMs);
    }

    private void Run()
    {
        while (!_stop)
        {
            bool idle;
            lock (_gate) idle = _subs.Count == 0;

            if (idle)
            {
                // Nobody is listening: drop the driver and sleep until someone subscribes.
                ReleaseService();
                _wake.WaitOne();
                continue;
            }

            SystemSnapshot? snap = null;
            try
            {
                if (_service == null)
                {
                    _service = new SensorService();
                    _service.Read(); // prime the delta-based readers
                    // The first real sample needs a gap after priming; fall through to
                    // the wait below and read on the next pass.
                    _wake.WaitOne(_periodMs);
                    if (_stop) break;
                }
                snap = _service.Read();
            }
            catch (Exception ex)
            {
                _log($"sensor read failed: {ex.Message}");
                ReleaseService();
                _wake.WaitOne(2000);
                continue;
            }

            Latest = snap;
            Dispatch(snap);
            _wake.WaitOne(_periodMs);
        }
        ReleaseService();
    }

    private void Dispatch(SystemSnapshot snap)
    {
        // Background priority: sensor updates must never delay input or animation.
        _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            Subscription[] subs;
            lock (_gate) subs = _subs.ToArray();

            long now = Environment.TickCount64;
            foreach (var s in subs)
            {
                if (now - s.LastFiredTicks + 50 < s.PeriodMs) continue; // 50 ms slack for timer jitter
                s.LastFiredTicks = now;
                try { s.Callback(snap); }
                catch (Exception ex) { _log($"sensor subscriber threw: {ex}"); }
            }

            try { Sampled?.Invoke(snap); }
            catch (Exception ex) { _log($"sensor event threw: {ex}"); }
        });
    }

    private void ReleaseService()
    {
        if (_service == null) return;
        try { _service.Dispose(); } catch { /* driver unload is best-effort */ }
        _service = null;
        Latest = null;
    }

    /// <summary>One-shot read outside the sampling loop (used by `--dump`).</summary>
    public static string DumpSensors()
    {
        using var svc = new SensorService();
        svc.Read();
        Thread.Sleep(1000);
        return svc.Dump();
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        _thread?.Join(2000);
        _wake.Dispose();
    }

    private sealed class Subscription(SensorSampler owner, int periodMs, Action<SystemSnapshot> callback)
        : IDisposable
    {
        public int PeriodMs { get; } = periodMs;
        public Action<SystemSnapshot> Callback { get; } = callback;
        public long LastFiredTicks;

        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            owner.Remove(this);
        }
    }
}
