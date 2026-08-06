using System.Windows;

namespace DreamTray;

/// <summary>
/// Describes a kind of widget the user can add to the main panel. Built-in widgets
/// and plugin-contributed widgets are registered through the same interface, so
/// the panel treats them identically.
/// </summary>
public interface IWidgetFactory
{
    /// <summary>Stable id for this widget type (e.g. "core.brightness"). Persisted.</summary>
    string TypeId { get; }
    string DisplayName { get; }
    string Description { get; }

    /// <summary>Segoe Fluent Icons glyph shown in the "add widget" picker, e.g. "".</summary>
    string Glyph { get; }

    /// <summary>
    /// False when the machine cannot support it (no battery, no TDP backend, …) —
    /// such widgets are hidden from the picker instead of showing dead controls.
    /// </summary>
    bool IsAvailable(IPluginHost host) => true;

    /// <summary>Can the user add more than one? True for e.g. a per-display widget.</summary>
    bool AllowMultiple => false;

    IWidget Create(IWidgetContext context);
}

/// <summary>A live widget instance placed on the panel.</summary>
public interface IWidget : IDisposable
{
    /// <summary>Title shown in the widget's header.</summary>
    string Title { get; }

    /// <summary>The widget body. Built once and reused while the panel is alive.</summary>
    FrameworkElement View { get; }

    /// <summary>
    /// Optional element parked at the right-hand end of the title row, for a short
    /// piece of state that qualifies the whole widget ("On battery", "Connected").
    /// It costs no extra height, unlike a line of its own in the body. Built once
    /// with the widget; update it in place rather than returning a new instance.
    /// </summary>
    FrameworkElement? HeaderAccessory => null;

    /// <summary>
    /// Per-instance settings UI, shown in a flyout from the widget's "…" button.
    /// Return null when the widget has nothing to configure.
    /// </summary>
    FrameworkElement? CreateSettingsView() => null;

    /// <summary>
    /// Called when the panel opens/closes. Widgets should only subscribe to sensors
    /// while visible — this is what keeps idle CPU at zero.
    /// </summary>
    void OnVisibilityChanged(bool visible) { }

    /// <summary>
    /// Called once per second while the panel is closed, but only if the widget
    /// asked for it via <see cref="WantsBackgroundWork"/> (e.g. auto-TDP rules).
    /// </summary>
    void OnBackgroundTick(SystemSnapshot snapshot) { }

    /// <summary>
    /// True when this widget has rules that must run with the panel closed (auto
    /// TDP on AC/DC, auto theme on battery, …). Costs a shared 1 Hz subscription.
    /// </summary>
    bool WantsBackgroundWork => false;
}

/// <summary>What a widget instance is given when it is created.</summary>
public interface IWidgetContext
{
    IPluginHost Host { get; }

    /// <summary>Settings scoped to *this* widget instance, not the widget type.</summary>
    IStorage Storage { get; }

    /// <summary>Unique id of this placed instance.</summary>
    string InstanceId { get; }

    /// <summary>Ask the panel to re-read this widget's title.</summary>
    void RequestTitleUpdate();
}
