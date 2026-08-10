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

    /// <summary>
    /// Open if closed, close if open — every time, with nothing debounced or
    /// swallowed. The panel does not dismiss itself when the tray icon takes focus
    /// (see DismissedByCaller), so this is the only thing that toggles it and there
    /// is no second dismissal to disambiguate.
    ///
    /// IsClosing, not just IsVisible: a panel playing its exit is on its way out and
    /// counts as closed, so a click during the animation turns it straight back
    /// round rather than being absorbed.
    /// </summary>
    private void TogglePanel()
    {
        if (_panel is { IsVisible: true, IsClosing: false }) _panel.HidePanel();
        else ShowPanel();
    }

    /// <summary>
    /// True when the panel is losing focus to a left-button press on the tray icon —
    /// the click this controller is about to toggle on. Both halves matter: the
    /// pointer alone would also swallow the dismissal when the user merely alt-tabs
    /// away with the cursor parked over the icon.
    /// </summary>
    private bool PointerOverIcon()
    {
        if ((GetAsyncKeyState(VK_LBUTTON) & 0x8000) == 0) return false;
        var rect = _icon?.GetIconRect() ?? Rect.Empty;
        return !rect.IsEmpty && rect.Contains(WindowEffects.GetCursorPosition());
    }

    private const int VK_LBUTTON = 0x01;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    public void ShowPanel()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        if (_panel == null)
        {
            _panel = new PanelWindow(_services, OpenSettings);
            _panel.DismissedByCaller = PointerOverIcon;
        }
        _panel.ShowNear(_icon?.GetIconRect() ?? Rect.Empty);
        // Everything above runs on the UI thread between the click and the panel
        // being composed, so anything slow in a widget's OnShown shows up here as a
        // late flyout. Logged only when it is long enough for a user to notice.
        if (clock.ElapsedMilliseconds >= 100)
            Logging.Log.Write($"panel open took {clock.ElapsedMilliseconds} ms on the UI thread");
    }

    /// <summary>
    /// Build the panel before the user asks for it.
    ///
    /// The window is created once and reused, so the cost of building it — the
    /// widgets, their views, the first pass over the WPF resource dictionaries, and
    /// the JIT for all of it — lands entirely on whichever click happens to be the
    /// first. That is the one open that is reliably slow. Doing it at idle after
    /// startup moves the cost to a moment when nobody is waiting.
    /// </summary>
    public void Prewarm() =>
        _panel ??= new PanelWindow(_services, OpenSettings) { DismissedByCaller = PointerOverIcon };

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
