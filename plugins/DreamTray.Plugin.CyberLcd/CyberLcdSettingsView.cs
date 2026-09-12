using System.Windows;
using System.Windows.Controls;

namespace DreamTray.Plugins.CyberLcd;

/// <summary>
/// The plugin's page in DreamTray's Settings window: connection state, port
/// selection, the panel's master power, two brightness sliders, and the two
/// independent RGB strips -- display backlight (Temperature / Solid /
/// Gradient / Rainbow) and the desk-perimeter strip (Solid / Gradient /
/// Rainbow + LED count). Every change is sent to the device immediately and
/// persisted, then re-sent on the next connect so the device always matches
/// what is shown here (see CyberLcdPlugin.QueueFullState) -- the desk strip
/// additionally saves its own configuration into the device's flash, so it
/// comes back up on its own even before this plugin connects.
/// </summary>
internal sealed class CyberLcdSettingsView : UserControl
{
    private readonly CyberLcdPlugin _plugin;
    private readonly TextBlock _status = PluginUi.Caption("");
    private readonly ComboBox _portCombo;
    private readonly StackPanel _displayEditorHost = new();
    private readonly StackPanel _deskEditorHost = new();

    public CyberLcdSettingsView(CyberLcdPlugin plugin)
    {
        _plugin = plugin;
        var state = plugin.ReadState();

        var ports = new List<string> { "Auto-detect" };
        ports.AddRange(SerialLink.AvailablePorts());

        string selectedPort = state.PortMode == "Manual" && !string.IsNullOrEmpty(state.ManualPort)
            ? state.ManualPort
            : "Auto-detect";

        _portCombo = PluginUi.Combo(ports, selectedPort, choice =>
        {
            if (choice == "Auto-detect") _plugin.UsePortAuto();
            else _plugin.UsePort(choice);
        });

        var power = PluginUi.Switch(state.Power, _plugin.SetDevicePower);
        var powerNote = PluginUi.Caption("Off blanks the panel and its backlight. The desk strip is independent -- it keeps running (and remembers its setup on the device itself, even without this plugin).");
        powerNote.Margin = new Thickness(0, 4, 0, 0);

        var displayBrightnessValue = PluginUi.Value($"{Percent(state.DisplayBrightness)}%");
        var displayBrightness = PluginUi.Slider(0, 255, state.DisplayBrightness, v =>
        {
            int raw = (int)v;
            displayBrightnessValue.Text = $"{Percent(raw)}%";
            _plugin.SetDisplayBrightness(raw);
        });

        var deskBrightnessValue = PluginUi.Value($"{Percent(state.DeskBrightness)}%");
        var deskBrightness = PluginUi.Slider(0, 255, state.DeskBrightness, v =>
        {
            int raw = (int)v;
            deskBrightnessValue.Text = $"{Percent(raw)}%";
            _plugin.SetDeskBrightness(raw);
        });

        var deskLedCountValue = PluginUi.Value(state.DeskLedCount.ToString());
        var deskLedCount = PluginUi.Slider(1, 300, state.DeskLedCount, v =>
        {
            int count = (int)v;
            deskLedCountValue.Text = count.ToString();
            _plugin.SetDeskLedCount(count);
        });

        var rescan = PluginUi.Button("Re-scan ports", RefreshPorts);
        rescan.HorizontalAlignment = HorizontalAlignment.Left;
        rescan.Margin = new Thickness(0, 14, 0, 0);

        var displayModeCombo = PluginUi.Combo(["Temperature", "Solid", "Gradient", "Rainbow"], state.DisplayMode, mode =>
        {
            _plugin.SetDisplayMode(mode);
            RebuildDisplayEditor(mode);
        });

        var deskModeCombo = PluginUi.Combo(["Solid", "Gradient", "Rainbow"], state.DeskMode, mode =>
        {
            _plugin.SetDeskMode(mode);
            RebuildDeskEditor(mode);
        });

        RebuildDisplayEditor(state.DisplayMode);
        RebuildDeskEditor(state.DeskMode);

        var sectionDisplay = SectionHeader("Display backlight");
        var sectionDesk = SectionHeader("Desk strip");

        Content = PluginUi.Stack(
            _status,
            PluginUi.LabelRow("Serial port", _portCombo),
            PluginUi.LabelRow("Panel power", power),
            powerNote,
            PluginUi.LabelRow("Display brightness", ReadoutRow(displayBrightness, displayBrightnessValue)),
            PluginUi.LabelRow("Desk strip brightness", ReadoutRow(deskBrightness, deskBrightnessValue)),
            rescan,
            sectionDisplay,
            PluginUi.LabelRow("Mode", displayModeCombo),
            _displayEditorHost,
            sectionDesk,
            PluginUi.LabelRow("Mode", deskModeCombo),
            _deskEditorHost,
            PluginUi.LabelRow("LED count", ReadoutRow(deskLedCount, deskLedCountValue)));

        UpdateStatus();
        plugin.StatusChanged += UpdateStatus;
        Unloaded += (_, _) => plugin.StatusChanged -= UpdateStatus;
    }

    /// <summary>The panel register is 0..255; showing a percentage is friendlier.</summary>
    private static int Percent(int raw) => (int)Math.Round(raw * 100.0 / 255.0);

    /// <summary>A slider with a fixed-width value readout to its right (doesn't jog
    /// left/right as the value's digit count changes).</summary>
    private static StackPanel ReadoutRow(UIElement slider, TextBlock value)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        value.MinWidth = 40;
        value.Margin = new Thickness(12, 0, 0, 0);
        value.TextAlignment = TextAlignment.Right;
        value.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(slider);
        row.Children.Add(value);
        return row;
    }

    private static TextBlock SectionHeader(string text)
    {
        var header = PluginUi.Caption(text.ToUpperInvariant());
        header.Margin = new Thickness(0, 20, 0, 0);
        return header;
    }

    private void RebuildDisplayEditor(string mode)
    {
        var state = _plugin.ReadState();
        _displayEditorHost.Children.Clear();
        if (mode == "Solid")
        {
            var swatch = PluginUi.ColorSwatch(state.DisplayColor, _plugin.SetDisplayColor);
            _displayEditorHost.Children.Add(PluginUi.LabelRow("Color", swatch, topMargin: 8));
        }
        else if (mode == "Gradient")
        {
            var editor = PluginUi.GradientStopEditor(state.DisplayGradient, _plugin.SetDisplayGradient);
            editor.Margin = new Thickness(0, 8, 0, 0);
            _displayEditorHost.Children.Add(editor);
        }
        else if (mode == "Rainbow")
        {
            var editor = RainbowEditor(state.DisplayRainbowSpeed, state.DisplayRainbowWidth, _plugin.SetDisplayRainbow);
            editor.Margin = new Thickness(0, 8, 0, 0);
            _displayEditorHost.Children.Add(editor);
        }
        // Temperature: no extra controls -- driven live from CPU/GPU temperature.
    }

    private void RebuildDeskEditor(string mode)
    {
        var state = _plugin.ReadState();
        _deskEditorHost.Children.Clear();
        if (mode == "Solid")
        {
            var swatch = PluginUi.ColorSwatch(state.DeskColor, _plugin.SetDeskColor);
            _deskEditorHost.Children.Add(PluginUi.LabelRow("Color", swatch, topMargin: 8));
        }
        else if (mode == "Gradient")
        {
            var editor = PluginUi.GradientStopEditor(state.DeskGradient, _plugin.SetDeskGradient);
            editor.Margin = new Thickness(0, 8, 0, 0);
            _deskEditorHost.Children.Add(editor);
        }
        else if (mode == "Rainbow")
        {
            var editor = RainbowEditor(state.DeskRainbowSpeed, state.DeskRainbowWidth, _plugin.SetDeskRainbow);
            editor.Margin = new Thickness(0, 8, 0, 0);
            _deskEditorHost.Children.Add(editor);
        }
    }

    /// <summary>Speed (0..200 deg/s -- 0 is a static rainbow) and width (2..120 LEDs
    /// per full hue cycle) sliders shared by both strips' Rainbow mode.</summary>
    private static StackPanel RainbowEditor(float speed, float width, Action<float, float> onChanged)
    {
        float currentSpeed = speed, currentWidth = width;

        var speedValue = PluginUi.Value($"{speed:0}°/s");
        var speedSlider = PluginUi.Slider(0, 200, speed, v =>
        {
            currentSpeed = (float)v;
            speedValue.Text = $"{currentSpeed:0}°/s";
            onChanged(currentSpeed, currentWidth);
        });

        var widthValue = PluginUi.Value($"{width:0}");
        var widthSlider = PluginUi.Slider(2, 120, width, v =>
        {
            currentWidth = (float)v;
            widthValue.Text = $"{currentWidth:0}";
            onChanged(currentSpeed, currentWidth);
        });

        return PluginUi.Stack(
            PluginUi.LabelRow("Speed", ReadoutRow(speedSlider, speedValue)),
            PluginUi.LabelRow("Width", ReadoutRow(widthSlider, widthValue)));
    }

    private void RefreshPorts()
    {
        var state = _plugin.ReadState();
        _portCombo.Items.Clear();
        _portCombo.Items.Add("Auto-detect");
        foreach (var port in SerialLink.AvailablePorts()) _portCombo.Items.Add(port);
        _portCombo.SelectedItem = state.PortMode == "Manual" && !string.IsNullOrEmpty(state.ManualPort)
            ? state.ManualPort
            : "Auto-detect";
    }

    private void UpdateStatus() => _status.Text = _plugin.Status;
}
