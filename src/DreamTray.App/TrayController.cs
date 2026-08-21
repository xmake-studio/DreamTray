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

        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        Logging.Log.Write($"displays at startup: {WindowEffects.DescribeMonitors()}");
    }

    // ---------------------------------------------------------------- display changes

    /// <summary>
    /// Throw the panel away and build a fresh one whenever the display configuration
    /// changes.
    ///
    /// The panel is created once and hidden rather than closed, which is what makes
    /// opening it instant — and which is also why it cannot survive this. WPF caches
    /// the monitor scale per window and refreshes it only from WM_DPICHANGED, and
    /// Windows does not send WM_DPICHANGED to hidden windows. A resolution change made
    /// while the panel is closed therefore leaves the window permanently convinced of
    /// the old scale, and every dimension it computes scaled by the ratio of the two.
    /// There is no API for correcting that cache; a new window is the fix, and it is
    /// cheap here because it happens at idle rather than under a click.
    ///
    /// Raised on a system thread, so everything real happens back on the dispatcher.
    /// </summary>
    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        Application.Current?.Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            new Action(() =>
            {
                Logging.Log.Write($"display settings changed: {WindowEffects.DescribeMonitors()}");
                RebuildPanel("display change");
            }));

    /// <summary>
    /// Replace the panel window, unless it is on screen — yanking a flyout out from
    /// under the user is worse than the stale scale it would be fixing, and the check
    /// in <see cref="ShowPanel"/> catches whatever this skips on the next open anyway.
    /// </summary>
    private void RebuildPanel(string reason)
    {
        if (_panel == null || !_panel.HasWindowHandle) return;
        if (_panel.IsVisible)
        {
            Logging.Log.Write($"panel rebuild deferred ({reason}): panel is on screen");
            return;
        }

        Logging.Log.Write($"panel rebuilt ({reason})");
        _panel.Close();
        _panel = null;
        Prewarm();
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

        // Last line of defence for the scale cache. DisplaySettingsChanged catches
        // almost every case, but it does not fire for a monitor swapped on the KVM,
        // a dock attached while asleep, or a scale change applied to a window that
        // happened to be visible at the time — and any one of those leaves a panel
        // that opens at the wrong size. Asking the window whether it still agrees with
        // its own monitor costs one call and cannot be fooled by whichever
        // notification went missing.
        if (_panel != null && _panel.IsDpiStale())
        {
            Logging.Log.Write("panel scale disagrees with its monitor — rebuilding before open");
            RebuildPanel("stale dpi");
        }

        bool built = false;
        if (_panel == null)
        {
            // Prewarm should have done this at idle; getting here means the click beat
            // it, and this one open pays for the whole panel.
            _panel = new PanelWindow(_services, OpenSettings);
            _panel.DismissedByCaller = PointerOverIcon;
            built = true;
        }
        long afterBuild = clock.ElapsedMilliseconds;

        // A cross-process call into explorer's tray, and a blocking one: if explorer
        // is busy — which on a loaded machine it is — this waits for it on the UI
        // thread, between the click and anything at all happening. It is not ours to
        // make faster, so it is measured on its own rather than folded into the
        // panel's time, and it is the first number to look at in a slow open.
        var iconRect = _icon?.GetIconRect() ?? Rect.Empty;
        long afterRect = clock.ElapsedMilliseconds;

        // The panel finishes the trace and writes it, because the open is not over
        // when this call returns: the window is still cloaked at that point and only
        // becomes visible a frame or two later.
        _panel.ShowNear(
            iconRect,
            $"build {afterBuild}{(built ? "" : " cached")}, iconrect {afterRect - afterBuild}");
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
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _panel?.Close();
        _settings?.Close();
        _icon?.Dispose();
    }
}
