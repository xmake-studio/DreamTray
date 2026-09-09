using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace DreamTray.App.Widgets.BuiltIn;

internal sealed class BrightnessWidgetFactory : IWidgetFactory
{
    public const string Id = "core.brightness";

    public string TypeId => Id;
    public string DisplayName => "Brightness";
    public string Description => "One slider per display — the laptop panel and any DDC/CI monitor.";
    public string Glyph => "\uE706";

    public bool IsAvailable(IPluginHost host) => host.Hardware.GetDisplays().Count > 0;
    public IWidget Create(IWidgetContext context) => new BrightnessWidget(context);
}

/// <summary>
/// Brightness sliders for every controllable display.
///
/// Displays are re-enumerated each time the panel opens, because monitors get
/// plugged in and docks get detached while the app runs — a cached list goes stale
/// in a way the user notices immediately. The scan runs behind the panel: what is
/// on screen is always the last completed scan, and its sliders work throughout,
/// so a re-scan never costs the user the ability to change brightness.
/// </summary>
internal sealed class BrightnessWidget(IWidgetContext context) : WidgetBase(context)
{
    private StackPanel? _root;
    private StackPanel? _rows;
    private TextBlock? _status;
    private readonly List<(string Id, Slider Slider, TextBlock Value)> _controls = [];
    private bool _suppressCallbacks;
    /// <summary>Live subscription to scan results; only held while the panel is up.</summary>
    private IDisposable? _watch;
    /// <summary>Re-evaluates the status line while a scan is out. Runs only then.</summary>
    private DispatcherTimer? _tick;
    private DateTime _scanAskedAt;
    /// <summary>
    /// How long a scan has to be out before the widget admits to it. A scan of
    /// monitors that are awake takes ~130 ms, which is inside the panel's open
    /// animation: a line that appears and disappears in there is a flicker and a
    /// height change, not information. Past this the wait is real and worth naming.
    /// </summary>
    private static readonly TimeSpan SlowScan = TimeSpan.FromMilliseconds(400);
    /// <summary>
    /// The display set the sliders were built from. A scan that finds exactly what
    /// the last one did must not rebuild: rebuilding mid-drag swaps the slider out
    /// from under the mouse, and re-scans are frequent by design.
    /// </summary>
    private string _built = "";

    public override string Title => "Brightness";

    private bool LinkDisplays
    {
        get => Storage.Get("link", false);
        set => Storage.Set("link", value);
    }

    protected override FrameworkElement BuildView()
    {
        _rows = new StackPanel();
        _status = Ui.Caption("");
        _status.Margin = new Thickness(0, 4, 0, 0);
        _status.Visibility = Visibility.Collapsed;
        _root = new StackPanel();
        _root.Children.Add(_rows);
        _root.Children.Add(_status);
        Rebuild();
        UpdateStatus();
        return _root;
    }

    protected override void OnShown()
    {
        // Show the displays we already know about straight away, and re-scan behind
        // the panel. The scan is a WMI query plus a DDC/CI round trip per external
        // monitor: usually tens of milliseconds, occasionally seconds when a monitor
        // is asleep or slow to answer over I2C. Doing it here on the UI thread held
        // the whole panel back — every widget builds and the window composes after
        // this returns, so a dozing monitor delayed the flyout by however long it
        // took to reply.
        Rebuild();
        // Every scan while the panel is up, not just the one asked for below: the
        // retry that finally reaches a monitor which was asleep at open time is
        // exactly the result worth showing without making the user close and reopen.
        _watch ??= Hardware.SubscribeDisplayChanges(OnScanCompleted);
        RefreshDisplays();
    }

    public override void OnVisibilityChanged(bool visible)
    {
        base.OnVisibilityChanged(visible);
        if (visible) return;
        _watch?.Dispose();
        _watch = null;
        _tick?.Stop();
    }

    /// <summary>Re-scan in the background and update when the new list lands.</summary>
    private void RefreshDisplays()
    {
        if (_rows == null) return;
        // Ask, then say so: the status is what tells the user whether the list in
        // front of them is final or a scan is still working through a slow monitor.
        _scanAskedAt = DateTime.UtcNow;
        Hardware.RefreshDisplaysAsync(OnScanCompleted);
        UpdateStatus();

        // Nothing else would notice the scan crossing from "quick" to "slow": the
        // service only speaks when a scan finishes.
        _tick ??= new DispatcherTimer(DispatcherPriority.Background, _root!.Dispatcher)
        {
            Interval = SlowScan,
        };
        _tick.Tick -= OnTick;
        _tick.Tick += OnTick;
        _tick.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        UpdateStatus();
        if (!Hardware.GetDisplayScanStatus().Scanning) _tick?.Stop();
    }

    /// <summary>A scan finished (background thread) — reflect it on the UI thread.</summary>
    private void OnScanCompleted()
    {
        var root = _root;
        root?.Dispatcher.BeginInvoke(() =>
        {
            // The panel may have closed, or the widget been removed, while the scan
            // was out; updating a detached view is harmless but pointless.
            if (_root != root) return;
            Rebuild();
            SyncValues();
            UpdateStatus();
            if (!Hardware.GetDisplayScanStatus().Scanning) _tick?.Stop();
        });
    }

    /// <summary>
    /// Move the sliders to what the last scan read back, for the displays that are
    /// still the same ones. Rebuild bails out when the display set has not changed —
    /// which is nearly always — so without this a brightness changed on the monitor's
    /// own buttons would never show up. A slider the user has hold of is left alone:
    /// their hand beats a reading taken a moment ago.
    /// </summary>
    private void SyncValues()
    {
        var displays = Hardware.GetDisplays();
        _suppressCallbacks = true;
        try
        {
            foreach (var (id, slider, value) in _controls)
            {
                var display = displays.FirstOrDefault(d => d.Id == id);
                if (display == null || display.Brightness < 0) continue;
                if (slider.IsMouseCaptureWithin || slider.IsKeyboardFocusWithin) continue;
                if ((int)slider.Value == display.Brightness) continue;
                slider.Value = display.Brightness;
                value.Text = $"{display.Brightness}%";
            }
        }
        finally { _suppressCallbacks = false; }
    }

    /// <summary>
    /// One line under the sliders, shown only when there is something to wait for.
    /// Its absence is the "nothing more is coming" signal.
    /// </summary>
    private void UpdateStatus()
    {
        if (_status == null) return;
        var status = Hardware.GetDisplayScanStatus();
        bool slow = status.Scanning && DateTime.UtcNow - _scanAskedAt >= SlowScan;

        string text =
            slow ? "Checking displays…"
            : status.NotResponding == 0 ? ""
            : status.RetryScheduled
                ? $"{Monitors(status.NotResponding)} not answering yet — retrying"
                : $"{Monitors(status.NotResponding)} not answering. Turn DDC/CI on in the " +
                  "monitor menu, then re-scan from this widget's settings.";

        _status.Text = text;
        _status.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        static string Monitors(int count) => count == 1 ? "1 monitor is" : $"{count} monitors are";
    }

    private void Rebuild()
    {
        if (_rows == null) return;

        var displays = Hardware.GetDisplays();
        // Identity, not values: a scan that returns the same displays leaves the
        // sliders exactly as they are, including one the user is dragging right now.
        string signature = string.Join("|", displays.Select(d => $"{d.Id}\u0001{d.Name}"));
        if (_rows.Children.Count > 0 && signature == _built) return;
        _built = signature;

        _rows.Children.Clear();
        _controls.Clear();

        if (displays.Count == 0)
        {
            _rows.Children.Add(Ui.Caption("No display accepts a brightness command. " +
                                          "External monitors need DDC/CI enabled in their menu."));
            return;
        }

        bool showNames = displays.Count > 1;
        foreach (var display in displays)
        {
            int current = display.Brightness < 0 ? 50 : display.Brightness;

            var value = Ui.Value($"{current}%");
            value.MinWidth = 38;

            var slider = Ui.Slider(0, 100, current, v => OnSliderChanged(display.Id, (int)v));
            slider.Margin = new Thickness(0, 0, 8, 0);
            slider.VerticalAlignment = VerticalAlignment.Center;

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(slider, 0);
            Grid.SetColumn(value, 1);
            grid.Children.Add(slider);
            grid.Children.Add(value);

            if (showNames)
            {
                var label = Ui.Caption(display.Name);
                label.Margin = new Thickness(0, 0, 0, 2);
                label.TextTrimming = TextTrimming.CharacterEllipsis;
                label.TextWrapping = TextWrapping.NoWrap;
                _rows.Children.Add(label);
            }
            _rows.Children.Add(grid);
            _controls.Add((display.Id, slider, value));
        }
    }

    private void OnSliderChanged(string displayId, int percent)
    {
        if (_suppressCallbacks) return;

        if (LinkDisplays)
        {
            // Move every slider together, then issue one write per display.
            _suppressCallbacks = true;
            foreach (var (id, slider, value) in _controls)
            {
                slider.Value = percent;
                value.Text = $"{percent}%";
                if (id != displayId) Hardware.SetBrightness(id, percent);
            }
            _suppressCallbacks = false;
        }
        else
        {
            var entry = _controls.FirstOrDefault(c => c.Id == displayId);
            if (entry.Value != null) entry.Value.Text = $"{percent}%";
        }

        Hardware.SetBrightness(displayId, percent);
    }

    public override FrameworkElement? CreateSettingsView() => Ui.SettingsPanel(
        Ui.Caption("Brightness is applied to the built-in panel through the ACPI backlight " +
                   "interface and to external monitors over DDC/CI."),
        Ui.LabelRow("Move all displays together", Ui.Switch(LinkDisplays, v => LinkDisplays = v)),
        Ui.Button("Re-scan displays", RefreshDisplays));

    public override void Dispose()
    {
        _watch?.Dispose();
        _watch = null;
        if (_tick != null)
        {
            _tick.Stop();
            _tick.Tick -= OnTick;
            _tick = null;
        }
        base.Dispose();
    }
}
