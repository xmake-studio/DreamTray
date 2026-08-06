using System.Windows;
using System.Windows.Controls;

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
/// in a way the user notices immediately.
/// </summary>
internal sealed class BrightnessWidget(IWidgetContext context) : WidgetBase(context)
{
    private StackPanel? _rows;
    private readonly List<(string Id, Slider Slider, TextBlock Value)> _controls = [];
    private bool _suppressCallbacks;

    public override string Title => "Brightness";

    private bool LinkDisplays
    {
        get => Storage.Get("link", false);
        set => Storage.Set("link", value);
    }

    protected override FrameworkElement BuildView()
    {
        _rows = new StackPanel();
        Rebuild();
        return _rows;
    }

    protected override void OnShown()
    {
        // Cheap (a WMI read plus one DDC read per monitor) and only on open.
        Hardware.GetDisplays(refresh: true);
        Rebuild();
    }

    private void Rebuild()
    {
        if (_rows == null) return;
        _rows.Children.Clear();
        _controls.Clear();

        var displays = Hardware.GetDisplays();
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
            slider.Margin = new Thickness(0, 2, 8, 0);

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
        Ui.Button("Re-scan displays", () =>
        {
            Hardware.GetDisplays(refresh: true);
            Rebuild();
        }));
}
