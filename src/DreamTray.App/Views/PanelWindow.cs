using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DreamTray.App.Interop;
using DreamTray.App.Widgets;

namespace DreamTray.App.Views;

/// <summary>
/// The flyout that opens on a tray-icon click: a vertical stack of widgets the
/// user can reorder, remove and add to.
///
/// It is created once and hidden rather than closed, so reopening is instant.
/// While hidden it tells <see cref="WidgetManager"/> so every widget drops its
/// sensor subscription — that is what makes the app cost nothing when idle.
/// </summary>
internal sealed class PanelWindow : Window
{
    private const double PanelWidth = 340;
    private const double EdgeMargin = 12;
    // The floor on the panel, so a monitor too short for it still shows a header and
    // something scrollable rather than collapsing to nothing.
    private const double MinPanelHeight = 180;
    private const double CornerRadius = 16;
    // What DWM's own DWMWCP_ROUND arc measures, in dips. The translucent path leans on
    // that rounding instead of a region (see ApplyWindowEffects), and the hairline
    // border has to follow the same arc the window is actually clipped to.
    private const double DwmCornerRadius = 8;

    // Windows 11's tray flyouts do not fade. They start entirely outside the monitor,
    // travel the whole way in — past the taskbar, which draws over them — and stop at
    // their resting position. Dismissal runs the same trip backwards, faster.
    //
    // The travel here is close to a full screen height, which rules out the very
    // aggressive spline the shell uses for its short offset animations: covering most
    // of a monitor in the first few frames leaves visible gaps between them, and reads
    // as a stutter rather than as speed. A plain cubic ease-out over a longer duration
    // keeps the per-frame step small enough to stay smooth.
    private static readonly IEasingFunction OpenEase =
        new CubicEase { EasingMode = EasingMode.EaseOut };
    private static readonly IEasingFunction CloseEase =
        new QuadraticEase { EasingMode = EasingMode.EaseIn };

    private readonly AppServices _services;
    private readonly Action _openSettings;
    private readonly WidgetRegistry _registry;
    private readonly WidgetManager _manager;

    private readonly StackPanel _list = new();
    private ScrollViewer _scroller = null!;
    private readonly Button _editButton;
    private readonly Button _addButton;
    private bool _editMode;

    // Drag-reorder state.
    private Grid? _dragSurface;
    private WidgetHost? _dragHost;
    private int _dragFromIndex = -1;
    private Popup? _addPopup;

    // Where the panel was anchored when it was opened. Adding or removing a widget
    // changes the content height, and SizeToContent grows the window from its top-
    // left corner — so without re-anchoring, the panel drifts off the work area.
    private Point _anchor;
    private bool _anchored;

    // The outline that traces the window's corners; its radius has to match whichever
    // rounding is in force. Set once by BuildLayout.
    private Border? _frame;

    // Whether the corners are currently DWM's rather than ours. True while a backdrop
    // material is live — a region would clip it with hard edges.
    private bool _dwmCorners;

    // The two ends of the slide. _rest* is where the panel belongs once it has
    // settled, _offscreenTop* is the far end just past the monitor edge. Kept in
    // device pixels because that is what the per-frame move takes, and in DIPs
    // because that is what WPF's Left/Top take once the panel is at rest.
    private int _leftPx;
    private double _restTopPx;
    private double _offscreenTopPx;
    private double _restTop;
    private bool _sliding;
    private bool _closing;

    // Slide state. The move is driven off the compositor's frame callback rather
    // than a WPF animation — see StartSlide.
    private readonly System.Diagnostics.Stopwatch _slideClock = new();
    private double _slideFromPx;
    private double _slideToPx;
    private TimeSpan _slideDuration;
    private IEasingFunction _slideEase = OpenEase;
    private Action? _slideDone;
    private bool _slideRunning;
    // Last vertical position handed to the window manager, so a frame that resolves
    // to the same pixel does not pay for a recomposite. See OnSlideFrame.
    private int _lastMovedToPx = int.MinValue;

    // Pending "compose at rest, then jump off-screen and slide in" step. See BeginReveal.
    private EventHandler? _reveal;
    private int _revealFrames;
    // Whether the pending reveal ends in a slide (animation on) or a bare uncloak.
    private bool _revealSlides;
    // Backstop for a reveal whose frame callback never arrives. See ArmRevealWatchdog.
    private System.Threading.Timer? _revealWatchdog;
    private int _watchdogTicks;
    // Set when the watchdog put the window on screen without the UI thread. The panel
    // is already visible where it stands, so the reveal must not go on to throw it
    // off-screen and slide it back — that would read as the panel appearing, vanishing
    // and then arriving.
    private volatile bool _revealForced;

    // The window handle, cached the moment it exists. Everything that reads it off the
    // UI thread has to come from here: WindowInteropHelper touches the Window, which
    // has thread affinity, and the watchdog runs on the thread pool precisely because
    // it cannot assume the UI thread is alive to run on.
    private nint _hwnd;
    // Whether the window is currently hidden from the screen by DWM. Written from the
    // watchdog thread as well as the UI thread.
    private volatile bool _cloaked;

    // Open profiling. One clock runs from the tray click to the panel actually being
    // on screen — which is *after* ShowNear returns, because the reveal is asynchronous
    // — so the trace has to outlive the call that starts it.
    private readonly System.Diagnostics.Stopwatch _openClock = new();
    private System.Text.StringBuilder? _openTrace;
    private double _lastTraceMark;

    // Theme and backdrop the window chrome was last built for. Null until the first
    // pass, so that one always runs. See ApplyWindowEffects.
    private (bool Dark, bool Translucent)? _appliedAppearance;

    public PanelWindow(AppServices services, Action openSettings)
    {
        _services = services;
        _openSettings = openSettings;

        AppState.Attach(services);
        _registry = new WidgetRegistry(services);
        _manager = new WidgetManager(services, _registry);

        Title = "DreamTray";
        Width = PanelWidth;
        // Somewhere harmless until the first ShowNear works out where the panel goes;
        // the default (0,0) would flash in the corner of the primary monitor.
        Left = -32000;
        Top = -32000;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        // Deliberately not topmost. The taskbar is, so leaving this window in the
        // normal band is what puts it *under* the taskbar — the panel slides up out
        // of it instead of across it, which is how every system tray flyout behaves.
        // Nothing is lost by it: the panel is dismissed the moment it loses focus, so
        // it is always the active window while on screen.
        Topmost = false;
        // AllowsTransparency would disable the DWM backdrop and force software
        // rendering of the whole window; a solid themed background plus DWM's own
        // rounded corners gets the Windows 11 look without either cost.
        AllowsTransparency = false;
        Background = Application.Current?.TryFindResource("WindowBackground") as Brush
                     ?? Brushes.White;

        _editButton = Ui.IconButton("\uE70F", "Edit widgets", ToggleEditMode);
        _addButton = Ui.IconButton("\uE710", "Add a widget", ShowAddWidget);
        _addButton.Visibility = Visibility.Collapsed;

        Content = BuildLayout();

        _manager.LayoutChanged += RebuildList;
        _manager.Load();
        RebuildList();

        Deactivated += (_, _) =>
        {
            // A click on the tray icon deactivates the panel too, but that click is a
            // toggle and the toggle handles the close itself. Hiding here as well
            // would make the press close the panel and the toggle immediately reopen
            // it, so this one deactivation is left alone.
            if (DismissedByCaller?.Invoke() == true) return;
            HidePanel();
        };
        SizeChanged += OnSizeChanged;
        PreviewKeyDown += OnKeyDown;
        SourceInitialized += OnSourceInitialized;
        _services.Theme.Changed += OnThemeChanged;
    }

    /// <summary>The placed widgets, for <c>--selftest</c> to drive add/remove through.</summary>
    internal WidgetManager Manager => _manager;

    // Read per open and per close rather than cached: the settings window edits these
    // live, and the panel is created once and reused for the life of the app.
    private Settings.AnimationSettings Animations => _services.Settings.Current.Animations;
    private TimeSpan OpenSlideTime => TimeSpan.FromMilliseconds(Animations.ClampedOpenMs);
    private TimeSpan CloseSlideTime => TimeSpan.FromMilliseconds(Animations.ClampedCloseMs);
    private bool AnimatesOpen => Animations.Enabled && Animations.ClampedOpenMs > 0;
    private bool AnimatesClose => Animations.Enabled && Animations.ClampedCloseMs > 0;

    /// <summary>
    /// How many cards failed to build in the last rebuild. A failure is contained
    /// (the rest of the panel still appears) and logged rather than thrown, so this
    /// is what lets <c>--selftest</c> notice it happened at all.
    /// </summary>
    internal int LastRebuildFailures { get; private set; }

    // ---------------------------------------------------------------- layout

    private UIElement BuildLayout()
    {
        var title = new TextBlock
        {
            Text = "DreamTray",
            Style = Ui.Find("SubtitleText"),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var settingsButton = Ui.IconButton("\uE713", "Settings", () => _openSettings());

        var header = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(title, 0);
        Grid.SetColumn(_addButton, 1);
        Grid.SetColumn(_editButton, 2);
        Grid.SetColumn(settingsButton, 3);
        header.Children.Add(title);
        header.Children.Add(_addButton);
        header.Children.Add(_editButton);
        header.Children.Add(settingsButton);

        // No cap of its own: the height budget is put on the window (see
        // ApplyHeightBudget) and this scroller is what gives way when the window
        // runs out of room.
        _scroller = new ScrollViewer
        {
            Style = Ui.Find("ThinScrollViewer"),
            Content = _list,
        };

        var root = new Grid { Margin = new Thickness(14, 12, 14, 14) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        // Star, not Auto. Measured against an unbounded window — which is what
        // SizeToContent does — a star row asks for exactly its content, so the panel
        // still shrinks to fit its widgets. The difference is on the way down: an Auto
        // row insists on its full desired height at arrange and simply overflows the
        // window, which is the list being cut off mid-widget. A star row gives up the
        // space it does not have, and a ScrollViewer arranged shorter than its content
        // scrolls.
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(header, 0);
        Grid.SetRow(_scroller, 1);
        root.Children.Add(header);
        root.Children.Add(_scroller);

        // Drag-reorder is tracked at the window level so the pointer can leave the
        // grip (and the card) without dropping the gesture. The capture has to land
        // on this same element: captured events are routed to the capture target and
        // bubble up from there, so capturing an ancestor of the handlers (the frame
        // below, say) would silently stop the gesture from ever moving.
        _dragSurface = root;
        root.MouseMove += OnDragMove;
        root.MouseLeftButtonUp += (_, _) => EndDrag();

        // The outline is drawn here rather than as Window.BorderThickness: DWM clips
        // the window to a rounded rect, which would cut the four corners out of a
        // square window border. Matching CornerRadius to the DWM radius keeps the
        // hairline following the same arc the window is clipped to.
        _frame = new Border
        {
            CornerRadius = new CornerRadius(CornerRadius),
            BorderThickness = new Thickness(1),
            Background = Brushes.Transparent,
            Child = root,
        };
        _frame.SetResourceReference(Border.BorderBrushProperty, "WindowStroke");
        return _frame;
    }

    private void RebuildList()
    {
        // Detach before clearing: the cards are thrown away but the widgets are not,
        // and each card is holding elements the next one is about to adopt.
        foreach (var host in _list.Children.OfType<WidgetHost>()) host.Detach();
        _list.Children.Clear();
        LastRebuildFailures = 0;

        foreach (var instance in _manager.Instances)
        {
            // One card failing to build must not take the rest of the panel with it:
            // the loop is what puts every widget on screen, so an escaping exception
            // here reads to the user as "half my widgets vanished".
            try
            {
                var host = new WidgetHost(instance, BeginDrag, _manager.Remove);
                host.SetEditMode(_editMode);
                _list.Children.Add(host);
            }
            catch (Exception ex)
            {
                LastRebuildFailures++;
                Logging.Log.Write($"widget '{instance.Factory.TypeId}' card failed to build: {ex}");
                _list.Children.Add(Ui.Caption($"{instance.Factory.DisplayName} failed to load. See the log."));
            }
        }

        if (_manager.Instances.Count == 0)
        {
            _list.Children.Add(Ui.Caption("No widgets yet. Use the + button to add some."));
        }

        // Cards carry a bottom margin to separate them; on the last one it stacks
        // with the root's bottom margin and the gap under the panel reads deeper
        // than the gaps at its sides.
        if (_list.Children.Count > 0 && _list.Children[^1] is FrameworkElement last)
        {
            var m = last.Margin;
            last.Margin = new Thickness(m.Left, m.Top, m.Right, 0);
        }
    }

    // ---------------------------------------------------------------- edit mode

    private void ToggleEditMode()
    {
        _editMode = !_editMode;
        _addButton.Visibility = _editMode ? Visibility.Visible : Visibility.Collapsed;
        foreach (var host in _list.Children.OfType<WidgetHost>()) host.SetEditMode(_editMode);
    }

    private void ShowAddWidget()
    {
        var placed = _manager.Instances.Select(i => i.Factory.TypeId).ToHashSet();
        var host = _services.CreateHost(_services.Settings.Scope(new System.Text.Json.Nodes.JsonObject()));

        var candidates = _registry.Available(host)
            .Where(f => f.AllowMultiple || !placed.Contains(f.TypeId))
            .ToList();

        var panel = new StackPanel { Width = 300 };
        if (candidates.Count == 0)
        {
            panel.Children.Add(Ui.Caption("Every available widget is already on the panel."));
        }

        foreach (var factory in candidates)
        {
            var button = new Button
            {
                Style = Ui.Find("FluentButton"),
                Margin = new Thickness(0, 0, 0, 6),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(10, 8, 10, 8),
                Content = BuildPickerEntry(factory),
            };
            var typeId = factory.TypeId;
            button.Click += (_, _) =>
            {
                if (_addPopup != null) _addPopup.IsOpen = false;
                _manager.Add(typeId);
            };
            panel.Children.Add(button);
        }

        var card = new Border
        {
            Style = Ui.Find("Card"),
            Padding = new Thickness(12),
            Background = Application.Current?.TryFindResource("FlyoutBackground") as Brush,
            Child = new ScrollViewer
            {
                Style = Ui.Find("ThinScrollViewer"),
                MaxHeight = 420,
                Content = panel,
            },
        };
        // Card's own stroke is a dark one, meant for a card sitting on the panel.
        // A popup floats over the desktop like the panel does, so it takes the
        // panel's light hairline instead.
        card.SetResourceReference(Border.BorderBrushProperty, "WindowStroke");

        _addPopup = new Popup
        {
            Child = card,
            PlacementTarget = _addButton,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
            IsOpen = true,
        };
    }

    private static UIElement BuildPickerEntry(IWidgetFactory factory)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var glyph = Ui.Glyph(factory.Glyph);
        glyph.Margin = new Thickness(0, 0, 10, 0);
        glyph.VerticalAlignment = VerticalAlignment.Top;

        var text = new StackPanel();
        text.Children.Add(Ui.Body(factory.DisplayName));
        var description = Ui.Caption(factory.Description);
        description.Margin = new Thickness(0, 2, 0, 0);
        text.Children.Add(description);

        Grid.SetColumn(glyph, 0);
        Grid.SetColumn(text, 1);
        grid.Children.Add(glyph);
        grid.Children.Add(text);
        return grid;
    }

    // ---------------------------------------------------------------- drag reorder

    private void BeginDrag(WidgetHost host, MouseButtonEventArgs e)
    {
        _dragHost = host;
        _dragFromIndex = _list.Children.IndexOf(host);
        host.Opacity = 0.6;
        Mouse.Capture(_dragSurface);
        e.Handled = true;
    }

    private void OnDragMove(object sender, MouseEventArgs e)
    {
        if (_dragHost == null || e.LeftButton != MouseButtonState.Pressed) return;

        double y = e.GetPosition(_list).Y;
        int currentIndex = _list.Children.IndexOf(_dragHost);
        int targetIndex = currentIndex;

        // Find which card the pointer is over by walking the stack's heights.
        double offset = 0;
        for (int i = 0; i < _list.Children.Count; i++)
        {
            var child = (FrameworkElement)_list.Children[i];
            double height = child.ActualHeight + child.Margin.Top + child.Margin.Bottom;
            if (y < offset + height / 2) { targetIndex = i; break; }
            offset += height;
            targetIndex = i;
        }

        if (targetIndex == currentIndex) return;
        _list.Children.Remove(_dragHost);
        _list.Children.Insert(Math.Clamp(targetIndex, 0, _list.Children.Count), _dragHost);
    }

    private void EndDrag()
    {
        if (_dragHost == null) return;

        int toIndex = _list.Children.IndexOf(_dragHost);
        _dragHost.Opacity = 1;
        Mouse.Capture(null);

        var host = _dragHost;
        _dragHost = null;

        if (toIndex >= 0 && toIndex != _dragFromIndex)
        {
            // Move() re-raises LayoutChanged, which rebuilds the list from the
            // manager's order — the visual and the model converge there.
            _manager.Move(_dragFromIndex, toIndex);
        }
        _dragFromIndex = -1;
        _ = host;
    }

    // ---------------------------------------------------------------- show / hide

    /// <summary>
    /// Where the time went in the last open, kept for <c>--selftest</c>. The same
    /// string is written to the log on every open by <see cref="FinishOpenTrace"/>.
    /// </summary>
    internal string LastOpenTrace { get; private set; } = "";

    /// <summary>
    /// Start the trace for one open. <paramref name="callerTrace"/> is whatever the
    /// tray controller already spent before handing over — building the window,
    /// asking the shell for the icon rectangle — so the log line accounts for the
    /// whole trip from the click rather than starting halfway through it.
    /// </summary>
    private void BeginOpenTrace(string callerTrace)
    {
        _openTrace = new System.Text.StringBuilder(callerTrace);
        _lastTraceMark = 0;
        _openClock.Restart();
    }

    /// <summary>Record how long the phase that just finished took, in milliseconds.</summary>
    private void MarkOpen(string phase)
    {
        if (_openTrace is null) return;
        double now = _openClock.Elapsed.TotalMilliseconds;
        if (_openTrace.Length > 0) _openTrace.Append(", ");
        _openTrace.Append($"{phase} {now - _lastTraceMark:F0}");
        _lastTraceMark = now;
    }

    /// <summary>
    /// Close the trace and write it, once the panel is genuinely on screen.
    ///
    /// Deliberately unconditional rather than logged only past some threshold: the
    /// open path is user-initiated and runs a handful of times an hour, so the volume
    /// is nothing, and a report of "it was slow that one time" is only actionable if
    /// the fast opens around it were recorded too. The total is what the user
    /// experiences as the delay; the phases say which part of it to go and look at.
    /// </summary>
    private void FinishOpenTrace()
    {
        if (_openTrace is null) return;
        double total = _openClock.Elapsed.TotalMilliseconds;
        LastOpenTrace = _openTrace.ToString();
        _openTrace = null;
        _openClock.Stop();
        Logging.Log.Write($"panel open {total:F0} ms total: {LastOpenTrace}");
    }

    /// <summary>
    /// The panel was dismissed before it ever reached the screen. This gets its own
    /// line because it is the exact shape of an "it did not open at all" report: the
    /// trace says which phase the open had got to, and the total says how long the
    /// window sat there cloaked — invisible, but active, and answering the next click
    /// as though it had been open the whole time.
    /// </summary>
    private void AbortOpenTrace(string why)
    {
        if (_openTrace is null) return;
        double total = _openClock.Elapsed.TotalMilliseconds;
        string trace = _openTrace.ToString();
        _openTrace = null;
        _openClock.Stop();
        Logging.Log.Write($"panel open abandoned after {total:F0} ms ({why}): {trace}");
    }

    /// <summary>Position next to the tray icon and show.</summary>
    public void ShowNear(Rect iconRect, string callerTrace = "")
    {
        BeginOpenTrace(callerTrace);

        // Cancel a close that is still playing, otherwise its Completed handler
        // would hide the panel we are in the middle of reopening.
        StopAnimations();
        // Cloaking needs an HWND, and on the very first open there is none yet —
        // the window is constructed at startup but only gets a handle when it is
        // shown. Without this the first open of the session is the one that shows
        // its own assembly, which is exactly the case that is slowest.
        _hwnd = new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
        // Cloak before showing, and lay the panel out at its *resting* position: a
        // window only ever paints the part of itself that is on screen, so one that
        // is shown off-screen and then slid in arrives with everything below the
        // point it had reached still undefined. Cloaking lets it compose a complete
        // frame where it belongs without the user seeing it happen; BeginReveal jumps
        // it off-screen and starts the slide once that frame exists.
        //
        // With animation off there is no slide, but the cloak still earns its keep:
        // everything below — the backdrop probe, two layout passes, Activate, and the
        // widgets' own OnShown — runs on the UI thread *after* the window is on
        // screen, and WPF cannot present a frame until it is done. An uncloaked
        // Show() therefore puts an empty window up immediately and leaves it there,
        // showing bare DWM acrylic with none of the panel's tint or content, until
        // the UI thread finally goes idle. That gap is the delay before the widgets
        // appear. Cloaked, the window stays invisible until it has something
        // complete to show.
        bool animate = AnimatesOpen;
        // Both before Show: the first layout happens inside it, and a layout that runs
        // without a height budget is one WPF gets to clamp on its own terms. Neither
        // call needs the window — the anchor comes from the caller, and the budget
        // from the monitor it lands on.
        SetAnchor(iconRect);
        ApplyHeightBudget();
        Cloak(true);
        Show();
        MarkOpen("show");
        // Get the window onto the monitor it is going to open on before a single
        // thing is measured. It is shown at wherever it was last left — on the first
        // open of the session, the parking spot far off-screen — and if that monitor
        // scales differently from the destination, then laying out here means laying
        // out at the wrong scale: WPF only learns the new one from the WM_DPICHANGED
        // that arrives *during* the move, by which point the height budget and the
        // layout have both already been computed against the old figure. Positioning
        // first costs one extra SetWindowPos on a cloaked window and makes every
        // measurement below happen in the units the panel will actually be shown in.
        ApplyPosition();
        MarkOpen("park");
        // Re-check the backdrop on every open: the user can switch transparency
        // effects on or off while the panel is alive, and the window is created
        // once and reused, so SourceInitialized alone would never see the change.
        ApplyWindowEffects();
        MarkOpen("effects");
        // Now that the window is on the right monitor its scale is settled, so the
        // budget is worked out again against the figure that will actually apply.
        ApplyHeightBudget();
        // Height is content-driven, so lay out before positioning — otherwise that
        // runs on a stale (zero) height and the panel lands off the bottom.
        UpdateLayout();
        MarkOpen("layout");
        ApplyPosition();
        Activate();
        MarkOpen("activate");
        // Every widget's OnShown runs from here, and with it every sensor
        // subscription. The slow hardware reads are already off the UI thread, but
        // their *results* are not: each one comes back to rebuild its card, which
        // with SizeToContent resizes the window — and a resize lands in the middle
        // of the slide, where it fights the per-frame move and rebuilds the corner
        // region. That is the stutter. Waiting until the panel has arrived costs
        // nothing visually, because every widget already draws its cached reading
        // when its card is built.
        if (animate)
        {
            BeginReveal(slide: true);
        }
        else
        {
            // Nothing is in flight to be disturbed here, so the widgets are woken
            // while the window is still cloaked and the panel is re-laid-out and
            // re-anchored around whatever their cached readings changed. Then one
            // composed frame is waited for, exactly as the animated path does, so the
            // panel becomes visible already complete rather than as an empty
            // rectangle that fills in afterwards.
            _manager.SetPanelVisible(true);
            MarkOpen("widgets");
            UpdateLayout();
            ApplyPosition();
            BeginReveal(slide: false);
        }
        LogGeometry("open");
    }

    /// <summary>
    /// Asked on deactivation whether the click that stole focus is one the owner is
    /// about to act on itself — a press on the tray icon. True means "leave it to me".
    /// </summary>
    public Func<bool>? DismissedByCaller { get; set; }

    /// <summary>
    /// The panel is on screen but playing its exit. It is on its way out, so a
    /// toggle must treat it as closed and reopen it rather than dismiss it again.
    /// </summary>
    public bool IsClosing => _closing;

    public void HidePanel()
    {
        // _closing: the panel is still on screen playing its exit, so IsVisible is
        // true and a second dismissal (a tray click landing on top of the Deactivated
        // that started this one) would restart the animation from full opacity.
        if (!IsVisible || _closing) return;
        AbortOpenTrace(_cloaked ? "still cloaked" : "revealed but not yet traced");
        foreach (var host in _list.Children.OfType<WidgetHost>()) host.CloseSettings();
        if (_addPopup != null) _addPopup.IsOpen = false;

        if (!AnimatesClose)
        {
            StopAnimations();
            _manager.SetPanelVisible(false);
            Hide();
            return;
        }
        AnimateClose();
    }

    // ---------------------------------------------------------------- animation

    /// <summary>
    /// The Windows 11 flyout entrance: the panel starts completely outside the
    /// monitor and travels the whole way to its resting position, emerging from
    /// behind the taskbar. No fade — the shell does not fade these, and the taskbar
    /// hiding the first part of the trip is what sells it.
    /// </summary>
    // How long the reveal is allowed to wait for the frame callback before giving up
    // on it, and how long after that before the window is uncloaked without the UI
    // thread's help at all. Both are far longer than the two frames being waited for
    // (about 30 ms at 60 Hz) and far shorter than a delay a user would call a delay.
    private const int RevealSoftDeadlineMs = 250;
    private const int RevealHardDeadlineMs = 500;

    /// <summary>
    /// Wait for the cloaked panel to compose one complete frame at its resting
    /// position, then — if <paramref name="slide"/> — throw it off-screen, uncloak,
    /// and slide it back in; otherwise just uncloak it where it stands.
    ///
    /// Two ticks, not one: CompositionTarget.Rendering fires *before* the frame it
    /// belongs to is drawn, so the first one is still ahead of the paint being waited
    /// on. The second means a full frame with the panel at rest has been composed.
    /// That costs about 30 ms between the click and the panel moving, which is well
    /// under what reads as a delay.
    ///
    /// The watchdog is not an optimisation, it is the correctness of this whole
    /// mechanism. Waiting on a frame callback makes the panel's visibility depend on
    /// the render loop continuing to tick, and that is not something this code gets to
    /// assume: the window is cloaked, so DWM has no reason to present it; the UI
    /// thread is at that moment running the widgets' wake-up; and on a loaded machine
    /// the render-priority dispatcher work behind CompositionTarget.Rendering is
    /// exactly what gets starved first. Miss the callback and the window sits there
    /// cloaked — invisible, but active, visible-to-WPF, and dismissed by the next
    /// click as though it had been open all along. That is the "opened four seconds
    /// late", and the "did not open at all", and they are the same bug: an unbounded
    /// wait on something nobody guarantees will happen.
    /// </summary>
    private void BeginReveal(bool slide)
    {
        CancelReveal();
        _revealSlides = slide;
        _revealForced = false;
        _revealFrames = 0;
        _reveal = (_, _) =>
        {
            if (++_revealFrames < 2) return;
            CompleteReveal("frames");
        };
        CompositionTarget.Rendering += _reveal;
        ArmRevealWatchdog();
    }

    /// <summary>
    /// Put the panel on screen. Runs on the UI thread, from whichever of the frame
    /// callback and the watchdog gets there first; the second one finds the reveal
    /// already cancelled and does nothing.
    /// </summary>
    private void CompleteReveal(string cause)
    {
        if (_reveal is null) return;
        CancelReveal();

        // Last chance to catch a region that does not match the window it is on:
        // after this the panel is on screen, and a short one takes the bottom of
        // the list with it.
        UpdateCornerRadius();
        if (_revealSlides && !_revealForced)
        {
            WindowEffects.MoveTo(this, _leftPx, (int)Math.Round(_offscreenTopPx));
            Cloak(false);
            AnimateOpen();
        }
        else
        {
            Cloak(false);
        }

        MarkOpen($"reveal({cause},{_revealFrames}f)");
        FinishOpenTrace();
    }

    /// <summary>
    /// Arm the two deadlines that guarantee the panel becomes visible.
    ///
    /// The first fires on the UI thread and simply finishes the reveal without its
    /// frame — the panel may be a few milliseconds short of a complete composition,
    /// which nobody will see, and it is on screen.
    ///
    /// The second does not go near the UI thread, because by then the UI thread is
    /// the suspect: if the dispatcher were running, the first deadline would have
    /// been served. Uncloaking is a DWM attribute write against a cached HWND, which
    /// needs neither the dispatcher nor thread affinity, so the window can be put on
    /// screen from the pool thread regardless of what the app is doing. A panel that
    /// is a frame behind beats a panel that never appears.
    /// </summary>
    private void ArmRevealWatchdog()
    {
        DisarmRevealWatchdog();
        _watchdogTicks = 0;
        _revealWatchdog = new System.Threading.Timer(
            OnRevealWatchdog, null, RevealSoftDeadlineMs, RevealHardDeadlineMs);
    }

    private void OnRevealWatchdog(object? state)
    {
        if (Interlocked.Increment(ref _watchdogTicks) == 1)
        {
            Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Send,
                new Action(() => CompleteReveal("watchdog")));
            return;
        }

        // Still hidden one whole deadline after asking the UI thread to finish. It is
        // not coming; uncloak from here and say so, because this line is the evidence
        // that the stall was the dispatcher rather than anything the panel did.
        DisarmRevealWatchdog();
        if (!_cloaked) return;
        _revealForced = true;
        _cloaked = false;
        WindowEffects.SetCloaked(_hwnd, false);
        Logging.Log.Write(
            $"panel reveal: no composed frame and no dispatcher within " +
            $"{RevealSoftDeadlineMs + RevealHardDeadlineMs} ms — uncloaked off-thread");
    }

    private void DisarmRevealWatchdog()
    {
        var timer = Interlocked.Exchange(ref _revealWatchdog, null);
        timer?.Dispose();
    }

    private void CancelReveal()
    {
        DisarmRevealWatchdog();
        if (_reveal != null) CompositionTarget.Rendering -= _reveal;
        _reveal = null;
    }

    /// <summary>
    /// Hide or reveal the window through DWM, keeping <see cref="_cloaked"/> in step.
    /// Everything goes through here so the watchdog can tell whether the panel is
    /// still hidden without racing the UI thread over it.
    /// </summary>
    private void Cloak(bool cloaked)
    {
        if (_hwnd == nint.Zero) _hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        _cloaked = cloaked;
        WindowEffects.SetCloaked(_hwnd, cloaked);
    }

    private void AnimateOpen()
    {
        _sliding = true;
        StartSlide(_offscreenTopPx, _restTopPx, OpenSlideTime, OpenEase, () =>
        {
            _sliding = false;
            // Not _restTopPx as captured at the start: the panel may have been
            // re-anchored (a widget added) while the slide was running.
            SettleAt();
            // Deferred from ShowNear so the sensor traffic it starts cannot resize
            // the window mid-flight.
            _manager.SetPanelVisible(true);
            LogGeometry("widgets");
        });
    }

    /// <summary>
    /// The exit: the same trip in reverse, back out of the monitor. Windows makes
    /// this noticeably faster than the entrance — dismissal should feel immediate.
    /// </summary>
    private void AnimateClose()
    {
        // A dismissal landing inside the reveal window finds the panel still cloaked
        // at its resting position, which is exactly where the exit starts from.
        CancelReveal();
        Cloak(false);
        _closing = true;
        StartSlide(_restTopPx, _offscreenTopPx, CloseSlideTime, CloseEase, () =>
        {
            _closing = false;
            _manager.SetPanelVisible(false);
            Hide();
            SettleAt();
        });
    }

    /// <summary>
    /// Move the window from one position to another over time, one step per rendered
    /// frame.
    ///
    /// A WPF animation on the Top property would be the obvious way to do this, and
    /// it is the wrong one: the property system converts through DIPs and re-enters
    /// the window's own position handling on every tick, and the clock is not tied to
    /// the frames that actually get presented. Over a travel this long that produces
    /// uneven steps and half-drawn frames. CompositionTarget.Rendering fires exactly
    /// once per composed frame, so each move lands on a frame that is about to be
    /// shown, and the position comes from the wall clock rather than a frame count —
    /// a dropped frame costs smoothness, never duration.
    /// </summary>
    private void StartSlide(
        double fromPx, double toPx, TimeSpan duration, IEasingFunction ease, Action? done)
    {
        StopSlide();
        _slideFromPx = fromPx;
        _slideToPx = toPx;
        _slideDuration = duration;
        _slideEase = ease;
        _slideDone = done;
        _slideRunning = true;

        // SizeToContent makes WPF re-measure the window against its content whenever
        // the window's own rectangle changes — and the slide changes it on every
        // frame. That is a layout pass per frame over the whole widget tree, on the
        // UI thread, competing with the move it is triggered by. The height is
        // already settled by the time a slide starts, so it is pinned for the trip
        // and handed back at the end.
        SizeToContent = SizeToContent.Manual;

        _lastMovedToPx = (int)Math.Round(fromPx);
        WindowEffects.MoveTo(this, _leftPx, _lastMovedToPx);
        _slideClock.Restart();
        CompositionTarget.Rendering += OnSlideFrame;
    }

    private void OnSlideFrame(object? sender, EventArgs e)
    {
        double total = _slideDuration.TotalMilliseconds;
        double t = total <= 0 ? 1 : _slideClock.Elapsed.TotalMilliseconds / total;
        bool finished = t >= 1;
        if (finished) t = 1;

        double y = _slideFromPx + (_slideToPx - _slideFromPx) * _slideEase.Ease(t);
        int yPx = (int)Math.Round(y);
        // A move to where the window already is still makes DWM recompose it, and
        // with an acrylic backdrop that means re-blurring everything behind it. On a
        // high-refresh monitor the ease-out's final frames land on the same pixel
        // several times over, so skipping those is free smoothness.
        if (yPx != _lastMovedToPx)
        {
            _lastMovedToPx = yPx;
            WindowEffects.MoveTo(this, _leftPx, yPx);
        }

        if (!finished) return;
        // Read the callback before stopping: StopSlide clears it, and it is what
        // hides the window at the end of a close.
        var done = _slideDone;
        StopSlide();
        done?.Invoke();
    }

    /// <summary>
    /// Drop a slide in flight without running its completion. That is what keeps a
    /// cancelled close from hiding a panel that has since been reopened.
    /// </summary>
    private void StopSlide()
    {
        if (_slideRunning)
        {
            CompositionTarget.Rendering -= OnSlideFrame;
            SizeToContent = SizeToContent.Height;
        }
        _slideRunning = false;
        _slideClock.Reset();
        _slideDone = null;
    }

    private void StopAnimations()
    {
        CancelReveal();
        StopSlide();
        _sliding = false;
        _closing = false;
        // Never leave the window cloaked: it would be invisible but still active, and
        // the next dismissal would hide an already-invisible panel.
        Cloak(false);
    }

    private void SetAnchor(Rect iconRect)
    {
        // Anchor to the icon when the shell told us where it is; otherwise fall back
        // to the cursor, which is where the click happened anyway. Remember it: the
        // cursor moves, but the panel must keep re-anchoring to the same spot when
        // its height changes.
        _anchor = iconRect.IsEmpty
            ? WindowEffects.GetCursorPosition()
            : new Point(iconRect.Left + iconRect.Width / 2, iconRect.Top);
        _anchored = true;
    }

    /// <summary>
    /// Give the window itself the work area as its height budget, so a long list
    /// scrolls instead of growing past the screen edge.
    ///
    /// The cap belongs on the window rather than on the widget list because WPF puts
    /// one there regardless: SizeToContent will not size a window past the monitor,
    /// and when it hits that limit it clamps the HWND while the content inside stays
    /// laid out for the height it asked for. The panel is then a window shorter than
    /// its own contents — rounded corners and all, with the last widget running off
    /// the bottom edge — which is why capping only the list never fixed it. Setting a
    /// budget that is always tighter than WPF's own keeps that clamp out of it, and
    /// the star row below the header is what turns the shortfall into scrolling.
    ///
    /// The scale comes from the monitor the panel is anchored to, not from the
    /// window: this runs before the panel has been positioned, and until then WPF's
    /// transform still describes wherever the window happened to be created.
    /// </summary>
    private void ApplyHeightBudget()
    {
        double scale = _anchored ? WindowEffects.GetDpiScale(_anchor) : 0;
        if (scale <= 0) scale = WindowEffects.GetDpiScale(this);
        if (scale <= 0) scale = 1;

        double available = WindowEffects.GetWorkArea(_anchor).Height / scale - 2 * EdgeMargin;
        MaxHeight = Math.Max(MinPanelHeight, available);
    }

    /// <summary>
    /// Keep the panel pinned to its anchor as widgets are added or removed. The
    /// window grows downward from Top, so a taller panel would otherwise run past
    /// the bottom of the work area (or float away from the taskbar when it shrinks).
    /// </summary>
    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_anchored && IsVisible) ApplyPosition();
        if (!IsVisible) return;
        // The HWND is resized after this pass, so the region cannot be checked here —
        // but once the dispatcher comes back round it has happened, whatever order the
        // messages arrived in. Cheap, and it is the backstop for the window growing
        // under a region that stayed behind.
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Loaded,
            new Action(() => { UpdateCornerRadius(); LogGeometry("resize"); }));
    }

    /// <summary>
    /// One line of everything that decides how tall the panel gets, so a report of it
    /// being cut off can be read rather than guessed at. The two heights that matter
    /// are WPF's (<c>win</c>) and the window manager's (<c>hwnd</c>): the list is
    /// clipped exactly when the second is the smaller of the two, and whichever of
    /// the caps below equals it is the one doing the clipping.
    /// </summary>
    private void LogGeometry(string phase)
    {
        WindowEffects.TryGetSize(this, out int wPx, out int hPx);
        Rect work = WindowEffects.GetWorkArea(_anchor);
        Rect region = WindowEffects.GetRegionBox(this);
        double wpfScale = WindowEffects.GetDpiScale(this);
        double osScale = WindowEffects.GetDpiScaleForWindow(_hwnd);
        Logging.Log.Write(
            $"panel {phase}: win {ActualWidth:F0}x{ActualHeight:F0} dip, hwnd {wPx}x{hPx} px, " +
            $"region {(region.IsEmpty ? "none" : $"{region.Width:F0}x{region.Height:F0}")}, " +
            $"max {MaxHeight:F0} dip, scale wpf {wpfScale:F2} os {osScale:F2} " +
            $"mon {WindowEffects.GetDpiScale(_anchor):F2}, work {work.Width:F0}x{work.Height:F0} px, " +
            $"list viewport {_scroller.ViewportHeight:F0} extent {_scroller.ExtentHeight:F0}, " +
            $"sizeToContent {SizeToContent}, top {Top:F0}");
    }

    /// <summary>
    /// Whether WPF's cached scale for this window has fallen out of step with the
    /// monitor it is actually on. When it has, every dimension the panel derives is
    /// off by the ratio between the two — most visibly the width, which is a fixed
    /// <see cref="PanelWidth"/> in WPF's units and therefore becomes
    /// <c>340 x wpfScale</c> physical pixels on a monitor that wanted
    /// <c>340 x osScale</c> of them. A panel that comes back conspicuously narrow
    /// after a few resolution changes is this, and nothing else: nothing in the
    /// layout can make the window narrower, because nothing constrains its width.
    ///
    /// WPF refreshes that cache from WM_DPICHANGED alone, and Windows does not send
    /// WM_DPICHANGED to a window that is hidden — which this one is, all but a few
    /// seconds of its life, because it is created once at startup and hidden rather
    /// than closed. So a display change that lands while the panel is closed is
    /// invisible to it, permanently. There is no supported way to correct the cache
    /// in place; the window has to be built again. See TrayController.ShowPanel.
    /// </summary>
    /// <summary>
    /// Whether the window has been realised yet. A panel that has only ever been
    /// prewarmed holds no cached scale — it has not been near a monitor — so there is
    /// nothing for a display change to invalidate.
    /// </summary>
    internal bool HasWindowHandle => _hwnd != nint.Zero;

    internal bool IsDpiStale()
    {
        if (_hwnd == nint.Zero) return false;
        double wpf = WindowEffects.GetDpiScale(this);
        double os = WindowEffects.GetDpiScaleForWindow(_hwnd);
        // A zero from either side means the answer is unavailable, not that they
        // disagree — an old build with no GetDpiForWindow must not rebuild every open.
        if (wpf <= 0 || os <= 0) return false;
        return Math.Abs(wpf - os) > 0.001;
    }

    /// <summary>
    /// Rebuild the rounded-corner clip from the size the window actually has.
    ///
    /// Driven off WM_WINDOWPOSCHANGED rather than SizeChanged: SizeChanged fires from
    /// the layout pass, but with SizeToContent the HWND is resized after it, so a
    /// region built there is cut to the *previous* height. Everything below that line
    /// is clipped off the window — and since a shrinking region never invalidates
    /// what it stops covering, the clipped widgets are left behind on the desktop as
    /// a ghost of themselves.
    ///
    /// The check is against the region the window manager actually has, not against
    /// a size remembered here. Remembered state was the bug: every path that resized
    /// the window without the message arriving — or that arrived while the cache said
    /// "already done" — left a region shorter than the window, and a short region
    /// clips the bottom off the panel while every number WPF reports stays correct.
    /// Asking GDI costs one call and cannot drift.
    /// </summary>
    private void UpdateCornerRadius(int width = 0, int height = 0)
    {
        // With a backdrop live the corners belong to DWM, which rounds the window
        // itself at any size — there is no region to keep in step.
        if (_dwmCorners) return;
        // A size from the caller is one the window may not admit to yet; without one,
        // ask.
        if ((width <= 0 || height <= 0)
            && !WindowEffects.TryGetSize(this, out width, out height)) return;

        // SetCornerRadius builds the region exclusive of the right and bottom edge,
        // so a region that fits the window measures one larger in each direction.
        // Anything else — shorter, taller, or absent — is rebuilt.
        Rect box = WindowEffects.GetRegionBox(this);
        if (box.Width == width + 1 && box.Height == height + 1) return;
        WindowEffects.SetCornerRadius(this, CornerRadius, width, height);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (PresentationSource.FromVisual(this) is System.Windows.Interop.HwndSource source)
            source.AddHook(OnWindowMessage);
        ApplyWindowEffects();
    }

    private nint OnWindowMessage(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        const int WM_WINDOWPOSCHANGED = 0x0047;
        if (msg == WM_WINDOWPOSCHANGED)
        {
            // The size in the message beats the one the window will admit to: adding
            // a widget resizes the HWND, and asking here still answers with the rect
            // from before it. That is what left the panel taller than the region
            // clipping it until the next open rebuilt one.
            if (WindowEffects.TryReadWindowPos(lParam, out int width, out int height))
                UpdateCornerRadius(width, height);
            else
                UpdateCornerRadius();
        }
        return nint.Zero;
    }

    private void ApplyPosition()
    {
        Point anchor = _anchor;
        Rect workArea = WindowEffects.GetWorkArea(anchor);
        double scale = WindowEffects.GetDpiScale(this);
        if (scale <= 0) scale = 1;

        // Work in device pixels, then convert once: WPF's Left/Top are in DIPs but
        // the monitor rectangle is not.
        double widthPx = ActualWidth * scale;
        double heightPx = ActualHeight * scale;

        double margin = EdgeMargin * scale;

        // Clamp with Min/Max rather than Clamp: on a small monitor the panel can be
        // wider or taller than the work area minus margins, which would make the
        // lower bound exceed the upper one and throw. Then the near edge wins.
        double left = anchor.X - widthPx / 2;
        left = Math.Max(workArea.Left + margin,
                        Math.Min(left, workArea.Right - widthPx - margin));

        // Above the taskbar when it is at the bottom, below it when it is at the top.
        bool taskbarAtTop = anchor.Y < workArea.Top + workArea.Height / 2;
        double top = taskbarAtTop
            ? workArea.Top + margin
            : workArea.Bottom - heightPx - margin;
        top = Math.Max(workArea.Top + margin,
                       Math.Min(top, workArea.Bottom - heightPx - margin));

        // The far end of the trip: outside the monitor, on the side the panel is
        // docked against. Measured off the full monitor rather than the work area so
        // the panel starts beyond the taskbar and slides out from under it, rather
        // than starting on top of it.
        //
        // One pixel is deliberately left overlapping the screen, so the window never
        // has zero intersection with the desktop while it is in flight.
        Rect monitor = WindowEffects.GetMonitorArea(anchor);
        double offscreen = taskbarAtTop ? monitor.Top - heightPx + 1 : monitor.Bottom - 1;

        _leftPx = (int)Math.Round(left);
        _restTopPx = top;
        _offscreenTopPx = offscreen;
        _restTop = top / scale;

        // While a slide is running it owns the window position, and it moves the HWND
        // directly. Assigning Left here would make WPF push its own stale cached Top
        // along with it and yank the panel mid-flight, so a settled panel — and only a
        // settled panel — is repositioned through the properties.
        if (!_sliding && !_closing) SettleAt();
    }

    /// <summary>
    /// Hand the window position back to WPF after a slide has moved the HWND behind
    /// its back, so Left/Top agree with where the window actually is.
    /// </summary>
    private void SettleAt()
    {
        double scale = WindowEffects.GetDpiScale(this);
        if (scale <= 0) scale = 1;
        Left = _leftPx / scale;
        Top = _restTop;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) HidePanel();
    }

    // ---------------------------------------------------------------- appearance

    private void ApplyWindowEffects()
    {
        bool dark = _services.Theme.IsDark;

        // Acrylic is the material Windows uses for its own tray flyouts. If DWM
        // refuses it — an older build, or transparency effects switched off — the
        // theme falls back to opaque surfaces and the window paints them itself.
        // Asking is a registry read and one DWM attribute write, and the answer is
        // what everything below depends on, so it happens on every open.
        bool translucent = WindowEffects.TryApplyBackdrop(this, WindowEffects.Backdrop.Acrylic);

        // The rest only matters when the appearance actually moved. Re-theming and
        // rebuilding the corner region on every open cost a full invalidate-and-
        // relayout of the panel for a result identical to the frame before it.
        if (_appliedAppearance == (dark, translucent)) return;
        _appliedAppearance = (dark, translucent);

        WindowEffects.SetDarkMode(this, dark);

        // How the corners get rounded depends on the backdrop, because the two
        // mechanisms fail in opposite conditions.
        //
        // A window region is a hard stencil: it clips in device pixels with no
        // anti-aliasing, and it cuts through the acrylic the compositor draws behind
        // the window. On a machine with transparency effects on that reads as chipped,
        // half-blurred corners on an otherwise rounded panel. DWM's own rounding is
        // applied by the compositor together with the material, so it stays clean —
        // it just cannot be given a radius, which is why it is not used everywhere.
        //
        // With no backdrop there is nothing for a region to spoil, and the larger
        // 16-dip radius is the look this panel wants, so the region stays.
        _dwmCorners = translucent;
        if (translucent) WindowEffects.ClearCornerRegion(this);
        // The backdrop change above can restore DWM's rounding, so the region is put
        // back on the spot rather than waiting for the next resize.
        else UpdateCornerRadius();

        if (_frame is not null)
            _frame.CornerRadius = new CornerRadius(translucent ? DwmCornerRadius : CornerRadius);

        (Application.Current as App)?.ApplyTheme(translucent);

        if (translucent) WindowEffects.ExtendFrameIntoClientArea(this);
        // Not Transparent even with a backdrop live: the panel's own tint is what
        // turns raw blurred wallpaper into a Windows flyout surface. See
        // PanelBackground in ThemeManager.
        Background = Application.Current?.TryFindResource("PanelBackground") as Brush
                     ?? Brushes.White;
        SetCompositionBackground(translucent);
    }

    /// <summary>
    /// Let the DWM material actually reach the screen.
    ///
    /// A transparent <see cref="Window.Background"/> is not enough on its own, and
    /// on its own is worse than nothing: WPF composes the window onto its own
    /// render surface, and that surface has an opaque background colour of its own —
    /// black by default. Painting nothing over black leaves black, which is exactly
    /// the flat panel the acrylic was supposed to be showing through. The backdrop
    /// was live the whole time, sitting behind a surface that never let it out.
    ///
    /// Clearing the composition background is what punches the hole through to it,
    /// and it is the piece that pairs with DwmExtendFrameIntoClientArea — the frame
    /// says "glass reaches this far", this says "and nothing of mine covers it".
    /// </summary>
    private void SetCompositionBackground(bool translucent)
    {
        if (PresentationSource.FromVisual(this) is not System.Windows.Interop.HwndSource source)
            return;
        if (source.CompositionTarget is not { } target) return;
        // Opaque again when there is no material: an unpainted pixel would show the
        // desktop straight through rather than the window's own colour.
        target.BackgroundColor = translucent ? Colors.Transparent : Colors.Black;
    }

    private void OnThemeChanged()
    {
        WindowEffects.SetDarkMode(this, _services.Theme.IsDark);
        // The tokens are frozen brushes, so a theme switch replaces them rather than
        // recolouring them — the reference held here is the old palette's and has to
        // be re-read. (Background is a plain property, not a resource reference: it
        // is chosen by backdrop as well as by theme.)
        Background = Application.Current?.TryFindResource("PanelBackground") as Brush
                     ?? Background;
    }

    protected override void OnClosed(EventArgs e)
    {
        // CompositionTarget.Rendering is static, so a slide or a pending reveal left
        // running here would keep the closed window alive and moving. CancelReveal
        // also disposes the watchdog timer, which would otherwise fire against a
        // handle that no longer exists.
        CancelReveal();
        StopSlide();
        _hwnd = nint.Zero;
        _services.Theme.Changed -= OnThemeChanged;
        _manager.LayoutChanged -= RebuildList;
        _manager.Dispose();
        base.OnClosed(e);
    }
}


