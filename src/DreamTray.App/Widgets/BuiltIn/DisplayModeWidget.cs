using System.Windows;
using System.Windows.Controls;

namespace DreamTray.App.Widgets.BuiltIn;

internal sealed class DisplayModeWidgetFactory : IWidgetFactory
{
    public const string Id = "core.displaymode";
    public string TypeId => Id;
    public string DisplayName => "Resolution & refresh rate";
    public string Description => "Change resolution and refresh rate; on a laptop, optionally drop the mode on battery.";
    public string Glyph => "\uE7F4";
    public IWidget Create(IWidgetContext context) => new DisplayModeWidget(context);
}

/// <summary>
/// Resolution and refresh rate for one display (the primary by default).
///
/// Resolution and refresh are separate pickers even though Windows enumerates them
/// as one mode list: changing refresh rate is a common, low-risk thing to do, and
/// making the user find their current resolution again to do it would be tedious.
/// The refresh list is filtered to the selected resolution.
/// </summary>
internal sealed class DisplayModeWidget(IWidgetContext context) : WidgetBase(context)
{
    private StackPanel? _root;
    private bool? _lastOnAc;

    public override string Title => "Display mode";

    private string DeviceName
    {
        get => Storage.Get("device", "");
        set => Storage.Set("device", value);
    }

    private int BatteryRefreshHz
    {
        get => Storage.Get("batteryHz", 0);   // 0 = leave alone
        set => Storage.Set("batteryHz", value);
    }

    private int ChargerRefreshHz
    {
        get => Storage.Get("chargerHz", 0);
        set => Storage.Set("chargerHz", value);
    }

    /// <summary>Stored as "WxH"; empty means "leave the resolution alone".</summary>
    private (int Width, int Height) BatteryResolution
    {
        get => ParseResolution(Storage.Get("batteryRes", ""));
        set => Storage.Set("batteryRes", FormatResolution(value));
    }

    private (int Width, int Height) ChargerResolution
    {
        get => ParseResolution(Storage.Get("chargerRes", ""));
        set => Storage.Set("chargerRes", FormatResolution(value));
    }

    /// <summary>What to do with modes whose aspect ratio is not the panel's own.</summary>
    private enum OffRatioDisplay { Show, Fade, Hide }

    private OffRatioDisplay OffRatioMode
    {
        get => Enum.TryParse(Storage.Get("offRatio", ""), out OffRatioDisplay v) ? v : OffRatioDisplay.Fade;
        set => Storage.Set("offRatio", value.ToString());
    }

    private static (int Width, int Height) ParseResolution(string raw)
    {
        var parts = raw.Split('x');
        return parts.Length == 2 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h)
            ? (w, h)
            : (0, 0);
    }

    private static string FormatResolution((int Width, int Height) r) =>
        r.Width > 0 && r.Height > 0 ? $"{r.Width}x{r.Height}" : "";

    protected override FrameworkElement BuildView()
    {
        _root = new StackPanel();
        Rebuild();
        return _root;
    }

    protected override void OnShown()
    {
        // Draw what the last scan found, immediately, and re-scan behind the panel.
        // The scan is a CCD query plus one EnumDisplaySettings call per supported
        // mode — several hundred driver round trips — and doing it here on the UI
        // thread held the whole panel back: every widget builds and the window
        // composes after this returns, so a busy GPU driver delayed the flyout by
        // however long it took to answer.
        Rebuild();
        RefreshModes();
    }

    /// <summary>Re-scan in the background and rebuild when the new list lands.</summary>
    private void RefreshModes()
    {
        var root = _root;
        if (root == null) return;
        Hardware.RefreshDisplayModesAsync(() => root.Dispatcher.BeginInvoke(() =>
        {
            // The panel may have closed, or the widget been removed, while the scan
            // was out; rebuilding a detached view is harmless but pointless.
            if (_root == root) Rebuild();
        }));
    }

    private DisplayDevice? ResolveDevice() => ResolveDevice(Hardware.GetDisplayDevices());

    private DisplayDevice? ResolveDevice(IReadOnlyList<DisplayDevice> devices) =>
        devices.Count == 0
            ? null
            // A saved device that has been unplugged falls back to the primary rather
            // than showing an empty widget.
            : devices.FirstOrDefault(d => d.DeviceName == DeviceName) ?? devices[0];

    private void Rebuild()
    {
        if (_root == null) return;
        _root.Children.Clear();

        var devices = Hardware.GetDisplayDevices();
        var device = ResolveDevice(devices);
        if (device == null)
        {
            _root.Children.Add(Ui.Caption("No display found."));
            return;
        }

        // The picker stays in the body rather than on the title row: it is one of
        // three combos, and hoisting it would leave it out of line with the two it
        // qualifies.
        if (devices.Count > 1)
        {
            _root.Children.Add(Ui.LabelRow("Display", Ui.Combo(devices, device, d =>
            {
                DeviceName = d.DeviceName;
                Rebuild();
            }, d => d.FriendlyName)));
        }

        var modes = Hardware.GetModes(device.DeviceName);
        var current = Hardware.GetCurrentMode(device.DeviceName);
        if (modes.Count == 0 || current == null)
        {
            _root.Children.Add(Ui.Caption("This display did not report any modes."));
            return;
        }

        var resolutions = modes
            .Select(m => (m.Width, m.Height))
            .Distinct()
            .OrderByDescending(r => r.Width * r.Height)
            .ToList();

        var selectedResolution = (current.Width, current.Height);

        // Windows lists modes that letterbox or stretch the panel alongside the ones that
        // fill it. They still work, so by default they stay selectable and are drawn faded,
        // so the shapes that match the panel read as the normal choices; the setting can
        // instead treat them as ordinary or drop them from the list. The largest mode is
        // the panel's native one and defines the reference ratio.
        var native = resolutions[0];
        double nativeRatio = (double)native.Width / native.Height;
        bool OffRatio((int Width, int Height) r) =>
            Math.Abs((double)r.Width / r.Height - nativeRatio) > 0.01;

        var offRatio = OffRatioMode;
        if (offRatio == OffRatioDisplay.Hide)
        {
            // The mode in use stays listed even when off-ratio, or the combo would have
            // nothing to select and would read as if some other resolution were active.
            resolutions = resolutions
                .Where(r => !OffRatio(r) || r == selectedResolution)
                .ToList();
        }

        var resolutionCombo = Ui.Combo(resolutions, selectedResolution, r =>
        {
            if (r == (current.Width, current.Height)) return;
            // Keep the current refresh rate if the new resolution supports it,
            // otherwise take its highest.
            int hz = modes.Any(m => m.Width == r.Width && m.Height == r.Height &&
                                    m.RefreshHz == current.RefreshHz)
                ? current.RefreshHz
                : modes.Where(m => m.Width == r.Width && m.Height == r.Height)
                       .Max(m => m.RefreshHz);
            ApplyMode(device.DeviceName, new DisplayMode(r.Width, r.Height, hz));
        }, r => $"{r.Width} × {r.Height}",
           offRatio == OffRatioDisplay.Fade ? OffRatio : null);

        var rates = modes
            .Where(m => m.Width == selectedResolution.Width && m.Height == selectedResolution.Height)
            .Select(m => m.RefreshHz)
            .Distinct()
            .OrderByDescending(hz => hz)
            .ToList();

        var refreshCombo = Ui.Combo(rates, current.RefreshHz, hz =>
        {
            if (hz == current.RefreshHz) return;
            ApplyMode(device.DeviceName,
                      new DisplayMode(selectedResolution.Width, selectedResolution.Height, hz));
        }, hz => $"{hz} Hz");

        _root.Children.Add(Ui.LabelRow("Resolution", resolutionCombo, devices.Count > 1 ? 6 : 0));
        _root.Children.Add(Ui.LabelRow("Refresh rate", refreshCombo, 6));
    }

    private void ApplyMode(string deviceName, DisplayMode mode)
    {
        if (!Hardware.SetMode(deviceName, mode))
            Host.Notify("DreamTray", $"{mode} was rejected by the display.");
        // The cached snapshot still describes the old mode, so rebuild only once a
        // scan taken after the change has landed — otherwise the combos would snap
        // back to what was selected a moment ago.
        RefreshModes();
    }

    // ---- background rule ----

    public override bool WantsBackgroundWork =>
        Hardware.HasBattery &&
        (BatteryRefreshHz > 0 || ChargerRefreshHz > 0 ||
         BatteryResolution.Width > 0 || ChargerResolution.Width > 0);

    public override void OnBackgroundTick(SystemSnapshot snapshot)
    {
        if (!WantsBackgroundWork || !snapshot.BatteryPresent) return;

        bool onAc = snapshot.OnAcPower;
        if (_lastOnAc == onAc) return;
        bool first = _lastOnAc == null;
        _lastOnAc = onAc;
        if (first) return;

        int targetHz = onAc ? ChargerRefreshHz : BatteryRefreshHz;
        var targetRes = onAc ? ChargerResolution : BatteryResolution;
        if (targetHz <= 0 && targetRes.Width <= 0) return;

        var device = ResolveDevice();
        if (device == null) return;
        var current = Hardware.GetCurrentMode(device.DeviceName);
        if (current == null) return;

        int width = targetRes.Width > 0 ? targetRes.Width : current.Width;
        int height = targetRes.Width > 0 ? targetRes.Height : current.Height;
        int hz = targetHz > 0 ? targetHz : current.RefreshHz;

        // Resolution and refresh rate are picked independently here, so the pair can name
        // a mode the display does not have; fall back to the best rate for that size.
        var modes = Hardware.GetModes(device.DeviceName);
        if (!modes.Any(m => m.Width == width && m.Height == height && m.RefreshHz == hz))
        {
            var forSize = modes.Where(m => m.Width == width && m.Height == height).ToList();
            if (forSize.Count == 0) return;
            hz = forSize.Max(m => m.RefreshHz);
        }

        var target = new DisplayMode(width, height, hz);
        if (target == current) return;
        Hardware.SetMode(device.DeviceName, target);
    }

    public override FrameworkElement? CreateSettingsView()
    {
        var device = ResolveDevice();
        var modes = device == null ? [] : Hardware.GetModes(device.DeviceName);

        string RatioLabel(OffRatioDisplay v) => v switch
        {
            OffRatioDisplay.Show => "Show",
            OffRatioDisplay.Hide => "Hide",
            _ => "Fade",
        };

        var ratioSection = new UIElement[]
        {
            Ui.Caption("Resolutions that do not match the panel's own aspect ratio letterbox " +
                       "or stretch the picture. Fading keeps them available but out of the way; " +
                       "disabling leaves them out of the list entirely."),
            Ui.LabelRow("Off-ratio modes", Ui.Combo(
                Enum.GetValues<OffRatioDisplay>(), OffRatioMode, v =>
                {
                    OffRatioMode = v;
                    Rebuild();
                }, RatioLabel)),
        };

        // The power-source rule only makes sense on a laptop; on a desktop the flyout is
        // just the off-ratio choice.
        if (!Hardware.HasBattery) return Ui.SettingsPanel(ratioSection);

        // 0 / (0, 0) mean "don't touch it", offered first so each rule is opt-in.
        var rates = new List<int> { 0 };
        rates.AddRange(modes.Select(m => m.RefreshHz).Distinct().OrderByDescending(hz => hz));

        var sizes = new List<(int Width, int Height)> { (0, 0) };
        sizes.AddRange(modes.Select(m => (m.Width, m.Height)).Distinct()
                            .OrderByDescending(r => r.Width * r.Height));

        string HzLabel(int hz) => hz == 0 ? "Leave unchanged" : $"{hz} Hz";
        string ResLabel((int Width, int Height) r) =>
            r.Width == 0 ? "Leave unchanged" : $"{r.Width} × {r.Height}";

        return Ui.SettingsPanel(
            [.. ratioSection,
             Ui.Separator(),
             Ui.Caption("Automatically switch the display mode when the charger comes or goes. " +
                        "Dropping to 60 Hz on battery is usually worth 1–2 W."),
             Ui.LabelRow("Battery refresh", Ui.Combo(rates, BatteryRefreshHz, v =>
             {
                 BatteryRefreshHz = v;
                 _lastOnAc = null;
             }, HzLabel)),
             Ui.LabelRow("Charger refresh", Ui.Combo(rates, ChargerRefreshHz, v =>
             {
                 ChargerRefreshHz = v;
                 _lastOnAc = null;
             }, HzLabel)),
             Ui.LabelRow("Battery resolution", Ui.Combo(sizes, BatteryResolution, v =>
             {
                 BatteryResolution = v;
                 _lastOnAc = null;
             }, ResLabel)),
             Ui.LabelRow("Charger resolution", Ui.Combo(sizes, ChargerResolution, v =>
             {
                 ChargerResolution = v;
                 _lastOnAc = null;
             }, ResLabel))]);
    }
}
