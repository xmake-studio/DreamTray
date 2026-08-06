using Microsoft.Win32;
using System.Windows.Threading;

namespace DreamTray.Theme;

/// <summary>Which theme the app paints itself in.</summary>
public enum ThemePreference
{
    /// <summary>Follow the Windows "app mode" setting (default).</summary>
    System,
    Light,
    Dark,
}

/// <summary>
/// Tracks the Windows theme and resolves it against the user's preference.
///
/// Windows exposes two separate switches under <c>…\Themes\Personalize</c>:
/// <c>AppsUseLightTheme</c> (what apps should follow) and
/// <c>SystemUsesLightTheme</c> (taskbar and tray). The window follows the first;
/// the tray icon has to follow the second, or a black gear lands on a black
/// taskbar. Both are surfaced here.
/// </summary>
public sealed class ThemeService : IThemeInfo, IDisposable
{
    private const string PersonalizeKey =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private readonly Dispatcher _dispatcher;
    private ThemePreference _preference = ThemePreference.System;

    public ThemeService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        Refresh();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    /// <summary>Resolved theme for app windows.</summary>
    public bool IsDark { get; private set; }

    /// <summary>Windows' own app-mode setting, ignoring the user's app preference.</summary>
    public bool WindowsAppsUseDark { get; private set; }

    /// <summary>Taskbar/tray theme — drives the tray icon's colour.</summary>
    public bool TrayUsesDark { get; private set; }

    public ThemePreference Preference
    {
        get => _preference;
        set { _preference = value; Refresh(); }
    }

    /// <summary>Raised on the UI thread when the resolved theme changes.</summary>
    public event Action? Changed;

    /// <summary>Raised on the UI thread when the taskbar theme changes.</summary>
    public event Action? TrayThemeChanged;

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle))
            return;
        // The registry is written slightly before the broadcast settles; a short
        // hop through the dispatcher is enough to read the new values.
        _dispatcher.BeginInvoke(DispatcherPriority.Background, Refresh);
    }

    private void Refresh()
    {
        bool appsDark = ReadFlag("AppsUseLightTheme");
        bool trayDark = ReadFlag("SystemUsesLightTheme");

        bool resolved = _preference switch
        {
            ThemePreference.Light => false,
            ThemePreference.Dark => true,
            _ => appsDark,
        };

        bool themeChanged = resolved != IsDark;
        bool trayChanged = trayDark != TrayUsesDark;

        IsDark = resolved;
        WindowsAppsUseDark = appsDark;
        TrayUsesDark = trayDark;

        if (themeChanged) Changed?.Invoke();
        if (trayChanged) TrayThemeChanged?.Invoke();
    }

    /// <summary>The registry stores "uses *light* theme", so dark is the inverse.</summary>
    private static bool ReadFlag(string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue(name) is int v && v == 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// Switch Windows itself to light or dark. Sets both flags so the taskbar and
    /// apps agree, then broadcasts the change so already-running apps repaint.
    /// </summary>
    public bool SetWindowsDarkMode(bool dark)
    {
        try
        {
            int light = dark ? 0 : 1;
            // The key has to be flushed and closed *before* the broadcast: registry
            // writes are lazy, and the secondary taskbars re-read the value the moment
            // they get the message. Broadcasting first hands them the stale one.
            using (var key = Registry.CurrentUser.CreateSubKey(PersonalizeKey, writable: true))
            {
                if (key == null) return false;
                key.SetValue("AppsUseLightTheme", light, RegistryValueKind.DWord);
                key.SetValue("SystemUsesLightTheme", light, RegistryValueKind.DWord);
                key.Flush();
            }

            BroadcastThemeChange();
            _dispatcher.BeginInvoke(DispatcherPriority.Background, Refresh);
            return true;
        }
        catch { return false; }
    }

    private const int WM_SETTINGCHANGE = 0x001A;
    private const int HWND_BROADCAST = 0xFFFF;
    private const int SMTO_ABORTIFHUNG = 0x0002;

    /// <summary>
    /// Tells running windows the colours changed. The system-wide broadcast covers
    /// ordinary apps and the primary taskbar, but the per-monitor taskbars
    /// (<c>Shell_SecondaryTrayWnd</c>) only repaint their foreground from it and
    /// re-read the background from the registry — so they get a direct message too,
    /// or a dark theme leaves white text on a white bar.
    /// </summary>
    private static void BroadcastThemeChange()
    {
        // Windows caches the resolved immersive colour set per process, and the
        // broadcast below only asks windows to repaint — it does not invalidate that
        // cache. Explorer's primary taskbar re-resolves anyway; the per-monitor ones
        // repaint from the cached set, giving new foreground on old background. This
        // flush is what Settings does and what we were missing.
        RefreshImmersiveColorPolicy();

        // Per-window timeout on a system-wide send. Explorer under load blows through
        // a short one, and SMTO_ABORTIFHUNG then drops the notification silently.
        SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE, nint.Zero, "ImmersiveColorSet",
                           SMTO_ABORTIFHUNG, 1000, out _);

        foreach (nint taskbar in FindTaskbars())
            SendMessageTimeout(taskbar, WM_SETTINGCHANGE, nint.Zero, "ImmersiveColorSet",
                               SMTO_ABORTIFHUNG, 1000, out _);
    }

    /// <summary>The primary taskbar plus one window per additional monitor.</summary>
    private static List<nint> FindTaskbars()
    {
        var found = new List<nint>();

        nint primary = FindWindow("Shell_TrayWnd", null);
        if (primary != nint.Zero) found.Add(primary);

        var buffer = new System.Text.StringBuilder(64);
        EnumWindows((hWnd, _) =>
        {
            buffer.Clear();
            if (GetClassName(hWnd, buffer, buffer.Capacity) > 0 &&
                buffer.ToString() == "Shell_SecondaryTrayWnd")
            {
                found.Add(hWnd);
            }
            return true;
        }, nint.Zero);

        return found;
    }

    /// <summary>
    /// Drops the cached immersive colour set so the next repaint re-resolves it.
    /// Exported from uxtheme.dll by ordinal only — it is undocumented and carries no
    /// name, so a future Windows build could drop it. Failing to flush costs us the
    /// old rendering bug, not a crash, so this stays best-effort.
    /// </summary>
    private static void RefreshImmersiveColorPolicy()
    {
        try { RefreshImmersiveColorPolicyState(); }
        catch (EntryPointNotFoundException) { }
        catch (DllNotFoundException) { }
    }

    [System.Runtime.InteropServices.DllImport("uxtheme.dll", EntryPoint = "#104", SetLastError = false)]
    private static extern void RefreshImmersiveColorPolicyState();

    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern nint SendMessageTimeout(nint hWnd, int msg, nint wParam, string lParam,
                                                   int flags, int timeout, out nint result);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern nint FindWindow(string? lpClassName, string? lpWindowName);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetClassName(nint hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    public void Dispose() => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
}
