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
/// It is created once, put on the desktop cloaked, and from then on only ever
/// cloaked and uncloaked — never hidden, never closed — so an open has nothing left
/// to do but let DWM show what is already composed. See <see cref="Realize(Rect)"/>.
/// While closed it tells <see cref="WidgetManager"/> so every widget drops its
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

    // The window handle, cached the moment it exists.
    private nint _hwnd;
    // Whether the window is currently hidden from the screen by DWM.
    private bool _cloaked;

    // Whether the window has been shown — cloaked — and is live on the desktop. Once
    // set it stays set: the panel is never hidden again. See Realize.
    private bool _realized;
    // Whether the user can see the panel (or it is sliding in). False while closed,
    // and from the first moment of a close, exit slide included.
    private bool _open;
    // When the current open put the panel up, and whether it has already had to
    // take the foreground back once. See OnDeactivated.
    private long _openedAtTicks;
    private bool _reactivatedThisOpen;
    // Re-entrancy guard for ShowNear. The common path never pumps, but realizing the
    // window waits for a frame, and a tray click dispatched from inside that wait
    // would otherwise start a second open on top of the first.
    private bool _inShowNear;

    /// <summary>
    /// How long after an open a lost foreground is put down to the shell finishing
    /// its click rather than to the user. See OnDeactivated.
    /// </summary>
    private const int ActivationGraceMs = 250;

    // Open profiling: from the tray click to the panel being uncloaked and active.
    private readonly System.Diagnostics.Stopwatch _openClock = new();
    private System.Text.StringBuilder? _openTrace;
    private double _lastTraceMark;

    // Theme and backdrop the window chrome was last built for. Null until the first
    // pass, so that one always runs. See ApplyWindowEffects.
    private (bool Dark, bool Translucent)? _appliedAppearance;
    // The transparency-effects switch as of that pass, so a change to it can be
    // noticed with a registry read instead of a full re-application.
    private bool _transparencyAtApply;

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
        // Realize shows the window long before anyone asks for it; it must not take
        // the foreground from whatever the user is doing at the time.
        ShowActivated = false;
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

        Deactivated += OnDeactivated;
        SizeChanged += OnSizeChanged;
        PreviewKeyDown += OnKeyDown;
        SourceInitialized += OnSourceInitialized;
        _services.Theme.Changed += OnThemeChanged;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
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
    /// tray controller already spent before handing over — how long the click sat in
    /// the queue, building the window, asking the shell for the icon rectangle — so
    /// the log line accounts for the whole trip from the click rather than starting
    /// halfway through it.
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
        _openTrace.Append($"{phase} {now - _lastTraceMark:F1}");
        _lastTraceMark = now;
    }

    /// <summary>
    /// Close the trace and write it. Unconditional rather than logged only past some
    /// threshold: the open path is user-initiated and runs a handful of times an
    /// hour, so the volume is nothing, and a report of "it was slow that one time" is
    /// only actionable if the fast opens around it were recorded too.
    /// </summary>
    private void FinishOpenTrace()
    {
        if (_openTrace is null) return;
        double total = _openClock.Elapsed.TotalMilliseconds;
        LastOpenTrace = _openTrace.ToString();
        _openTrace = null;
        _openClock.Stop();
        Logging.Log.Write($"panel open {total:F1} ms total: {LastOpenTrace}");
    }

    /// <summary>Whether the panel is on screen for the user — or sliding onto it.</summary>
    public bool IsOpen => _open;

    /// <summary>
    /// The panel is on screen but playing its exit. It is on its way out, so a
    /// toggle must treat it as closed and reopen it rather than dismiss it again.
    /// </summary>
    public bool IsClosing => _closing;

    /// <summary>Whether DWM is keeping the window off the screen. For <c>--selftest</c>.</summary>
    internal bool IsCloaked => _cloaked;

    /// <summary>
    /// Asked on deactivation whether the click that stole focus is one the owner is
    /// about to act on itself — a press on the tray icon. True means "leave it to me".
    /// </summary>
    public Func<bool>? DismissedByCaller { get; set; }

    /// <summary>
    /// Put the panel on the desktop — shown, laid out and rendered at the spot it
    /// opens in — but cloaked, and leave it there for the life of the window.
    ///
    /// This is the whole of the open's cost, paid while nobody is waiting. The panel
    /// is never hidden again: closing it cloaks it, opening it uncloaks it. A WPF
    /// window that is hidden gives up its render target, and showing it again means
    /// the window manager, the layout, the render thread and the GPU all doing their
    /// part before a single pixel can appear — every open, and slowest of all after a
    /// long idle, which is exactly when a tray panel gets opened. A cloaked window
    /// keeps all of it: its content is already composed, so DWM puts it on screen at
    /// its next frame, without this process running another line of code.
    /// </summary>
    public void Realize(Rect iconRect) => Realize(AnchorFor(iconRect, cursorFallback: false));

    private void Realize(Point anchor)
    {
        if (_realized) return;
        _hwnd = new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
        // Cloaked before it is shown, so not one frame of it reaches the screen. Both
        // the anchor and the budget before Show as well: the first layout happens
        // inside it, and one that runs without a height budget is one WPF gets to
        // clamp on its own terms.
        Cloak(true);
        SetAnchor(anchor);
        ApplyHeightBudget();
        Show();
        _realized = true;
        // Onto the monitor it will open on before anything is measured, so the layout
        // below happens at that monitor's scale rather than wherever it was created.
        ApplyPosition();
        ApplyWindowEffects();
        ApplyHeightBudget();
        UpdateLayout();
        ApplyPosition();
        Stow();
    }

    /// <summary>
    /// Out of the way while closed: no keyboard focus it could be handed, and the
    /// bottom of the z-order, so the window manager never picks it to activate when
    /// whatever was above it goes away.
    /// </summary>
    private void Stow()
    {
        WindowEffects.SetActivatable(_hwnd, false);
        WindowEffects.SendToBack(_hwnd);
    }

    /// <summary>Position next to the tray icon and show.</summary>
    public void ShowNear(Rect iconRect, string callerTrace = "")
    {
        // One open at a time. The common path below never pumps, but the slow ones
        // can — realizing the window waits for a frame — and a tray click dispatched
        // from inside that would start a second open on top of the first.
        if (_inShowNear)
        {
            Logging.Log.Write("panel open ignored: re-entered from inside the one in flight");
            return;
        }
        if (_open && !_closing) return;
        _inShowNear = true;
        try
        {
            ShowNearCore(iconRect, callerTrace);
        }
        finally
        {
            _inShowNear = false;
        }
    }

    /// <summary>
    /// The open itself: on a realized panel, a z-order change, one DWM attribute and
    /// an activation — nothing that lays out, renders or waits. Everything that used
    /// to happen here (showing the window, laying it out, waiting for it to compose,
    /// waking the widgets) either happened long before the click or happens after
    /// the panel is already on screen.
    /// </summary>
    private void ShowNearCore(Rect iconRect, string callerTrace)
    {
        BeginOpenTrace(callerTrace);

        // A close still sliding out is turned round where it is. Stopping the slide
        // first means its completion — which cloaks the window — never runs.
        bool interrupted = _sliding || _closing;
        StopSlide();
        _sliding = false;
        _closing = false;

        Point anchor = AnchorFor(iconRect, cursorFallback: true);

        // Set whenever the window's content may not have been composed yet, which is
        // the one thing that would make uncloaking show the user an unfinished frame.
        bool needsFrame = false;

        // The shell cloaks every window of a virtual desktop the user has left, and our
        // own cloak cannot lift that. A window shown afresh joins the current desktop.
        // (Tool windows are exempt from desktop switching and this panel is one, so
        // this is the fallback for a shell that disagrees, not an expected path.)
        if (_realized && WindowEffects.IsCloakedByShell(_hwnd))
        {
            Logging.Log.Write("panel is cloaked by the shell (another virtual desktop?) — showing it afresh");
            Hide();
            _realized = false;
        }

        if (!_realized)
        {
            // The click beat the idle prewarm, or the window was just rebuilt for a
            // display change: this one open pays for what the prewarm would have done.
            Realize(anchor);
            needsFrame = true;
            MarkOpen("realize");
        }
        else if (anchor != _anchor)
        {
            // The icon moved — a taskbar rearranged, a different monitor, or no icon
            // rectangle and so the cursor. Moving a cloaked window is a SetWindowPos;
            // only a move that crosses into another scale makes WPF lay out again.
            double scale = WindowEffects.GetDpiScale(this);
            SetAnchor(anchor);
            bool budgetChanged = ApplyHeightBudget();
            ApplyPosition();
            if (budgetChanged || Math.Abs(WindowEffects.GetDpiScale(this) - scale) > 0.001)
            {
                ApplyHeightBudget();
                UpdateLayout();
                ApplyPosition();
                needsFrame = true;
            }
            MarkOpen("anchor");
        }
        else if (interrupted)
        {
            SettleAt();
        }

        if (needsFrame)
        {
            AwaitComposedFrame();
            MarkOpen("frame");
        }

        _open = true;
        _openedAtTicks = Environment.TickCount64;
        _reactivatedThisOpen = false;

        WindowEffects.SetActivatable(_hwnd, true);
        WindowEffects.BringToFront(_hwnd);
        if (AnimatesOpen)
        {
            WindowEffects.MoveTo(this, _leftPx, (int)Math.Round(_offscreenTopPx));
            Cloak(false);
            AnimateOpen();
        }
        else
        {
            Cloak(false);
        }
        MarkOpen("uncloak");
        Activate();
        MarkOpen("activate");
        FinishOpenTrace();

        // The panel is on screen at DWM's next frame whatever this thread does now, so
        // the rest waits until after it — and after any input the user has already
        // started, which Background yields to.
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            new Action(AfterOpen));
    }

    /// <summary>
    /// The part of an open the user does not wait for. Every widget's OnShown runs
    /// from here, and with it every sensor subscription and every re-read of state
    /// that may have moved while the panel was closed. Until then the panel shows
    /// what it showed when it was last closed, which is what every widget would have
    /// drawn first anyway.
    /// </summary>
    private void AfterOpen()
    {
        if (!_open || _closing) return;
        EnsureForeground();
        // A slide wakes the widgets when it lands instead: their results resize the
        // window, and a resize mid-flight fights the per-frame move.
        if (!_sliding) _manager.SetPanelVisible(true);
        // The transparency switch is normally caught by the settings broadcast; this
        // is the backstop, and it is one registry read.
        if (WindowEffects.IsTransparencyEnabled() != _transparencyAtApply) ApplyWindowEffects();
        LogGeometry("open");
    }

    /// <summary>
    /// Let the window's current content reach the compositor before it is uncloaked.
    /// Only for the slow paths, where the window has just been shown or re-laid-out:
    /// running the dispatcher's render pass hands the frame to WPF's render thread,
    /// and waiting out one composition gives that thread the time to present it.
    /// </summary>
    private void AwaitComposedFrame()
    {
        Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Render, new Action(() => { }));
        WindowEffects.WaitForComposition();
    }

    public void HidePanel()
    {
        // _closing: the panel is still on screen playing its exit, and a second
        // dismissal (a tray click landing on top of the Deactivated that started this
        // one) would restart the animation from the top.
        if (!_open || _closing) return;

        foreach (var host in _list.Children.OfType<WidgetHost>()) host.CloseSettings();
        if (_addPopup != null) _addPopup.IsOpen = false;

        _open = false;
        // Before the exit rather than after it, so nothing the widgets receive from
        // here on can resize the window under the slide.
        _manager.SetPanelVisible(false);

        if (AnimatesClose) AnimateClose();
        else Conceal();
    }

    /// <summary>
    /// Take the panel off the screen without taking it off the desktop: cloak it,
    /// put it back where it rests, and hand the keyboard to whatever window the user
    /// would have landed on had it been hidden.
    /// </summary>
    private void Conceal()
    {
        StopSlide();
        _sliding = false;
        _closing = false;

        bool hadFocus = WindowEffects.IsForegroundWindow(_hwnd);
        Cloak(true);
        // A slide moved the HWND behind WPF's back; the next open uncloaks it where it
        // stands, so it has to stand where it belongs.
        SettleAt();
        WindowEffects.SetActivatable(_hwnd, false);
        // Before Stow: the window to hand over to is found by walking down the
        // z-order from this one, which needs this one still at the top.
        if (hadFocus) WindowEffects.ActivateWindowBelow(_hwnd);
        Stow();

        // Nothing is on screen from here until the next click, which is the only
        // window this process gets for a blocking compacting collection.
        MemoryTrim.Schedule(Logging.Log.Write);
        // And whatever the open just touched is part of what has to stay in memory.
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.ApplicationIdle,
            new Action(() => ProcessTuning.KeepResident(Logging.Log.Write)));
    }

    /// <summary>
    /// The panel lost the foreground while open — ordinarily the user clicking away,
    /// which closes it.
    ///
    /// Ordinarily. The open activates this window from inside the shell's handling of
    /// a click on the taskbar, and the two can trade the foreground once more as that
    /// click finishes. A deactivation in the first moments of an open is that, not the
    /// user — nobody has seen the panel yet, let alone decided to leave it — so it is
    /// answered by taking the foreground back rather than by closing.
    /// </summary>
    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (!_open || _closing) return;
        // A click on the tray icon deactivates the panel too, but that click is a
        // toggle and the toggle handles the close itself. Hiding here as well would
        // make the press close the panel and the toggle immediately reopen it.
        if (DismissedByCaller?.Invoke() == true) return;
        if (Environment.TickCount64 - _openedAtTicks < ActivationGraceMs && !_reactivatedThisOpen)
        {
            Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Background,
                new Action(EnsureForeground));
            return;
        }
        HidePanel();
    }

    /// <summary>
    /// Take the foreground back if the open lost it — once per open.
    ///
    /// A panel on screen without the foreground is a trap rather than a blemish: it
    /// is dismissed by losing focus, and a window that never had focus never loses
    /// it, so it would sit there until something unrelated moved. One Activate
    /// settles it; if the window manager refuses, the panel is still on screen and
    /// still answers a click on the tray icon, which is the honest fallback.
    /// </summary>
    private void EnsureForeground()
    {
        if (!_open || _closing || _reactivatedThisOpen || WindowEffects.IsForegroundWindow(_hwnd)) return;
        _reactivatedThisOpen = true;
        Logging.Log.Write("panel is on screen without the foreground — reactivating");
        Activate();
    }

    // ---------------------------------------------------------------- animation

    /// <summary>
    /// The Windows 11 flyout entrance: the panel starts completely outside the
    /// monitor and travels the whole way to its resting position, emerging from
    /// behind the taskbar. No fade — the shell does not fade these, and the taskbar
    /// hiding the first part of the trip is what sells it.
    ///
    /// The window composed its frame at rest long before this, while cloaked, so the
    /// jump off-screen carries a complete picture with it: a window only ever paints
    /// the part of itself that is on screen, and one first drawn off-screen would
    /// arrive with everything below the point it had reached still undefined.
    /// </summary>
    private void AnimateOpen()
    {
        _sliding = true;
        StartSlide(_offscreenTopPx, _restTopPx, OpenSlideTime, OpenEase, () =>
        {
            _sliding = false;
            // Not _restTopPx as captured at the start: the panel may have been
            // re-anchored (a widget added) while the slide was running.
            SettleAt();
            // Deferred from the open so the sensor traffic it starts cannot resize
            // the window mid-flight.
            _manager.SetPanelVisible(true);
            LogGeometry("widgets");
        });
    }

    /// <summary>
    /// The exit: the same trip in reverse, back out of the monitor. Windows makes
    /// this noticeably faster than the entrance — dismissal should feel immediate.
    /// Starts from wherever the panel is, so a close that interrupts the entrance
    /// does not jump it back to its resting place first.
    /// </summary>
    private void AnimateClose()
    {
        double from = _sliding ? _lastMovedToPx : _restTopPx;
        StopSlide();
        _sliding = false;
        _closing = true;
        StartSlide(from, _offscreenTopPx, CloseSlideTime, CloseEase, Conceal);
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
        // cloaks the window at the end of a close.
        var done = _slideDone;
        StopSlide();
        done?.Invoke();
    }

    /// <summary>
    /// Drop a slide in flight without running its completion. That is what keeps a
    /// cancelled close from cloaking a panel that has since been reopened.
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

    /// <summary>
    /// Hide or reveal the window through DWM, keeping <see cref="_cloaked"/> in step.
    /// </summary>
    private void Cloak(bool cloaked)
    {
        if (_hwnd == nint.Zero) _hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        _cloaked = cloaked;
        WindowEffects.SetCloaked(_hwnd, cloaked);
    }

    /// <summary>
    /// The point the panel hangs off: the middle of the icon's top edge when the
    /// shell told us where the icon is. Otherwise, for a click, the cursor — which is
    /// where the click happened anyway — and for a panel being realized ahead of any
    /// click, the bottom-right corner of the primary monitor, which is where the
    /// notification area is on nearly every desktop; a click that finds it elsewhere
    /// moves the panel before it uncloaks.
    /// </summary>
    private static Point AnchorFor(Rect iconRect, bool cursorFallback)
    {
        if (!iconRect.IsEmpty) return new Point(iconRect.Left + iconRect.Width / 2, iconRect.Top);
        if (cursorFallback) return WindowEffects.GetCursorPosition();
        Rect primary = WindowEffects.GetMonitorArea(new Point(0, 0));
        return new Point(primary.Right - 1, primary.Bottom - 1);
    }

    private void SetAnchor(Point anchor)
    {
        // Remembered: the panel must keep re-anchoring to the same spot whenever its
        // height changes, and the cursor it may have come from does not stay put.
        _anchor = anchor;
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
    ///
    /// Returns whether the budget moved, which is when the panel needs laying out
    /// again before it can be shown.
    /// </summary>
    private bool ApplyHeightBudget()
    {
        double scale = _anchored ? WindowEffects.GetDpiScale(_anchor) : 0;
        if (scale <= 0) scale = WindowEffects.GetDpiScale(this);
        if (scale <= 0) scale = 1;

        double available = WindowEffects.GetWorkArea(_anchor).Height / scale - 2 * EdgeMargin;
        double budget = Math.Max(MinPanelHeight, available);
        if (Math.Abs(MaxHeight - budget) < 0.01) return false;
        MaxHeight = budget;
        return true;
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
    /// WM_DPICHANGED to a hidden window. The panel is no longer hidden while closed —
    /// it stays shown, cloaked — so the message should reach it now; this stays as
    /// the backstop for whichever display change still slips past, because the cost
    /// of missing one is a panel at the wrong size until the app restarts. There is
    /// no supported way to correct the cache in place; the window has to be built
    /// again. See TrayController.ShowPanel.
    /// </summary>
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
        // Before the window is ever shown. A window that stays shown belongs to the
        // virtual desktop it was shown on, and the shell cloaks it on every other one
        // — where no uncloak of ours could bring it back. Tool windows are on all of
        // them, and a flyout is one anyway: no taskbar button, no Alt+Tab entry.
        WindowEffects.SetToolWindow(_hwnd);
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
    /// Put the window at its resting position, and hand that position back to WPF
    /// after a slide has moved the HWND behind its back, so Left/Top agree with where
    /// the window actually is.
    ///
    /// The move is made directly as well as through the properties. A property set
    /// to the value WPF already holds is no move at all, and WPF's idea of where the
    /// window is can be a slide's frame behind; a window left part-way down a slide
    /// while cloaked would be uncloaked there by the next open.
    /// </summary>
    private void SettleAt()
    {
        WindowEffects.MoveTo(this, _leftPx, (int)Math.Round(_restTopPx));
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
        // Asked when the window is realized and whenever Windows says the setting may
        // have moved (OnUserPreferenceChanged), not on the open: nothing that can be
        // known ahead of the click belongs between the click and the panel.
        _transparencyAtApply = WindowEffects.IsTransparencyEnabled();
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

    /// <summary>
    /// The transparency-effects switch is broadcast as a general settings change.
    /// Raised on a system thread, and the registry is written a moment before the
    /// broadcast settles, so the re-check goes through the dispatcher at idle.
    /// </summary>
    private void OnUserPreferenceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (Microsoft.Win32.UserPreferenceCategory.General
                               or Microsoft.Win32.UserPreferenceCategory.VisualStyle))
            return;
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            new Action(() =>
            {
                if (_realized) ApplyWindowEffects();
            }));
    }

    protected override void OnClosed(EventArgs e)
    {
        // CompositionTarget.Rendering is static, so a slide left running here would
        // keep the closed window alive and moving.
        StopSlide();
        _open = false;
        _realized = false;
        _hwnd = nint.Zero;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _services.Theme.Changed -= OnThemeChanged;
        _manager.LayoutChanged -= RebuildList;
        _manager.Dispose();
        base.OnClosed(e);
    }
}


