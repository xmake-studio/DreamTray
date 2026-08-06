using System.Windows;

namespace DreamTray;

/// <summary>
/// A DreamTray plugin. Drop a folder under <c>plugins\&lt;name&gt;\</c> containing the
/// plugin DLL; DreamTray finds every public non-abstract type implementing this
/// interface and instantiates it through its parameterless constructor.
///
/// Lifecycle: ctor → <see cref="Initialize"/> → <see cref="Start"/> when enabled →
/// <see cref="Stop"/> when disabled or on shutdown → Dispose.
/// <see cref="Start"/>/<see cref="Stop"/> may be called repeatedly as the user
/// toggles the plugin, so both must be idempotent.
/// </summary>
public interface IDreamPlugin : IDisposable
{
    /// <summary>Stable unique id — used as the settings key, so never change it.</summary>
    string Id { get; }
    string Name { get; }
    string Description { get; }
    string Version { get; }

    /// <summary>Called once, before <see cref="Start"/>. Do not touch hardware here.</summary>
    void Initialize(IPluginHost host);

    /// <summary>Begin doing work. Called when the plugin is enabled.</summary>
    void Start();

    /// <summary>Release devices/threads. Must leave the plugin restartable.</summary>
    void Stop();

    /// <summary>
    /// Settings UI shown in the plugin's page, or null when there is nothing to
    /// configure. Called on the UI thread; a fresh element each time.
    /// </summary>
    FrameworkElement? CreateSettingsView();

    /// <summary>Widgets this plugin contributes to the main panel. May be empty.</summary>
    IEnumerable<IWidgetFactory> Widgets { get; }
}

/// <summary>
/// Convenience base class: implements the boilerplate so a plugin only overrides
/// what it needs.
/// </summary>
public abstract class DreamPluginBase : IDreamPlugin
{
    protected IPluginHost Host { get; private set; } = null!;

    public abstract string Id { get; }
    public abstract string Name { get; }
    public virtual string Description => "";
    public virtual string Version => "1.0";

    public virtual void Initialize(IPluginHost host) => Host = host;
    public virtual void Start() { }
    public virtual void Stop() { }
    public virtual FrameworkElement? CreateSettingsView() => null;
    public virtual IEnumerable<IWidgetFactory> Widgets => [];
    public virtual void Dispose() => GC.SuppressFinalize(this);
}
