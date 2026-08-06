using DreamTray.Settings;
using DreamTray.Theme;

namespace DreamTray.App.Widgets;

/// <summary>
/// The one place widgets reach app-level state.
///
/// Most widget settings are per-instance and go through <see cref="IStorage"/>.
/// A few — the TDP policy, the app's theme preference — are global by nature:
/// the Settings window edits the same values, and they must apply whether or not
/// the corresponding widget is on the panel. Rather than thread AppServices
/// through the widget contract (which plugins also implement), those widgets go
/// through this small, explicit surface.
/// </summary>
internal static class AppState
{
    private static AppServices? _services;

    public static void Attach(AppServices services) => _services = services;

    /// <summary>APU power-limit policy — shared with the Settings window.</summary>
    public static TdpSettings Tdp => _services?.Settings.Current.Tdp ?? new TdpSettings();

    public static void Save() => _services?.Settings.Save();

    /// <summary>Push a changed TDP policy into the live service.</summary>
    public static void ApplyTdpSettings() => _services?.ApplyTdpSettings();

    public static ThemePreference ThemePreference =>
        _services?.Theme.Preference ?? ThemePreference.System;

    public static void SetThemePreference(ThemePreference preference)
    {
        if (_services == null) return;
        _services.Theme.Preference = preference;
        _services.Settings.Current.Theme = preference.ToString();
        _services.Settings.Save();
    }
}
