using System.Collections.ObjectModel;
using DreamTray.Logging;
using DreamTray.Settings;

namespace DreamTray.App.Widgets;

/// <summary>One widget placed on the panel: its persisted record plus the live instance.</summary>
internal sealed class WidgetInstance(WidgetPlacement placement, IWidgetFactory factory, IWidget widget)
    : IDisposable
{
    public WidgetPlacement Placement { get; } = placement;
    public IWidgetFactory Factory { get; } = factory;
    public IWidget Widget { get; } = widget;
    public string InstanceId => Placement.InstanceId;

    public void Dispose()
    {
        try { Widget.Dispose(); } catch (Exception ex) { Log.Write($"widget dispose threw: {ex.Message}"); }
    }
}

/// <summary>
/// Owns the placed widgets: creation, ordering, removal and persistence.
///
/// Two subtleties live here. First, visibility: widgets only subscribe to sensors
/// while the panel is open, so a closed panel polls nothing. Second, background
/// rules: a widget that declares <see cref="IWidget.WantsBackgroundWork"/> (auto
/// TDP, auto theme on battery) still needs to run with the panel closed, so the
/// manager keeps exactly one shared low-rate subscription alive for that set.
/// </summary>
internal sealed class WidgetManager(AppServices services, WidgetRegistry registry) : IDisposable
{
    private readonly ObservableCollection<WidgetInstance> _instances = [];
    private IDisposable? _backgroundSubscription;
    private bool _panelVisible;

    public ObservableCollection<WidgetInstance> Instances => _instances;

    /// <summary>Raised after add/remove/reorder so the panel can rebuild its list.</summary>
    public event Action? LayoutChanged;

    private void RaiseLayoutChanged() => LayoutChanged?.Invoke();

    // ---------------------------------------------------------------- loading

    public void Load()
    {
        var settings = services.Settings.Current;

        if (!settings.Initialised)
        {
            settings.Initialised = true;
            settings.Widgets = WidgetRegistry.DefaultLayout
                .Select(id => new WidgetPlacement { TypeId = id })
                .ToList();
            services.Settings.Save();
        }

        bool migrated = false;
        foreach (var placement in settings.Widgets.ToList())
        {
            // Rewrite a historical TypeId in place so the file stops carrying it.
            var canonical = WidgetRegistry.CanonicalTypeId(placement.TypeId);
            if (canonical != placement.TypeId)
            {
                Log.Write($"widget type '{placement.TypeId}' renamed to '{canonical}'");
                placement.TypeId = canonical;
                migrated = true;
            }

            if (!TryCreate(placement, out var instance))
            {
                // The widget's type is gone (plugin disabled or uninstalled). Keep the
                // placement in the file so re-enabling the plugin restores it intact.
                Log.Write($"widget type '{placement.TypeId}' not available — placement kept but not shown");
                continue;
            }
            _instances.Add(instance!);
        }

        if (migrated) services.Settings.Save();
        UpdateBackgroundSubscription();
    }

    private bool TryCreate(WidgetPlacement placement, out WidgetInstance? instance)
    {
        instance = null;
        var factory = registry.Find(placement.TypeId);
        if (factory == null) return false;

        try
        {
            var storage = services.Settings.Scope(placement.Settings);
            var host = services.CreateHost(storage);
            var context = new WidgetContext(host, storage, placement.InstanceId, this);
            var widget = factory.Create(context);
            instance = new WidgetInstance(placement, factory, widget);
            context.Bind(instance);
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"widget '{placement.TypeId}' failed to create: {ex}");
            return false;
        }
    }

    // ---------------------------------------------------------------- mutations

    public bool Add(string typeId)
    {
        var factory = registry.Find(typeId);
        if (factory == null) return false;
        if (!factory.AllowMultiple && _instances.Any(i => i.Factory.TypeId == typeId)) return false;

        var placement = new WidgetPlacement { TypeId = typeId };
        if (!TryCreate(placement, out var instance)) return false;

        services.Settings.Current.Widgets.Add(placement);
        _instances.Add(instance!);
        instance!.Widget.OnVisibilityChanged(_panelVisible);

        Persist();
        return true;
    }

    public void Remove(string instanceId)
    {
        var instance = _instances.FirstOrDefault(i => i.InstanceId == instanceId);
        if (instance == null) return;

        _instances.Remove(instance);
        services.Settings.Current.Widgets.RemoveAll(p => p.InstanceId == instanceId);
        instance.Widget.OnVisibilityChanged(false);
        instance.Dispose();

        Persist();
    }

    /// <summary>Move a widget to a new index (drag-reorder on the panel).</summary>
    public void Move(int fromIndex, int toIndex)
    {
        if (fromIndex == toIndex) return;
        if (fromIndex < 0 || fromIndex >= _instances.Count) return;
        toIndex = Math.Clamp(toIndex, 0, _instances.Count - 1);

        _instances.Move(fromIndex, toIndex);

        // Rewrite the persisted order from the live order — the settings list can
        // also hold placements for unavailable types, which must keep their slots.
        var live = _instances.Select(i => i.Placement).ToList();
        var all = services.Settings.Current.Widgets;
        var orphans = all.Where(p => live.All(l => l.InstanceId != p.InstanceId)).ToList();
        all.Clear();
        all.AddRange(live);
        all.AddRange(orphans);

        Persist();
    }

    private void Persist()
    {
        services.Settings.Save();
        UpdateBackgroundSubscription();
        RaiseLayoutChanged();
    }

    // ---------------------------------------------------------------- visibility

    /// <summary>
    /// Tell every widget whether the panel is on screen. This is the switch that
    /// keeps idle cost at zero: hidden widgets release their sensor subscriptions,
    /// and with none left the sampler shuts its driver down.
    /// </summary>
    public void SetPanelVisible(bool visible)
    {
        if (_panelVisible == visible) return;
        _panelVisible = visible;

        foreach (var instance in _instances)
        {
            try { instance.Widget.OnVisibilityChanged(visible); }
            catch (Exception ex) { Log.Write($"widget visibility threw: {ex.Message}"); }
        }
    }

    /// <summary>
    /// Keep one 5-second subscription alive iff some widget has background rules.
    /// Five seconds is fast enough for "switch theme when unplugged" and slow
    /// enough to be invisible in Task Manager.
    /// </summary>
    private void UpdateBackgroundSubscription()
    {
        bool wanted = _instances.Any(i => SafeWantsBackground(i.Widget));

        if (!wanted)
        {
            _backgroundSubscription?.Dispose();
            _backgroundSubscription = null;
            return;
        }
        _backgroundSubscription ??= services.Sensors.Subscribe(TimeSpan.FromSeconds(5), snapshot =>
        {
            foreach (var instance in _instances)
            {
                if (!SafeWantsBackground(instance.Widget)) continue;
                try { instance.Widget.OnBackgroundTick(snapshot); }
                catch (Exception ex) { Log.Write($"widget background tick threw: {ex.Message}"); }
            }
        });
    }

    private static bool SafeWantsBackground(IWidget widget)
    {
        try { return widget.WantsBackgroundWork; }
        catch { return false; }
    }

    public void Dispose()
    {
        _backgroundSubscription?.Dispose();
        foreach (var instance in _instances) instance.Dispose();
        _instances.Clear();
    }

    /// <summary>Per-instance context handed to a widget at construction.</summary>
    private sealed class WidgetContext(
        IPluginHost host, IStorage storage, string instanceId, WidgetManager owner) : IWidgetContext
    {
        private WidgetInstance? _instance;

        public IPluginHost Host => host;
        public IStorage Storage => storage;
        public string InstanceId => instanceId;

        /// <summary>Called once the instance exists — the widget is built before it.</summary>
        public void Bind(WidgetInstance instance) => _instance = instance;

        public void RequestTitleUpdate()
        {
            if (_instance != null) owner.RaiseLayoutChanged();
        }
    }
}
