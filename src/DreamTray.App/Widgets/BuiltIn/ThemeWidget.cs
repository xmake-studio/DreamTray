using System.Windows;
using System.Windows.Controls;
using DreamTray.Theme;

namespace DreamTray.App.Widgets.BuiltIn;

internal sealed class ThemeWidgetFactory : IWidgetFactory
{
    public const string Id = "core.theme";
    public string TypeId => Id;
    public string DisplayName => "Dark theme";
    public string Description => "Switch Windows between light and dark, optionally on power source.";
    public string Glyph => "\uE793";
    public IWidget Create(IWidgetContext context) => new ThemeWidget(context);
}

/// <summary>
/// Light/dark switch. Flipping it changes the *Windows* theme, not just this app's —
/// a tray toggle that only recoloured its own panel would be a toy, and DreamTray
/// follows the system setting anyway.
///
/// The optional battery rule runs in the background so it still fires with the
/// panel closed.
/// </summary>
internal sealed class ThemeWidget(IWidgetContext context) : WidgetBase(context)
{
    private System.Windows.Controls.Primitives.ToggleButton? _toggle;
    private bool _suppress;
    private bool? _lastOnAc;

    // The title carries the switch's meaning: with the toggle on the title row there
    // is no label of its own to say which way is which.
    public override string Title => "Dark theme";

    /// <summary>What to do with the Windows theme when the power source changes.</summary>
    private enum ThemeAction { None, Dark, Light }

    private ThemeAction OnBattery
    {
        get => Read("onBatteryAction", LegacyBattery());
        set => Storage.Set("onBatteryAction", value.ToString());
    }

    private ThemeAction OnCharger
    {
        get => Read("onChargerAction", LegacyCharger());
        set => Storage.Set("onChargerAction", value.ToString());
    }

    private ThemeAction Read(string key, ThemeAction fallback) =>
        Enum.TryParse(Storage.Get(key, ""), out ThemeAction a) ? a : fallback;

    // Carry the old two-switch rule over so an upgrade keeps behaving the same.
    private ThemeAction LegacyBattery() =>
        Storage.Get("autoLightOnBattery", false) ? ThemeAction.Light : ThemeAction.None;

    private ThemeAction LegacyCharger() =>
        Storage.Get("autoLightOnBattery", false) && Storage.Get("restoreOnCharger", true)
            ? ThemeAction.Dark : ThemeAction.None;

    /// <summary>
    /// The whole widget is one switch, so it rides on the title row: "Theme" plus a
    /// toggle says everything a labelled row below would, in half the height.
    /// </summary>
    public override FrameworkElement? HeaderAccessory
    {
        get
        {
            if (_toggle == null)
            {
                _toggle = Ui.Switch(Host.Theme.IsDark, dark =>
                {
                    if (_suppress) return;
                    Hardware.SetWindowsDarkMode(dark);
                });
                _toggle.ToolTip = "Dark mode";

                // Keep the switch honest if the theme changes from anywhere else.
                Host.Theme.Changed += SyncFromSystem;
            }
            return _toggle;
        }
    }

    /// <summary>
    /// No body: the switch is in the header. Collapsed rather than empty so the card
    /// drops the gap it would otherwise leave under the title.
    /// </summary>
    protected override FrameworkElement BuildView() =>
        new StackPanel { Visibility = Visibility.Collapsed };

    private void SyncFromSystem()
    {
        if (_toggle == null) return;
        _suppress = true;
        _toggle.IsChecked = Host.Theme.IsDark;
        _suppress = false;
    }

    // ---- background rule ----

    public override bool WantsBackgroundWork =>
        Hardware.HasBattery && (OnBattery != ThemeAction.None || OnCharger != ThemeAction.None);

    public override void OnBackgroundTick(SystemSnapshot snapshot)
    {
        if (!WantsBackgroundWork || !snapshot.BatteryPresent) return;

        // Act on transitions only: forcing the theme every tick would fight the user
        // if they switch it back by hand.
        bool onAc = snapshot.OnAcPower;
        if (_lastOnAc == onAc) return;
        bool first = _lastOnAc == null;
        _lastOnAc = onAc;
        if (first) return;

        Apply(onAc ? OnCharger : OnBattery);
    }

    private void Apply(ThemeAction action)
    {
        if (action == ThemeAction.None) return;
        Hardware.SetWindowsDarkMode(action == ThemeAction.Dark);
    }

    public override FrameworkElement? CreateSettingsView()
    {
        var appPreference = Ui.Combo(
            new[] { ThemePreference.System, ThemePreference.Light, ThemePreference.Dark },
            CurrentPreference(),
            AppState.SetThemePreference,
            p => p switch
            {
                ThemePreference.System => "Follow Windows",
                ThemePreference.Light => "Always light",
                _ => "Always dark",
            });

        var children = new List<UIElement>
        {
            Ui.Caption("The switch above changes the Windows theme. This app can follow it or " +
                       "stay pinned to one appearance."),
            Ui.LabelRow("DreamTray theme", appPreference),
        };

        // A desktop never changes power source, so the rule could never fire.
        if (Hardware.HasBattery)
        {
            children.Add(Ui.Separator());
            children.Add(Ui.Caption("Switch the Windows theme when the power source changes. " +
                                    "\"None\" leaves it alone."));
            children.Add(Ui.LabelRow("On battery", ActionCombo(OnBattery, v => OnBattery = v)));
            children.Add(Ui.LabelRow("On charger", ActionCombo(OnCharger, v => OnCharger = v)));
        }

        return Ui.SettingsPanel([.. children]);
    }

    private System.Windows.Controls.ComboBox ActionCombo(ThemeAction current, Action<ThemeAction> set) =>
        Ui.Combo(
            new[] { ThemeAction.None, ThemeAction.Dark, ThemeAction.Light },
            current,
            v =>
            {
                set(v);
                _lastOnAc = null; // re-arm so the next transition fires
            },
            a => a switch
            {
                ThemeAction.Dark => "Dark",
                ThemeAction.Light => "Light",
                _ => "None",
            });

    private static ThemePreference CurrentPreference() => AppState.ThemePreference;

    public override void Dispose()
    {
        Host.Theme.Changed -= SyncFromSystem;
        base.Dispose();
    }
}
