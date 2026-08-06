using System.Windows;
using System.Windows.Controls;

namespace DreamTray.App.Widgets.BuiltIn;

internal sealed class TdpWidgetFactory : IWidgetFactory
{
    public const string Id = "core.tdp";

    public string TypeId => Id;
    public string DisplayName => "APU power limit";
    public string Description => "Sustained TDP slider, with periodic re-apply and (on a laptop) per-power-source defaults.";
    public string Glyph => "\uE945";

    public bool IsAvailable(IPluginHost host) => host.Hardware.Tdp != null;
    public IWidget Create(IWidgetContext context) => new TdpWidget(context);
}

/// <summary>
/// The APU power-limit slider.
///
/// The slider only *sets* the limit; the policy that keeps it applied (periodic
/// re-assert, AC/battery defaults) lives in <see cref="Power.TdpService"/> so it
/// keeps working with the panel closed. This widget's settings flyout edits that
/// same policy, and so does the Settings window — one source of truth, two places
/// to reach it.
/// </summary>
internal sealed class TdpWidget : WidgetBase
{
    private readonly Power.TdpService? _service;
    private Slider? _slider;
    private TextBlock? _value;
    private TextBlock? _status;
    private bool _suppress;

    public TdpWidget(IWidgetContext context) : base(context)
    {
        // The concrete service is needed for the policy properties; the interface
        // deliberately exposes only the parts a plugin should touch.
        _service = (context.Host.Hardware.Tdp as Power.TdpService);
    }

    public override string Title => "APU power limit";

    protected override bool NeedsSensors => true;
    protected override TimeSpan SampleInterval => TimeSpan.FromSeconds(2);

    private Settings.TdpSettings Config => AppState.Tdp;

    protected override FrameworkElement BuildView()
    {
        var tdp = Hardware.Tdp;
        if (tdp == null)
        {
            // Say why, not just that: every cause here (no DLL, no elevation, driver
            // refused) has a different fix, and the status text names it.
            var reason = Ui.Caption(_service?.StatusText ?? "TDP control is unavailable.");
            return reason;
        }

        int min = tdp.MinWatts, max = tdp.MaxWatts;
        // Nothing chosen yet: start at the top of this chip's own range rather than a
        // fixed wattage, which would mean something different on every machine.
        int current = Config.LastWatts > 0 ? Math.Clamp(Config.LastWatts, min, max) : max;

        _value = Ui.Value($"{current} W");
        _value.MinWidth = 44;

        _slider = Ui.Slider(min, max, current, v => Apply((int)v));
        _slider.Margin = new Thickness(0, 2, 8, 0);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_slider, 0);
        Grid.SetColumn(_value, 1);
        grid.Children.Add(_slider);
        grid.Children.Add(_value);

        _status = Ui.Caption("");
        _status.Margin = new Thickness(0, 4, 0, 0);

        return Ui.Stack(grid, _status);
    }

    private void Apply(int watts)
    {
        if (_suppress) return;
        if (_value != null) _value.Text = $"{watts} W";

        Hardware.Tdp?.Apply(watts);
        Config.LastWatts = watts;
        AppState.Save();
    }

    protected override void OnSample(SystemSnapshot snapshot)
    {
        if (_status == null) return;

        // Two numbers, both about consumption: what the whole machine is pulling and
        // how much of that is the chip this slider controls. The limit itself is
        // already on the slider, so it is only worth words when the chip disagrees
        // with what we asked for — which is the OEM-override case this widget exists
        // for, and the one time a raw firmware number is the point.
        var parts = new List<string>();

        if (snapshot.SystemPowerKind == SystemPowerKind.Discharging)
            parts.Add($"{snapshot.SystemPower:F1} W system");
        else if (snapshot.SystemPowerKind == SystemPowerKind.Charging)
            parts.Add($"charging +{snapshot.SystemPower:F1} W");

        parts.Add($"{snapshot.PackagePower:F1} W APU");

        int asked = Hardware.Tdp?.AppliedWatts ?? 0;
        var readback = Hardware.Tdp?.Read();
        if (readback != null && asked > 0 && Math.Abs(readback.StapmLimit - asked) > 1f)
            parts.Add($"limit overridden to {readback.StapmLimit:F0} W");

        _status.Text = string.Join(" · ", parts);
    }

    protected override void OnShown()
    {
        // The policy may have moved the limit while the panel was closed.
        int applied = Hardware.Tdp?.AppliedWatts ?? 0;
        if (applied > 0 && _slider != null && Math.Abs(_slider.Value - applied) > 0.5)
        {
            _suppress = true;
            _slider.Value = applied;
            if (_value != null) _value.Text = $"{applied} W";
            _suppress = false;
        }
    }

    public override FrameworkElement? CreateSettingsView()
    {
        // Returning null hides the widget's "…" button entirely: with no backend
        // there is nothing to configure, and repeating the error in a flyout is noise.
        if (_service == null || !_service.IsAvailable) return null;

        var config = Config;

        var reapply = Ui.Number(config.ReapplySeconds, 0, 3600, v =>
        {
            config.ReapplySeconds = v;
            _service.ReapplySeconds = v;
            AppState.Save();
        });

        var children = new List<UIElement>
        {
            Ui.Caption(_service.StatusText),
            Ui.Separator(),
            Ui.LabelRow("Re-apply every (seconds)", reapply),
            Ui.Caption("OEM power software rewrites these limits on its own schedule. " +
                       "Re-applying periodically keeps your value in force. 0 turns it off."),
        };

        // Per-source defaults need two power sources to switch between.
        if (Hardware.HasBattery)
        {
            var acWatts = Ui.Number(config.AcWatts, _service.MinWatts, _service.MaxWatts, v =>
            {
                config.AcWatts = v;
                _service.AcWatts = v;
                AppState.Save();
            });

            var dcWatts = Ui.Number(config.DcWatts, _service.MinWatts, _service.MaxWatts, v =>
            {
                config.DcWatts = v;
                _service.DcWatts = v;
                AppState.Save();
            });

            var defaults = Ui.Switch(config.UsePowerSourceDefaults, v =>
            {
                config.UsePowerSourceDefaults = v;
                _service.UsePowerSourceDefaults = v;
                AppState.Save();
                if (v) _service.ApplyPowerSourceDefault();
            });

            children.Add(Ui.Separator());
            children.Add(Ui.LabelRow("Switch limit with power source", defaults));
            children.Add(Ui.LabelRow("On charger (W)", acWatts));
            children.Add(Ui.LabelRow("On battery (W)", dcWatts));
        }

        return Ui.SettingsPanel([.. children]);
    }
}
