using System.Windows;
using System.Windows.Controls;

namespace DreamTray.Plugins.CyberVfd;

/// <summary>
/// Streams system metrics to the CyberVFD (ESP32-C3 + GP1247AI vacuum-fluorescent
/// panel) over USB serial.
///
/// This replaces the standalone CyberVFD agent: it reads from DreamTray's shared
/// sampler, so the machine runs one hardware-monitoring stack and one tray icon
/// instead of two of each. The wire protocol and the discovery handshake are
/// unchanged, so existing firmware works as-is.
///
/// Serial I/O runs on its own thread — a wedged COM port must not stall the UI.
/// </summary>
public sealed class CyberVfdPlugin : DreamPluginBase
{
    private readonly object _gate = new();
    private readonly Queue<string> _outbox = new();
    private readonly SerialLink _link = new();
    private readonly AutoResetEvent _wake = new(false);

    private IDisposable? _subscription;
    private Thread? _worker;
    private volatile bool _stop;
    private volatile bool _reconnect;
    /// <summary>
    /// Bumped by every <see cref="Stop"/>. A worker whose generation is stale exits
    /// at the next check, so a <see cref="Start"/> that follows a join timeout can
    /// never leave two link threads fighting over the same port.
    /// </summary>
    private volatile int _generation;

    /// <summary>
    /// The most recent sample, *kept* rather than consumed: the data frame is what
    /// holds the tube on, so the worker re-sends the last one on its own clock when
    /// the host sampler stalls. See <see cref="Run"/>.
    /// </summary>
    private SystemSnapshot? _latest;

    private string _status = "stopped";

    public override string Id => "cybervfd";
    public override string Name => "CyberVFD display";
    public override string Description => "Streams system metrics to the CyberVFD serial panel.";
    public override string Version => "1.0";

    // ---- persisted settings ----

    private string PortMode
    {
        get => Host.Storage.Get("portMode", "Auto");   // "Auto" | "Manual"
        set => Host.Storage.Set("portMode", value);
    }

    private string ManualPort
    {
        get => Host.Storage.Get("manualPort", "");
        set => Host.Storage.Set("manualPort", value);
    }

    private int Brightness
    {
        get => Host.Storage.Get("brightness", 255);    // panel dimming register, 0..255
        set => Host.Storage.Set("brightness", value);
    }

    private bool Backlight
    {
        get => Host.Storage.Get("backlight", true);
        set => Host.Storage.Set("backlight", value);
    }

    private bool DevicePower
    {
        get => Host.Storage.Get("devicePower", true);  // master on/off (HV + backlight)
        set => Host.Storage.Set("devicePower", value);
    }

    /// <summary>Connection state, for the settings page and the widget.</summary>
    public string Status
    {
        get { lock (_gate) return _status; }
        private set { lock (_gate) _status = value; }
    }

    /// <summary>Raised (UI thread) when <see cref="Status"/> changes.</summary>
    public event Action? StatusChanged;

    // ---------------------------------------------------------------- lifecycle

    public override void Start()
    {
        if (_worker != null) return;

        _stop = false;
        int generation = _generation;
        _worker = new Thread(() => Run(generation)) { IsBackground = true, Name = "cybervfd-link" };
        _worker.Start();

        // One sample per second is what the firmware's display refresh expects. It is
        // *not* what keeps the panel powered — the worker's own clock does that — so a
        // stall anywhere upstream of this callback no longer blanks the tube.
        _subscription = Host.SubscribeSensors(TimeSpan.FromSeconds(1), snapshot =>
        {
            lock (_gate) _latest = snapshot;
            _wake.Set();
        });

        QueueFullState();
        Host.Log("cybervfd: started");
    }

    public override void Stop()
    {
        _subscription?.Dispose();
        _subscription = null;

        _stop = true;
        // Retire this worker generation before the join. A port scan can be
        // mid-handshake, so the thread may outlive the wait — but it will see the
        // bumped generation and exit without touching the link, instead of running on
        // alongside the one a later Start() creates.
        _generation++;
        _wake.Set();
        _worker?.Join(3000);
        _worker = null;

        _link.Disconnect();
        SetStatus("stopped");
        Host.Log("cybervfd: stopped");
    }

    public override void Dispose()
    {
        Stop();
        _link.Dispose();
        // _wake is deliberately not disposed: if the worker did not exit within the
        // join above it is still blocked on this handle, and disposing it would
        // throw ObjectDisposedException on that thread. One waitable handle left to
        // the finalizer is the cheaper trade.
        base.Dispose();
    }

    // ---------------------------------------------------------------- worker

    /// <summary>How often a data frame goes out. The firmware's watchdog is 5 s.</summary>
    private const int FramePeriodMs = 1000;

    /// <summary>
    /// Retry delays for a failed connect attempt. A scan opens every COM port in
    /// turn, and opening the panel's port makes the device re-enumerate — so
    /// retrying on a flat 2 s tick during a hot plug keeps knocking the device over
    /// just as it finishes coming up, which is what turned a single missed handshake
    /// into an endless on/off cycle. Backing off gives it room to settle.
    /// </summary>
    private static readonly int[] RetryDelaysMs = [1000, 2000, 5000, 10000];

    private void Run(int generation)
    {
        long nextFrameAt = 0;
        int attempt = 0;

        while (!_stop && _generation == generation)
        {
            if (_reconnect) { _link.Disconnect(); _reconnect = false; }

            if (!_link.Connected)
            {
                if (!Reconnect(attempt)) { attempt++; continue; }
                attempt = 0;
                // Re-arm the cadence: the device has just booted or re-enumerated and
                // wants a frame now, not on whatever the old schedule said.
                nextFrameAt = 0;
            }

            // Control frames first (power/backlight/brightness), then the data frame.
            string[] frames;
            lock (_gate)
            {
                frames = _outbox.ToArray();
                _outbox.Clear();
            }

            bool ok = frames.All(_link.Send);

            long now = Environment.TickCount64;
            if (ok && now >= nextFrameAt)
            {
                // With the panel forced off there is nothing to draw, so skip the data
                // frame; the firmware powers the tube down after 5 s without one.
                //
                // Otherwise a frame goes out every second whether or not a new sample
                // arrived. Sensor samples reach this plugin through the host sampler
                // and the UI dispatcher, and a stall in either — a sensor read that
                // throws and costs a full SensorService rebuild, a busy UI thread
                // during a panel animation — used to mean no frame for longer than the
                // watchdog allows, so the tube blanked and lit again on the next
                // sample. Repeating the last sample is stale by at most a second or
                // two; going dark is not a better answer.
                SystemSnapshot? snapshot;
                lock (_gate) snapshot = _latest;

                if (DevicePower && snapshot != null)
                    ok = _link.Send(VfdFrame.Build(snapshot, DateTime.Now));

                nextFrameAt = now + FramePeriodMs;
            }

            if (!ok)
            {
                // Re-queue undelivered control frames so they survive the reconnect.
                lock (_gate) foreach (var f in frames) _outbox.Enqueue(f);
                SetStatus("link lost");
                Host.Log($"cybervfd: link lost on {_link.PortName ?? "?"}");
                _link.Disconnect();
                continue;
            }

            SetStatus($"connected on {_link.PortName}");

            // Short waits so a control frame goes out promptly, with the next data
            // frame as the deadline.
            int wait = (int)Math.Clamp(nextFrameAt - Environment.TickCount64, 1, FramePeriodMs);
            _wake.WaitOne(wait);
        }

        // A stale generation (Stop timed out on the join and gave up on this thread)
        // must not leave the port held open behind the plugin's back.
        if (_generation != generation) _link.Disconnect();
    }

    /// <summary>
    /// One connect attempt, then a backoff wait. <paramref name="attempt"/> is the
    /// number of consecutive failures so far.
    /// </summary>
    private bool Reconnect(int attempt)
    {
        bool manual = PortMode == "Manual";
        string port = ManualPort;

        bool ok = manual
            ? !string.IsNullOrEmpty(port) && _link.TryConnectTo(port)
            : _link.TryConnect();

        if (ok)
        {
            // The firmware may have just reset, so push the full current state.
            QueueFullState();
            SetStatus($"connected on {_link.PortName}");
            Host.Log($"cybervfd: connected on {_link.PortName}");
            return true;
        }

        SetStatus(manual ? $"waiting for {port}" : "searching for the device…");
        _wake.WaitOne(RetryDelaysMs[Math.Min(attempt, RetryDelaysMs.Length - 1)]);
        return false;
    }

    private void QueueFullState()
    {
        lock (_gate)
        {
            _outbox.Enqueue(VfdFrame.Brightness(Brightness));
            _outbox.Enqueue(VfdFrame.Backlight(Backlight));
            _outbox.Enqueue(VfdFrame.Power(DevicePower));
        }
        _wake.Set();
    }

    private void Enqueue(string frame)
    {
        lock (_gate) _outbox.Enqueue(frame);
        _wake.Set();
    }

    private void SetStatus(string status)
    {
        lock (_gate)
        {
            if (_status == status) return;
            _status = status;
        }
        // Marshal to the UI thread: the settings page binds straight to this.
        Application.Current?.Dispatcher.BeginInvoke(() => StatusChanged?.Invoke());
    }

    // ---------------------------------------------------------------- controls

    public void SetDevicePower(bool on)
    {
        DevicePower = on;
        Enqueue(VfdFrame.Power(on));
    }

    public void SetBacklight(bool on)
    {
        Backlight = on;
        Enqueue(VfdFrame.Backlight(on));
    }

    public void SetBrightness(int value)
    {
        Brightness = Math.Clamp(value, 0, 255);
        Enqueue(VfdFrame.Brightness(Brightness));
    }

    public void UsePortAuto()
    {
        PortMode = "Auto";
        ManualPort = "";
        _reconnect = true;
        _wake.Set();
    }

    public void UsePort(string port)
    {
        PortMode = "Manual";
        ManualPort = port;
        _reconnect = true;
        _wake.Set();
    }

    // ---------------------------------------------------------------- UI

    public override IEnumerable<IWidgetFactory> Widgets => [new CyberVfdWidgetFactory(this)];

    public override FrameworkElement? CreateSettingsView() => new CyberVfdSettingsView(this);

    /// <summary>Current settings, read by the settings view.</summary>
    internal (string Mode, string Port, int Brightness, bool Backlight, bool Power) ReadState() =>
        (PortMode, ManualPort, Brightness, Backlight, DevicePower);
}
