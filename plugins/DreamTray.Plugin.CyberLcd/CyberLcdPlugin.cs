using System.Windows;
using System.Windows.Media;

namespace DreamTray.Plugins.CyberLcd;

/// <summary>
/// Streams system metrics to the CyberLCD (RP2040 + 316x55 inverted LCD panel,
/// two ARGB strips) over USB serial.
///
/// Same shape as DreamTray.Plugin.CyberVfd (this repo's sibling plugin): reads
/// from DreamTray's shared sampler, all serial I/O on its own thread, backoff
/// reconnect, control frames re-queued/re-sent across a reconnect. The extra
/// surface here is the two RGB strips: mode + color/gradient + brightness for
/// the display backlight, mode + color/gradient + brightness + LED count for
/// the desk-perimeter strip.
/// </summary>
public sealed class CyberLcdPlugin : DreamPluginBase
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
    /// keeps the panel + Temperature-mode backlight live, so the worker re-sends
    /// the last one on its own clock when the host sampler stalls.
    /// </summary>
    private SystemSnapshot? _latest;

    private string _status = "stopped";

    public override string Id => "cyberlcd";
    public override string Name => "CyberLCD display";
    public override string Description => "Streams system metrics to the CyberLCD serial panel.";
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

    private bool DevicePower
    {
        get => Host.Storage.Get("devicePower", true);  // master on/off
        set => Host.Storage.Set("devicePower", value);
    }

    private int DisplayBrightness
    {
        get => Host.Storage.Get("displayBrightness", 255);
        set => Host.Storage.Set("displayBrightness", value);
    }

    private int DeskBrightness
    {
        get => Host.Storage.Get("deskBrightness", 160);
        set => Host.Storage.Set("deskBrightness", value);
    }

    /// <summary>"Temperature" | "Solid" | "Gradient" | "Rainbow".</summary>
    private string DisplayMode
    {
        get => Host.Storage.Get("displayMode", "Temperature");
        set => Host.Storage.Set("displayMode", value);
    }

    private string DisplayColorHex
    {
        get => Host.Storage.Get("displayColor", "39E0C8");
        set => Host.Storage.Set("displayColor", value);
    }

    private string DisplayGradientWire
    {
        get => Host.Storage.Get("displayGradient", "0.00:39E0C8,1.00:7C5CFF");
        set => Host.Storage.Set("displayGradient", value);
    }

    private float DisplayRainbowSpeed
    {
        get => Host.Storage.Get("displayRainbowSpeed", 40f);
        set => Host.Storage.Set("displayRainbowSpeed", value);
    }

    private float DisplayRainbowWidth
    {
        get => Host.Storage.Get("displayRainbowWidth", 20f);
        set => Host.Storage.Set("displayRainbowWidth", value);
    }

    /// <summary>"Solid" | "Gradient" | "Rainbow" (no Temperature option for the desk strip).</summary>
    private string DeskMode
    {
        get => Host.Storage.Get("deskMode", "Solid");
        set => Host.Storage.Set("deskMode", value);
    }

    private string DeskColorHex
    {
        get => Host.Storage.Get("deskColor", "39E0C8");
        set => Host.Storage.Set("deskColor", value);
    }

    private string DeskGradientWire
    {
        get => Host.Storage.Get("deskGradient", "0.00:39E0C8,1.00:7C5CFF");
        set => Host.Storage.Set("deskGradient", value);
    }

    private float DeskRainbowSpeed
    {
        get => Host.Storage.Get("deskRainbowSpeed", 40f);
        set => Host.Storage.Set("deskRainbowSpeed", value);
    }

    private float DeskRainbowWidth
    {
        get => Host.Storage.Get("deskRainbowWidth", 20f);
        set => Host.Storage.Set("deskRainbowWidth", value);
    }

    private int DeskLedCount
    {
        get => Host.Storage.Get("deskLedCount", 30);
        set => Host.Storage.Set("deskLedCount", value);
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
        _worker = new Thread(() => Run(generation)) { IsBackground = true, Name = "cyberlcd-link" };
        _worker.Start();

        // One sample per second is what the firmware's ~5s watchdog and the
        // display refresh expect; the worker's own clock is what actually keeps
        // the panel powered (see Run()), so an upstream stall doesn't blank it.
        _subscription = Host.SubscribeSensors(TimeSpan.FromSeconds(1), snapshot =>
        {
            lock (_gate) _latest = snapshot;
            _wake.Set();
        });

        QueueFullState();
        Host.Log("cyberlcd: started");
    }

    public override void Stop()
    {
        _subscription?.Dispose();
        _subscription = null;

        _stop = true;
        _generation++;
        _wake.Set();
        _worker?.Join(3000);
        _worker = null;

        _link.Disconnect();
        SetStatus("stopped");
        Host.Log("cyberlcd: stopped");
    }

    public override void Dispose()
    {
        Stop();
        _link.Dispose();
        // _wake is deliberately not disposed: if the worker did not exit within the
        // join above it is still blocked on this handle, and disposing it would
        // throw ObjectDisposedException on that thread.
        base.Dispose();
    }

    // ---------------------------------------------------------------- worker

    private const int FramePeriodMs = 1000;

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
                nextFrameAt = 0;
            }

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
                SystemSnapshot? snapshot;
                lock (_gate) snapshot = _latest;

                if (DevicePower && snapshot != null)
                    ok = _link.Send(CyberLcdFrame.Build(snapshot, DateTime.Now));

                nextFrameAt = now + FramePeriodMs;
            }

            if (!ok)
            {
                lock (_gate) foreach (var f in frames) _outbox.Enqueue(f);
                SetStatus("link lost");
                Host.Log($"cyberlcd: link lost on {_link.PortName ?? "?"}");
                _link.Disconnect();
                continue;
            }

            SetStatus($"connected on {_link.PortName}");

            int wait = (int)Math.Clamp(nextFrameAt - Environment.TickCount64, 1, FramePeriodMs);
            _wake.WaitOne(wait);
        }

        if (_generation != generation) _link.Disconnect();
    }

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
            Host.Log($"cyberlcd: connected on {_link.PortName}");
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
            _outbox.Enqueue(CyberLcdFrame.Power(DevicePower));
            _outbox.Enqueue(CyberLcdFrame.DisplayBrightness(DisplayBrightness));
            _outbox.Enqueue(CyberLcdFrame.DeskBrightness(DeskBrightness));
            _outbox.Enqueue(CyberLcdFrame.DeskLedCount(DeskLedCount));
            _outbox.Enqueue(DisplayModeFrame());
            _outbox.Enqueue(DeskModeFrame());
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
        Application.Current?.Dispatcher.BeginInvoke(() => StatusChanged?.Invoke());
    }

    // ---------------------------------------------------------------- controls

    public void SetDevicePower(bool on)
    {
        DevicePower = on;
        Enqueue(CyberLcdFrame.Power(on));
    }

    public void SetDisplayBrightness(int value)
    {
        DisplayBrightness = Math.Clamp(value, 0, 255);
        Enqueue(CyberLcdFrame.DisplayBrightness(DisplayBrightness));
    }

    public void SetDeskBrightness(int value)
    {
        DeskBrightness = Math.Clamp(value, 0, 255);
        Enqueue(CyberLcdFrame.DeskBrightness(DeskBrightness));
    }

    /// <summary>"Temperature" | "Solid" | "Gradient" | "Rainbow". Re-applies the stored color/gradient/rainbow params for the new mode.</summary>
    public void SetDisplayMode(string mode)
    {
        DisplayMode = mode;
        Enqueue(DisplayModeFrame());
    }

    public void SetDisplayColor(Color color)
    {
        DisplayColorHex = CyberLcdFrame.ColorToHex(color);
        if (DisplayMode == "Solid") Enqueue(DisplayModeFrame());
    }

    public void SetDisplayGradient(List<RgbStop> stops)
    {
        DisplayGradientWire = CyberLcdFrame.StopsToWire(stops);
        if (DisplayMode == "Gradient") Enqueue(DisplayModeFrame());
    }

    public void SetDisplayRainbow(float speedDegPerSec, float widthLeds)
    {
        DisplayRainbowSpeed = speedDegPerSec;
        DisplayRainbowWidth = widthLeds;
        if (DisplayMode == "Rainbow") Enqueue(DisplayModeFrame());
    }

    /// <summary>"Solid" | "Gradient" | "Rainbow".</summary>
    public void SetDeskMode(string mode)
    {
        DeskMode = mode;
        Enqueue(DeskModeFrame());
    }

    public void SetDeskColor(Color color)
    {
        DeskColorHex = CyberLcdFrame.ColorToHex(color);
        if (DeskMode == "Solid") Enqueue(DeskModeFrame());
    }

    public void SetDeskGradient(List<RgbStop> stops)
    {
        DeskGradientWire = CyberLcdFrame.StopsToWire(stops);
        if (DeskMode == "Gradient") Enqueue(DeskModeFrame());
    }

    public void SetDeskRainbow(float speedDegPerSec, float widthLeds)
    {
        DeskRainbowSpeed = speedDegPerSec;
        DeskRainbowWidth = widthLeds;
        if (DeskMode == "Rainbow") Enqueue(DeskModeFrame());
    }

    public void SetDeskLedCount(int count)
    {
        DeskLedCount = Math.Max(1, count);
        Enqueue(CyberLcdFrame.DeskLedCount(DeskLedCount));
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

    private string DisplayModeFrame() => DisplayMode switch
    {
        "Solid" => CyberLcdFrame.DisplayModeSolid(ParseColor(DisplayColorHex)),
        "Gradient" => CyberLcdFrame.DisplayModeGradient(CyberLcdFrame.ParseStops(DisplayGradientWire)),
        "Rainbow" => CyberLcdFrame.DisplayModeRainbow(DisplayRainbowSpeed, DisplayRainbowWidth),
        _ => CyberLcdFrame.DisplayModeTemperature(),
    };

    private string DeskModeFrame() => DeskMode switch
    {
        "Gradient" => CyberLcdFrame.DeskModeGradient(CyberLcdFrame.ParseStops(DeskGradientWire)),
        "Rainbow" => CyberLcdFrame.DeskModeRainbow(DeskRainbowSpeed, DeskRainbowWidth),
        _ => CyberLcdFrame.DeskModeSolid(ParseColor(DeskColorHex)),
    };

    private static Color ParseColor(string hex) =>
        CyberLcdFrame.TryParseHex(hex, out var c) ? c : Colors.White;

    // ---------------------------------------------------------------- UI

    public override IEnumerable<IWidgetFactory> Widgets => [new CyberLcdWidgetFactory(this)];

    public override FrameworkElement? CreateSettingsView() => new CyberLcdSettingsView(this);

    /// <summary>Current settings, read by the settings view.</summary>
    internal CyberLcdState ReadState() => new(
        PortMode, ManualPort, DevicePower,
        DisplayBrightness, DeskBrightness,
        DisplayMode, ParseColor(DisplayColorHex), CyberLcdFrame.ParseStops(DisplayGradientWire),
        DisplayRainbowSpeed, DisplayRainbowWidth,
        DeskMode, ParseColor(DeskColorHex), CyberLcdFrame.ParseStops(DeskGradientWire),
        DeskRainbowSpeed, DeskRainbowWidth, DeskLedCount);
}

internal readonly record struct CyberLcdState(
    string PortMode, string ManualPort, bool Power,
    int DisplayBrightness, int DeskBrightness,
    string DisplayMode, Color DisplayColor, List<RgbStop> DisplayGradient,
    float DisplayRainbowSpeed, float DisplayRainbowWidth,
    string DeskMode, Color DeskColor, List<RgbStop> DeskGradient,
    float DeskRainbowSpeed, float DeskRainbowWidth, int DeskLedCount);
