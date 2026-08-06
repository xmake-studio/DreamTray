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
    private SystemSnapshot? _pending;

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
        _worker = new Thread(Run) { IsBackground = true, Name = "cybervfd-link" };
        _worker.Start();

        // One sample per second is what the firmware's display refresh expects, and
        // it is also what the panel's 5 s watchdog needs to stay powered.
        _subscription = Host.SubscribeSensors(TimeSpan.FromSeconds(1), snapshot =>
        {
            lock (_gate) _pending = snapshot;
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
        _wake.Set();
        // A port scan can be mid-handshake (up to ~1 s per port), so the worker may
        // outlive this join.
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

    private void Run()
    {
        while (!_stop)
        {
            if (_reconnect) { _link.Disconnect(); _reconnect = false; }

            if (!_link.Connected && !Reconnect()) continue;

            // Control frames first (power/backlight/brightness), then the data frame.
            string[] frames;
            SystemSnapshot? snapshot;
            lock (_gate)
            {
                frames = _outbox.ToArray();
                _outbox.Clear();
                snapshot = _pending;
                _pending = null;
            }

            bool ok = frames.All(_link.Send);

            if (ok && snapshot != null)
            {
                // With the panel forced off there is nothing to draw, so skip the data
                // frame; the firmware powers the tube down after 5 s without one.
                if (DevicePower) ok = _link.Send(VfdFrame.Build(snapshot));
            }

            if (!ok)
            {
                // Re-queue undelivered control frames so they survive the reconnect.
                lock (_gate) foreach (var f in frames) _outbox.Enqueue(f);
                SetStatus("link lost");
                continue;
            }

            if (snapshot != null) SetStatus($"connected on {_link.PortName}");
            _wake.WaitOne(2000);
        }
    }

    private bool Reconnect()
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
            return true;
        }

        SetStatus(manual ? $"waiting for {port}" : "searching for the device…");
        _wake.WaitOne(2000);
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
