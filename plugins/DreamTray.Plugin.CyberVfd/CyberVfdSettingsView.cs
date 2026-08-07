using System.Windows;
using System.Windows.Controls;

namespace DreamTray.Plugins.CyberVfd;

/// <summary>
/// The plugin's page in DreamTray's Settings window: connection state, port
/// selection and the panel's own controls (master power, backlight, hardware
/// dimming). Every change is sent to the device immediately and persisted, then
/// re-sent on the next connect so the panel always matches what is shown here.
/// </summary>
internal sealed class CyberVfdSettingsView : UserControl
{
    private readonly CyberVfdPlugin _plugin;
    private readonly TextBlock _status = PluginUi.Caption("");
    private readonly ComboBox _portCombo;

    public CyberVfdSettingsView(CyberVfdPlugin plugin)
    {
        _plugin = plugin;
        var state = plugin.ReadState();

        var ports = new List<string> { "Auto-detect" };
        ports.AddRange(SerialLink.AvailablePorts());

        string selected = state.Mode == "Manual" && !string.IsNullOrEmpty(state.Port)
            ? state.Port
            : "Auto-detect";

        _portCombo = PluginUi.Combo(ports, selected, choice =>
        {
            if (choice == "Auto-detect") _plugin.UsePortAuto();
            else _plugin.UsePort(choice);
        });

        var power = PluginUi.Switch(state.Power, _plugin.SetDevicePower);
        var backlight = PluginUi.Switch(state.Backlight, _plugin.SetBacklight);

        var brightnessValue = PluginUi.Value($"{Percent(state.Brightness)}%");
        var brightness = PluginUi.Slider(0, 255, state.Brightness, v =>
        {
            int raw = (int)v;
            brightnessValue.Text = $"{Percent(raw)}%";
            _plugin.SetBrightness(raw);
        });

        // The readout needs a gap of its own and a fixed slot: butted against the
        // slider it reads as part of the track, and it must not jog left and right
        // as the value crosses 9% and 99%.
        var brightnessRow = new StackPanel { Orientation = Orientation.Horizontal };
        brightnessValue.MinWidth = 40;
        brightnessValue.Margin = new Thickness(12, 0, 0, 0);
        brightnessValue.TextAlignment = TextAlignment.Right;
        brightnessValue.VerticalAlignment = VerticalAlignment.Center;
        brightnessRow.Children.Add(brightness);
        brightnessRow.Children.Add(brightnessValue);

        // The caption belongs to the row above it, so it carries a tighter gap than
        // the standard one PluginUi.Stack hands out.
        var powerNote = PluginUi.Caption("Off cuts the high-voltage supply and the backlight relay.");
        powerNote.Margin = new Thickness(0, 4, 0, 0);

        var rescan = PluginUi.Button("Re-scan ports", RefreshPorts);
        rescan.HorizontalAlignment = HorizontalAlignment.Left;
        rescan.Margin = new Thickness(0, 14, 0, 0);

        Content = PluginUi.Stack(
            _status,
            PluginUi.LabelRow("Serial port", _portCombo),
            PluginUi.LabelRow("Panel power", power),
            powerNote,
            PluginUi.LabelRow("Backlight", backlight),
            PluginUi.LabelRow("Panel brightness", brightnessRow),
            rescan);

        UpdateStatus();
        plugin.StatusChanged += UpdateStatus;
        Unloaded += (_, _) => plugin.StatusChanged -= UpdateStatus;
    }

    /// <summary>The panel register is 0..255; showing a percentage is friendlier.</summary>
    private static int Percent(int raw) => (int)Math.Round(raw * 100.0 / 255.0);

    private void RefreshPorts()
    {
        var state = _plugin.ReadState();
        _portCombo.Items.Clear();
        _portCombo.Items.Add("Auto-detect");
        foreach (var port in SerialLink.AvailablePorts()) _portCombo.Items.Add(port);
        _portCombo.SelectedItem = state.Mode == "Manual" && !string.IsNullOrEmpty(state.Port)
            ? state.Port
            : "Auto-detect";
    }

    private void UpdateStatus() => _status.Text = _plugin.Status;
}
