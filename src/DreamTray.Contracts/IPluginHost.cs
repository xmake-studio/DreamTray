namespace DreamTray;

/// <summary>
/// Everything DreamTray offers a plugin or widget. Obtained in
/// <see cref="IDreamPlugin.Initialize"/> and from <see cref="IWidgetContext"/>.
/// </summary>
public interface IPluginHost
{
    /// <summary>Most recent sensor reading, or null before the first sample.</summary>
    SystemSnapshot? Latest { get; }

    /// <summary>
    /// Ask the shared sampler for readings at (at least) <paramref name="interval"/>.
    /// The sampler only runs while at least one subscription is alive, and ticks at
    /// the fastest interval anyone asked for — so an idle tray costs nothing.
    /// Dispose the returned handle to stop consuming.
    /// </summary>
    /// <param name="onSample">Invoked on the UI thread once per tick.</param>
    IDisposable SubscribeSensors(TimeSpan interval, Action<SystemSnapshot> onSample);

    /// <summary>Per-plugin/per-widget JSON settings, persisted across restarts.</summary>
    IStorage Storage { get; }

    /// <summary>Display brightness, TDP, display modes and Windows theme control.</summary>
    IHardwareControl Hardware { get; }

    /// <summary>Current app/Windows theme, with a change notification.</summary>
    IThemeInfo Theme { get; }

    /// <summary>Append a line to the shared log file. Cheap and non-throwing.</summary>
    void Log(string message);

    /// <summary>Show a Windows toast-style tray balloon. Use sparingly.</summary>
    void Notify(string title, string message);
}

/// <summary>
/// A tiny key/value store backed by one JSON file per owner. Values are serialized
/// with System.Text.Json, so anything JSON-round-trippable works.
/// </summary>
public interface IStorage
{
    T Get<T>(string key, T fallback);
    void Set<T>(string key, T value);
    /// <summary>Flush to disk. Writes are already debounced; call this only when it matters.</summary>
    void Save();
}

/// <summary>Theme state, so plugin UI can match the rest of the app.</summary>
public interface IThemeInfo
{
    bool IsDark { get; }
    /// <summary>Raised on the UI thread after <see cref="IsDark"/> changes.</summary>
    event Action? Changed;
}
