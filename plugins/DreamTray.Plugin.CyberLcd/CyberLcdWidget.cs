using System.Windows;

namespace DreamTray.Plugins.CyberLcd;

/// <summary>
/// Puts the device's on/off switch and connection state on DreamTray's main
/// panel, so it can be silenced without opening the settings window.
/// </summary>
internal sealed class CyberLcdWidgetFactory(CyberLcdPlugin plugin) : IWidgetFactory
{
    public string TypeId => "cyberlcd.panel";
    public string DisplayName => "CyberLCD panel";
    public string Description => "Turn the LCD display on or off and see whether it is connected.";
    public string Glyph => "";

    public IWidget Create(IWidgetContext context) => new CyberLcdWidget(plugin, context);
}

internal sealed class CyberLcdWidget : IWidget
{
    private readonly CyberLcdPlugin _plugin;
    private readonly System.Windows.Controls.TextBlock _status = PluginUi.Caption("");
    private readonly FrameworkElement _view;

    public CyberLcdWidget(CyberLcdPlugin plugin, IWidgetContext context)
    {
        _plugin = plugin;
        var state = plugin.ReadState();

        var power = PluginUi.Switch(state.Power, plugin.SetDevicePower);

        _view = PluginUi.Stack(
            PluginUi.LabelRow("Panel power", power, topMargin: 0),
            _status);

        UpdateStatus();
        plugin.StatusChanged += UpdateStatus;
    }

    public string Title => "CyberLCD";
    public FrameworkElement View => _view;

    /// <summary>
    /// No sensor subscription of its own: the plugin already streams at 1 Hz while
    /// enabled, and this widget only mirrors its connection state.
    /// </summary>
    public void OnVisibilityChanged(bool visible)
    {
        if (visible) UpdateStatus();
    }

    private void UpdateStatus() => _status.Text = _plugin.Status;

    public void Dispose() => _plugin.StatusChanged -= UpdateStatus;
}
