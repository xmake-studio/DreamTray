using System.Windows;

namespace DreamTray.App.Widgets;

/// <summary>
/// Shared plumbing for the built-in widgets: lazy view construction and, more
/// importantly, sensor subscriptions tied to panel visibility.
///
/// A widget declares <see cref="NeedsSensors"/> and gets <see cref="OnSample"/>
/// calls only while the panel is on screen. Nothing polls behind a closed panel
/// unless the widget explicitly opts into background work.
/// </summary>
internal abstract class WidgetBase(IWidgetContext context) : IWidget
{
    protected IWidgetContext Context { get; } = context;
    protected IPluginHost Host => Context.Host;
    protected IStorage Storage => Context.Storage;
    protected IHardwareControl Hardware => Context.Host.Hardware;

    private FrameworkElement? _view;
    private IDisposable? _subscription;

    public abstract string Title { get; }

    public FrameworkElement View => _view ??= BuildView();

    /// <summary>Optional status element for the title row. See <see cref="IWidget"/>.</summary>
    public virtual FrameworkElement? HeaderAccessory => null;

    /// <summary>Build the widget body. Called once, on the UI thread.</summary>
    protected abstract FrameworkElement BuildView();

    /// <summary>True when the widget shows live values and needs sensor ticks.</summary>
    protected virtual bool NeedsSensors => false;

    /// <summary>How often this widget wants data while visible.</summary>
    protected virtual TimeSpan SampleInterval => TimeSpan.FromSeconds(1);

    /// <summary>A new reading arrived (UI thread). Only fires while visible.</summary>
    protected virtual void OnSample(SystemSnapshot snapshot) { }

    /// <summary>Called when the panel opens, before the first sample.</summary>
    protected virtual void OnShown() { }

    public virtual void OnVisibilityChanged(bool visible)
    {
        if (visible)
        {
            OnShown();
            if (NeedsSensors && _subscription == null)
            {
                _subscription = Host.SubscribeSensors(SampleInterval, OnSample);
                // Seed from the last known reading so the widget is never blank for
                // a second while the sampler spins up.
                if (Host.Latest != null) OnSample(Host.Latest);
            }
        }
        else
        {
            _subscription?.Dispose();
            _subscription = null;
        }
    }

    public virtual FrameworkElement? CreateSettingsView() => null;
    public virtual bool WantsBackgroundWork => false;
    public virtual void OnBackgroundTick(SystemSnapshot snapshot) { }

    public virtual void Dispose()
    {
        _subscription?.Dispose();
        _subscription = null;
    }
}
