using System.Windows;
using System.Windows.Controls;

namespace DreamTray.App.Widgets.BuiltIn;

internal sealed class DisplayModeWidgetFactory : IWidgetFactory
{
    public const string Id = "core.displaymode";
    public string TypeId => Id;
    public string DisplayName => "Resolution & refresh rate";
    public string Description => "Change resolution and refresh rate; on a laptop, optionally drop the refresh rate on battery.";
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

    protected override FrameworkElement BuildView()
    {
        _root = new StackPanel();
        Rebuild();
        return _root;
    }

    protected override void OnShown() => Rebuild();

    private DisplayDevice? ResolveDevice()
    {
        var devices = Hardware.GetDisplayDevices();
        if (devices.Count == 0) return null;
        // A saved device that has been unplugged falls back to the primary rather
        // than showing an empty widget.
        return devices.FirstOrDefault(d => d.DeviceName == DeviceName) ?? devices[0];
    }

    private void Rebuild()
    {
        if (_root == null) return;
        _root.Children.Clear();

        var device = ResolveDevice();
        if (device == null)
        {
            _root.Children.Add(Ui.Caption("No display found."));
            return;
        }

        var devices = Hardware.GetDisplayDevices();
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
        }, r => $"{r.Width} × {r.Height}");

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
        Rebuild();
    }

    // ---- background rule ----

    public override bool WantsBackgroundWork =>
        Hardware.HasBattery && (BatteryRefreshHz > 0 || ChargerRefreshHz > 0);

    public override void OnBackgroundTick(SystemSnapshot snapshot)
    {
        if (!WantsBackgroundWork || !snapshot.BatteryPresent) return;

        bool onAc = snapshot.OnAcPower;
        if (_lastOnAc == onAc) return;
        bool first = _lastOnAc == null;
        _lastOnAc = onAc;
        if (first) return;

        int target = onAc ? ChargerRefreshHz : BatteryRefreshHz;
        if (target <= 0) return;

        var device = ResolveDevice();
        if (device == null) return;
        var current = Hardware.GetCurrentMode(device.DeviceName);
        if (current == null || current.RefreshHz == target) return;

        Hardware.SetMode(device.DeviceName, current with { RefreshHz = target });
    }

    public override FrameworkElement? CreateSettingsView()
    {
        // The only thing in here is the power-source rule; on a desktop that leaves
        // an empty flyout, so drop the "…" button instead.
        if (!Hardware.HasBattery) return null;

        var device = ResolveDevice();
        List<int> rates = device == null
            ? []
            : Hardware.GetModes(device.DeviceName)
                      .Select(m => m.RefreshHz).Distinct()
                      .OrderByDescending(hz => hz).ToList();

        // 0 means "don't touch it", offered first so the rule is opt-in.
        var options = new List<int> { 0 };
        options.AddRange(rates);

        string Label(int hz) => hz == 0 ? "Leave unchanged" : $"{hz} Hz";

        return Ui.SettingsPanel(
            Ui.Caption("Automatically switch refresh rate when the charger comes or goes. " +
                       "Dropping to 60 Hz on battery is usually worth 1–2 W."),
            Ui.LabelRow("On battery", Ui.Combo(options, BatteryRefreshHz, v =>
            {
                BatteryRefreshHz = v;
                _lastOnAc = null;
            }, Label)),
            Ui.LabelRow("On charger", Ui.Combo(options, ChargerRefreshHz, v =>
            {
                ChargerRefreshHz = v;
                _lastOnAc = null;
            }, Label)));
    }
}
