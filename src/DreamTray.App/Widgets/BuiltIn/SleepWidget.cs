using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace DreamTray.App.Widgets.BuiltIn;

internal sealed class SleepWidgetFactory : IWidgetFactory
{
    public const string Id = "core.sleep";
    public string TypeId => Id;
    public string DisplayName => "Sleep";
    public string Description => "Standby timeout and what closing the lid does, for the current power source.";
    public string Glyph => "\uE708"; // QuietHours crescent
    public bool IsAvailable(IPluginHost host) => host.Hardware.PowerPolicy != null;
    public IWidget Create(IWidgetContext context) => new SleepWidget(context);
}

/// <summary>
/// The two sleep settings worth reaching for without opening Settings: how long the
/// machine idles before standby, and whether closing the lid puts it to sleep.
///
/// Windows keeps a separate value for mains and battery, and so do we — but the
/// widget only ever shows and edits the half that is in force right now. Showing
/// both would double the controls to make the inactive half editable, which is the
/// job of the Windows page, not of a tray flyout. The header caption says which
/// half you are looking at, and the widget re-reads itself when the charger comes
/// or goes.
/// </summary>
internal sealed class SleepWidget(IWidgetContext context) : WidgetBase(context)
{
    /// <summary>The list Windows itself offers, in seconds. 0 is "Never".</summary>
    private static readonly int[] TimeoutChoices =
    [
        60, 120, 180, 300, 600, 900, 1200, 1500, 1800, 2700,
        3600, 7200, 10800, 14400, 18000, 0,
    ];

    /// <summary>
    /// Everything the widget draws, as one reading of the active power scheme.
    /// Held so the panel can render without going back to the power manager: each
    /// value costs a <c>PowerGetActiveScheme</c> plus a <c>PowerRead*ValueIndex</c>,
    /// and there are four of them, which is not work to be doing between a tray
    /// click and the flyout appearing.
    /// </summary>
    private sealed record PolicyState(
        bool HasBattery, bool OnAc, bool HasLid, int? Timeout, LidAction? Lid);

    private StackPanel? _root;
    private TextBlock? _source;
    private PolicyState? _state;
    private bool _onAc;

    public override string Title => "Sleep";

    /// <summary>
    /// Which half of the power plan is on screen, on the title row — it qualifies
    /// every control below it, and a line of its own in the body would cost height
    /// for two words.
    /// </summary>
    public override FrameworkElement? HeaderAccessory
    {
        get
        {
            if (_state?.HasBattery != true) return null;
            _source ??= Ui.Caption("");
            _source.TextWrapping = TextWrapping.NoWrap;
            return _source;
        }
    }

    private IPowerPolicy? Policy => Hardware.PowerPolicy;

    protected override FrameworkElement BuildView()
    {
        _root = new StackPanel();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        // The first read is synchronous, and it is the one that can afford to be: the
        // panel is built once at idle, well before the user clicks the tray icon.
        _state = ReadState();
        Rebuild();
        return _root;
    }

    protected override void OnShown()
    {
        // Draw the last reading straight away and re-read behind the panel — the plan
        // can have been edited in Windows Settings, or the charger pulled, since the
        // panel was last on screen.
        Rebuild();
        RefreshState();
    }

    /// <summary>Read the scheme on a pool thread, then rebuild on the UI thread.</summary>
    private void RefreshState()
    {
        var root = _root;
        if (root == null) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            var state = ReadState();
            root.Dispatcher.BeginInvoke(() =>
            {
                // The widget may have been removed while the read was out.
                if (_root != root) return;
                _state = state;
                Rebuild();
            });
        });
    }

    /// <summary>Take one consistent reading of the plan. Not for the UI thread.</summary>
    private PolicyState? ReadState()
    {
        var policy = Policy;
        if (policy == null) return null;
        bool onAc = policy.IsOnAcPower;
        return new PolicyState(policy.HasBattery, onAc, policy.HasLid,
                               policy.GetSleepTimeout(onAc),
                               policy.GetLidCloseAction(onAc));
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        // Fires on a pool thread, and only StatusChange concerns us: it is what
        // Windows raises when the power source flips.
        if (e.Mode != PowerModes.StatusChange || _root == null) return;
        RefreshState();
    }

    private void Rebuild()
    {
        if (_root == null) return;
        _root.Children.Clear();

        var state = _state;
        if (state == null)
        {
            _root.Children.Add(Ui.Caption("The active power plan could not be read."));
            return;
        }

        _onAc = state.OnAc;

        if (state.HasBattery)
        {
            _source ??= Ui.Caption("");
            _source.Text = _onAc ? "Plugged in" : "On battery";
        }

        int? timeout = state.Timeout;
        if (timeout == null)
        {
            _root.Children.Add(Ui.Caption("The standby timeout is not available on this plan."));
        }
        else
        {
            // A value set elsewhere may not be one of the presets; keep it in the list
            // so the combo shows the truth instead of silently snapping to a neighbour.
            var choices = TimeoutChoices.ToList();
            if (!choices.Contains(timeout.Value)) choices.Insert(0, timeout.Value);

            _root.Children.Add(Ui.LabelRow("Sleep after", Ui.Combo(choices, timeout.Value, seconds =>
            {
                if (seconds == timeout.Value) return;
                if (Policy?.SetSleepTimeout(_onAc, seconds) == false)
                    Host.Notify("DreamTray", "Windows refused the standby timeout change.");
                RefreshState();
            }, TimeoutLabel)));
        }

        if (!state.HasLid) return;

        var action = state.Lid;
        if (action == null)
        {
            _root.Children.Add(Ui.Caption("The lid-close action is not available on this plan."));
            return;
        }

        // Sleep versus do-nothing is the switch people actually flip; hibernate and
        // shut down stay reachable through the settings flyout so the common case is
        // one click and the rare case is not lost.
        if (action is LidAction.Sleep or LidAction.DoNothing)
        {
            _root.Children.Add(Ui.LabelRow("Sleep on lid close",
                Ui.Switch(action == LidAction.Sleep,
                          on => ApplyLid(on ? LidAction.Sleep : LidAction.DoNothing)), 8));
        }
        else
        {
            // Hibernate or shut down: a two-state switch would misrepresent it, so
            // show the full picker inline instead.
            _root.Children.Add(Ui.LabelRow("On lid close", LidCombo(action.Value), 8));
        }
    }

    private ComboBox LidCombo(LidAction current) =>
        Ui.Combo(new[] { LidAction.DoNothing, LidAction.Sleep, LidAction.Hibernate, LidAction.ShutDown },
                 current,
                 a => { if (a != current) ApplyLid(a); },
                 LidLabel);

    private void ApplyLid(LidAction action)
    {
        var policy = Policy;
        if (policy != null && !policy.SetLidCloseAction(_onAc, action))
            Host.Notify("DreamTray", "Windows refused the lid-close change.");
        RefreshState();
    }

    private static string TimeoutLabel(int seconds) => seconds switch
    {
        0 => "Never",
        < 3600 => $"{seconds / 60} min",
        3600 => "1 hour",
        _ => $"{seconds / 3600} hours",
    };

    private static string LidLabel(LidAction action) => action switch
    {
        LidAction.DoNothing => "Do nothing",
        LidAction.Sleep => "Sleep",
        LidAction.Hibernate => "Hibernate",
        _ => "Shut down",
    };

    public override FrameworkElement? CreateSettingsView()
    {
        var state = _state;
        if (state == null) return null;

        var children = new List<UIElement>
        {
            Ui.Caption(state.HasBattery
                ? "Windows power-plan settings for the power source in use right now."
                : "Windows power-plan settings for the active plan."),
        };

        if (state.HasLid && state.Lid is { } action)
        {
            children.Add(Ui.Separator());
            children.Add(Ui.LabelRow("On lid close", LidCombo(action)));
        }

        return Ui.SettingsPanel([.. children]);
    }

    public override void Dispose()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        base.Dispose();
    }
}
