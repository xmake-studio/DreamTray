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

    /// <summary>
    /// Raised on the UI thread when the user picks a different Windows accent colour.
    /// Separate from <see cref="Changed"/> because the light/dark subscribers rebuild
    /// their content, and an accent swap only needs the brushes repainted.
    /// </summary>
    public event Action? AccentChanged;

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
        long accent = ReadAccentStamp();

        bool resolved = _preference switch
        {
            ThemePreference.Light => false,
            ThemePreference.Dark => true,
            _ => appsDark,
        };

        bool themeChanged = resolved != IsDark;
        bool trayChanged = trayDark != TrayUsesDark;
        // A zero on either side means the palette could not be read; hold the last
        // known stamp rather than reporting a change we cannot substantiate. The
        // first Refresh runs from the constructor, before anyone can subscribe, so
        // it seeds the stamp either way.
        bool accentChanged = accent != 0 && _accentStamp != 0 && accent != _accentStamp;
        if (accent == 0) accent = _accentStamp;

        IsDark = resolved;
        WindowsAppsUseDark = appsDark;
        TrayUsesDark = trayDark;
        _accentStamp = accent;

        if (themeChanged) Changed?.Invoke();
        if (trayChanged) TrayThemeChanged?.Invoke();
        if (accentChanged) AccentChanged?.Invoke();
    }

    private long _accentStamp;

    /// <summary>
    /// A value that changes whenever the accent does, without this layer having to
    /// know how the palette is laid out — that belongs to whoever paints with it.
    /// Zero means "could not read", which is treated as no change so a transient
    /// registry failure never causes a spurious repaint.
    /// </summary>
    private static long ReadAccentStamp()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent");
            if (key?.GetValue("AccentPalette") is byte[] palette && palette.Length > 0)
            {
                // FNV-1a. Only equality matters here, so anything with a low enough
                // collision rate over 32 bytes will do.
                long hash = unchecked((long)0xCBF29CE484222325);
                foreach (byte b in palette)
                    hash = unchecked((hash ^ b) * 0x100000001B3);
                return hash == 0 ? 1 : hash;
            }
        }
        catch { /* treated as unreadable */ }
        return 0;
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

    private bool _switching;

    /// <summary>Apply both Windows colour modes through the shell theme manager.</summary>
    public bool SetWindowsDarkMode(bool dark)
    {
        if (!_dispatcher.CheckAccess())
            return _dispatcher.Invoke(() => SetWindowsDarkMode(dark));
        // Shell COM calls may pump messages. Prevent a reentrant theme application.
        if (_switching) return false;
        _switching = true;
        try
        {
            WindowsThemeSwitcher.Apply(dark);
            Refresh();
            bool applied = WindowsAppsUseDark == dark && TrayUsesDark == dark;
            Logging.Log.Write($"theme: shell apply {(dark ? "dark" : "light")}, verified={applied}");
            return applied;
        }
        catch (Exception ex)
        {
            Logging.Log.Write($"theme: shell apply failed: {ex}");
            Refresh();
            return false;
        }
        finally { _switching = false; }
    }

    public void Dispose() => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
}
