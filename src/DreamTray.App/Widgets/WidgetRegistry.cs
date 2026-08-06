using DreamTray.App.Widgets.BuiltIn;

namespace DreamTray.App.Widgets;

/// <summary>
/// The catalogue of widget types the panel can place: the built-ins plus whatever
/// the enabled plugins contribute. Built-ins are ordinary
/// <see cref="IWidgetFactory"/> implementations — the panel has no notion of
/// "built-in" versus "from a plugin", which is what keeps the plugin API honest.
/// </summary>
internal sealed class WidgetRegistry(AppServices services)
{
    private readonly IWidgetFactory[] _builtIn =
    [
        new BrightnessWidgetFactory(),
        new TdpWidgetFactory(),
        new ThemeWidgetFactory(),
        new BatteryTimeWidgetFactory(),
        new TemperatureWidgetFactory(),
        new ClocksWidgetFactory(),
        new PowerRailsWidgetFactory(),
        new LoadWidgetFactory(),
        new DisplayModeWidgetFactory(),
        new SleepWidgetFactory(),
    ];

    /// <summary>Every registered factory, whether or not this machine supports it.</summary>
    public IEnumerable<IWidgetFactory> All =>
        _builtIn.Concat(services.Plugins.WidgetFactories);

    /// <summary>Factories worth offering in the picker on this machine.</summary>
    public IEnumerable<IWidgetFactory> Available(IPluginHost host)
    {
        foreach (var f in All)
        {
            bool ok;
            try { ok = f.IsAvailable(host); }
            catch { ok = false; } // a plugin's probe throwing must not empty the picker
            if (ok) yield return f;
        }
    }

    public IWidgetFactory? Find(string typeId) =>
        All.FirstOrDefault(f => f.TypeId == CanonicalTypeId(typeId));

    /// <summary>
    /// TypeIds that shipped under an older name, mapped to what they are called now.
    /// A placement whose type cannot be resolved is kept but not shown, so without
    /// this a rename would make the widget silently vanish from an existing setup.
    /// Entries here are permanent: someone's settings file may be arbitrarily old.
    /// </summary>
    private static readonly Dictionary<string, string> Renamed = new()
    {
        ["core.power"] = PowerRailsWidgetFactory.Id,
    };

    /// <summary>The current TypeId for a possibly historical one.</summary>
    public static string CanonicalTypeId(string typeId) =>
        Renamed.TryGetValue(typeId, out var current) ? current : typeId;

    /// <summary>
    /// What a fresh install starts with: the three controls the app is named for,
    /// plus the power rails readout. Everything else is opt-in from the picker.
    /// </summary>
    public static string[] DefaultLayout =>
    [
        BrightnessWidgetFactory.Id,
        TdpWidgetFactory.Id,
        PowerRailsWidgetFactory.Id,
        ThemeWidgetFactory.Id,
    ];
}
