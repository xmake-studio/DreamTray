using System.Reflection;
using System.Runtime.Loader;
using DreamTray.Settings;

namespace DreamTray.Plugins;

/// <summary>A discovered plugin plus everything the app needs to manage it.</summary>
public sealed class LoadedPlugin
{
    public required IDreamPlugin Instance { get; init; }
    public required string FolderName { get; init; }
    public required string AssemblyPath { get; init; }
    public bool Enabled { get; internal set; }
    /// <summary>Non-null when the plugin threw; shown in the UI instead of its settings.</summary>
    public string? Error { get; internal set; }

    public string Id => Instance.Id;
    public string Name => Instance.Name;
}

/// <summary>
/// Finds, loads and toggles plugins.
///
/// Each plugin folder gets its own <see cref="AssemblyLoadContext"/> with a
/// resolver rooted at that folder, so two plugins can depend on different versions
/// of the same library without colliding. The contracts assembly is deliberately
/// *not* isolated — it is resolved from the host so that
/// <c>plugin is IDreamPlugin</c> is true across the boundary.
///
/// A plugin that throws during load is recorded with an error and skipped; one bad
/// DLL must not stop the tray from starting.
/// </summary>
public sealed class PluginManager(AppServices services, Action<string> log) : IDisposable
{
    private readonly List<LoadedPlugin> _plugins = [];

    public IReadOnlyList<LoadedPlugin> Plugins => _plugins;

    /// <summary>Widget factories contributed by all currently enabled plugins.</summary>
    public IEnumerable<IWidgetFactory> WidgetFactories =>
        _plugins.Where(p => p.Enabled && p.Error == null)
                .SelectMany(p => SafeWidgets(p));

    /// <summary>Raised when the enabled set changes, so the widget picker can refresh.</summary>
    public event Action? PluginsChanged;

    /// <summary>Where plugin folders live: <c>plugins\</c> next to the executable.</summary>
    public static string PluginsRoot => Path.Combine(AppContext.BaseDirectory, "plugins");

    /// <summary>Scan the plugins folder, instantiate everything, start what is enabled.</summary>
    public void LoadAll()
    {
        if (!Directory.Exists(PluginsRoot))
        {
            try { Directory.CreateDirectory(PluginsRoot); } catch { }
            return;
        }

        foreach (var folder in Directory.EnumerateDirectories(PluginsRoot))
        {
            foreach (var dll in Directory.EnumerateFiles(folder, "*.dll"))
            {
                // Skip the obvious non-plugin DLLs so we do not reflection-load
                // every dependency in the folder.
                string file = Path.GetFileName(dll);
                if (file.StartsWith("DreamTray.Contracts", StringComparison.OrdinalIgnoreCase)) continue;
                if (file.StartsWith("System.", StringComparison.OrdinalIgnoreCase)) continue;
                if (file.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase)) continue;
                TryLoadAssembly(dll, Path.GetFileName(folder));
            }
        }

        log($"plugins: {_plugins.Count} loaded from {PluginsRoot}");

        foreach (var p in _plugins)
        {
            var entry = GetEntry(p.Id);
            if (entry.Enabled) Enable(p, true, persist: false);
        }
    }

    private void TryLoadAssembly(string path, string folderName)
    {
        try
        {
            var context = new PluginLoadContext(path);
            var assembly = context.LoadFromAssemblyPath(path);

            foreach (var type in assembly.GetExportedTypes())
            {
                if (!typeof(IDreamPlugin).IsAssignableFrom(type)) continue;
                if (type.IsAbstract || type.IsInterface) continue;
                if (type.GetConstructor(Type.EmptyTypes) == null)
                {
                    log($"plugins: {type.FullName} has no parameterless constructor — skipped");
                    continue;
                }

                var instance = (IDreamPlugin)Activator.CreateInstance(type)!;
                if (_plugins.Any(p => p.Id == instance.Id))
                {
                    log($"plugins: duplicate id '{instance.Id}' in {path} — skipped");
                    continue;
                }

                var loaded = new LoadedPlugin
                {
                    Instance = instance,
                    FolderName = folderName,
                    AssemblyPath = path,
                    Enabled = false,
                };

                var entry = GetEntry(instance.Id);
                instance.Initialize(services.CreateHost(services.Settings.Scope(entry.Settings)));
                _plugins.Add(loaded);
                log($"plugins: found {instance.Name} ({instance.Id}) v{instance.Version}");
            }
        }
        catch (BadImageFormatException) { /* native or non-.NET DLL in the folder */ }
        catch (Exception ex)
        {
            log($"plugins: failed to load {path}: {ex.Message}");
        }
    }

    private PluginEntry GetEntry(string id)
    {
        var map = services.Settings.Current.Plugins;
        if (!map.TryGetValue(id, out var entry))
        {
            entry = new PluginEntry();
            map[id] = entry;
        }
        return entry;
    }

    public bool IsEnabled(string id) => GetEntry(id).Enabled;

    /// <summary>Turn a plugin on or off, persisting the choice.</summary>
    public void SetEnabled(string id, bool enabled)
    {
        var p = _plugins.FirstOrDefault(x => x.Id == id);
        if (p == null) return;
        Enable(p, enabled, persist: true);
    }

    private void Enable(LoadedPlugin p, bool enabled, bool persist)
    {
        try
        {
            if (enabled) { p.Instance.Start(); p.Error = null; }
            else p.Instance.Stop();
            p.Enabled = enabled;
        }
        catch (Exception ex)
        {
            p.Error = ex.Message;
            p.Enabled = false;
            log($"plugins: {p.Id} {(enabled ? "start" : "stop")} failed: {ex}");
        }

        if (persist)
        {
            GetEntry(p.Id).Enabled = p.Enabled;
            services.Settings.Save();
        }
        PluginsChanged?.Invoke();
    }

    private IEnumerable<IWidgetFactory> SafeWidgets(LoadedPlugin p)
    {
        try { return p.Instance.Widgets.ToList(); }
        catch (Exception ex)
        {
            log($"plugins: {p.Id} widget enumeration threw: {ex.Message}");
            return [];
        }
    }

    public void Dispose()
    {
        foreach (var p in _plugins)
        {
            try { p.Instance.Stop(); } catch { }
            try { p.Instance.Dispose(); } catch { }
        }
        _plugins.Clear();
    }
}

/// <summary>
/// Load context for one plugin folder. Assemblies the host already has (the
/// contracts, WPF, the BCL) resolve to the host's copy so types are shared;
/// anything else is loaded privately from the plugin's own folder.
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string pluginPath)
        : base(name: Path.GetFileNameWithoutExtension(pluginPath), isCollectible: false)
        => _resolver = new AssemblyDependencyResolver(pluginPath);

    protected override Assembly? Load(AssemblyName name)
    {
        // Shared contract types must come from the host, or the plugin's IDreamPlugin
        // would be a different type than the one the manager checks against.
        if (name.Name is "DreamTray.Contracts") return null;

        string? path = _resolver.ResolveAssemblyToPath(name);
        return path != null ? LoadFromAssemblyPath(path) : null;
    }

    protected override nint LoadUnmanagedDll(string name)
    {
        string? path = _resolver.ResolveUnmanagedDllToPath(name);
        return path != null ? LoadUnmanagedDllFromPath(path) : nint.Zero;
    }
}
