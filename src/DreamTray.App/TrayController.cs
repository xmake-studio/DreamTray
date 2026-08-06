using System.Windows;
using System.Windows.Controls;
using DreamTray.App.Interop;
using DreamTray.App.Views;

namespace DreamTray.App;

/// <summary>
/// Wires the tray icon to the panel and the context menu, and keeps the icon's
/// colour and tooltip in step with the system.
///
/// The panel window is created once and hidden rather than closed, so opening it
/// is instant and widgets keep their state; widgets are told about visibility so
/// they can drop their sensor subscriptions while hidden.
/// </summary>
internal sealed class TrayController : IDisposable
{
    private readonly AppServices _services;
    private TrayIcon? _icon;
    private PanelWindow? _panel;
    private SettingsWindow? _settings;
    private ContextMenu? _menu;

    public TrayController(AppServices services) => _services = services;

    public void Start()
    {
        _icon = new TrayIcon("DreamTray", lightIcon: _services.Theme.TrayUsesDark);
        _icon.Activated += TogglePanel;
        _icon.ContextMenuRequested += ShowContextMenu;

        // The tray icon follows the *taskbar* theme, which is a separate Windows
        // setting from the app theme — a light taskbar needs a black gear.
        _services.Theme.TrayThemeChanged += () => _icon?.SetLight(_services.Theme.TrayUsesDark);

        _services.NotificationSink = (title, message) => _icon?.ShowBalloon(title, message);
    }

    // ---------------------------------------------------------------- panel

    /// <summary>Debounce for the hide-then-click sequence described on LastHiddenTicks.</summary>
    private const int ReopenGuardMs = 300;

    private void TogglePanel()
    {
        if (_panel is { IsVisible: true })
        {
            _panel.HidePanel();
            return;
        }
        if (_panel != null && Environment.TickCount64 - _panel.LastHiddenTicks < ReopenGuardMs)
            return; // the click that closed it; do not bounce straight back open
        ShowPanel();
    }

    public void ShowPanel()
    {
        _panel ??= new PanelWindow(_services, OpenSettings);
        _panel.ShowNear(_icon?.GetIconRect() ?? Rect.Empty);
    }

    public void OpenSettings()
    {
        _panel?.HidePanel();

        if (_settings is { IsLoaded: true })
        {
            _settings.Activate();
            return;
        }
        _settings = new SettingsWindow(_services);
        _settings.Closed += (_, _) => _settings = null;
        _settings.Show();
        _settings.Activate();
    }

    // ---------------------------------------------------------------- menu

    /// <summary>
    /// A WPF context menu placed at the mouse. It needs a focusable owner or it
    /// will not dismiss when the user clicks elsewhere, which is what
    /// <see cref="MenuHostWindow"/> provides.
    /// </summary>
    private void ShowContextMenu()
    {
        _menu ??= BuildMenu();
        MenuHostWindow.ShowMenu(_menu);
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu { StaysOpen = false };

        menu.Items.Add(MenuItemFor("Open panel", "", ShowPanel));
        menu.Items.Add(MenuItemFor("Settings", "", OpenSettings));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItemFor("Open settings folder", "", () =>
            OpenPath(DreamTray.Settings.SettingsStore.Folder)));
        menu.Items.Add(MenuItemFor("Open log", "", () => OpenPath(Logging.Log.FilePath)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItemFor("Exit", "", App.Quit));
        return menu;
    }

    private static MenuItem MenuItemFor(string header, string glyph, Action action)
    {
        var item = new MenuItem
        {
            Header = header,
            Icon = new TextBlock
            {
                Text = glyph,
                FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 13,
            },
        };
        item.Click += (_, _) => action();
        return item;
    }

    private static void OpenPath(string path)
    {
        try
        {
            Logging.Log.Flush();
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex) { Logging.Log.Write($"could not open {path}: {ex.Message}"); }
    }

    public void Dispose()
    {
        _panel?.Close();
        _settings?.Close();
        _icon?.Dispose();
    }
}
