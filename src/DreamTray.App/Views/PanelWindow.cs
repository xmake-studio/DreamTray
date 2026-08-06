using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using DreamTray.App.Interop;
using DreamTray.App.Widgets;

namespace DreamTray.App.Views;

/// <summary>
/// The flyout that opens on a tray-icon click: a vertical stack of widgets the
/// user can reorder, remove and add to.
///
/// It is created once and hidden rather than closed, so reopening is instant.
/// While hidden it tells <see cref="WidgetManager"/> so every widget drops its
/// sensor subscription вЂ” that is what makes the app cost nothing when idle.
/// </summary>
internal sealed class PanelWindow : Window
{
    private const double PanelWidth = 340;
    private const double EdgeMargin = 12;
    private const double CornerRadius = 16;

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
    private WidgetHost? _dragHost;
    private int _dragFromIndex = -1;
    private Popup? _addPopup;

    // Where the panel was anchored when it was opened. Adding or removing a widget
    // changes the content height, and SizeToContent grows the window from its top-
    // left corner вЂ” so without re-anchoring, the panel drifts off the work area.
    private Point _anchor;
    private bool _anchored;
    private bool _cornerUpdateQueued;

    public PanelWindow(AppServices services, Action openSettings)
    {
        _services = services;
        _openSettings = openSettings;

        AppState.Attach(services);
        _registry = new WidgetRegistry(services);
        _manager = new WidgetManager(services, _registry);

        Title = "DreamTray";
        Width = PanelWidth;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
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

        Deactivated += (_, _) => HidePanel();
        SizeChanged += OnSizeChanged;
        PreviewKeyDown += OnKeyDown;
        SourceInitialized += (_, _) => ApplyWindowEffects();
        _services.Theme.Changed += OnThemeChanged;
    }

    /// <summary>The placed widgets, for <c>--selftest</c> to drive add/remove through.</summary>
    internal WidgetManager Manager => _manager;

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

        // The cap is recomputed from the work area on every open (see
        // UpdateScrollerMaxHeight); this initial value only has to be sane for the
        // first measure pass.
        _scroller = new ScrollViewer
        {
            Style = Ui.Find("ThinScrollViewer"),
            MaxHeight = 620,
            Content = _list,
        };

        var root = new Grid { Margin = new Thickness(14, 12, 14, 14) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(header, 0);
        Grid.SetRow(_scroller, 1);
        root.Children.Add(header);
        root.Children.Add(_scroller);

        // Drag-reorder is tracked at the window level so the pointer can leave the
        // grip (and the card) without dropping the gesture.
        root.MouseMove += OnDragMove;
        root.MouseLeftButtonUp += (_, _) => EndDrag();
        return root;
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
        Mouse.Capture(Content as IInputElement);
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
            // manager's order вЂ” the visual and the model converge there.
            _manager.Move(_dragFromIndex, toIndex);
        }
        _dragFromIndex = -1;
        _ = host;
    }

    // ---------------------------------------------------------------- show / hide

    /// <summary>Position next to the tray icon and show.</summary>
    public void ShowNear(Rect iconRect)
    {
        Show();
        // Re-check the backdrop on every open: the user can switch transparency
        // effects on or off while the panel is alive, and the window is created
        // once and reused, so SourceInitialized alone would never see the change.
        ApplyWindowEffects();
        SetAnchor(iconRect);
        // Height is content-driven, so lay out before measuring the chrome or
        // positioning вЂ” otherwise both run on a stale (zero) height and the panel
        // lands off the bottom of the screen.
        UpdateLayout();
        UpdateScrollerMaxHeight();
        UpdateLayout();
        ApplyPosition();
        Activate();
        _manager.SetPanelVisible(true);
    }

    /// <summary>
    /// When the panel was last hidden. Clicking the tray icon while the panel is
    /// open deactivates it first, so by the time the click arrives the panel is
    /// already hidden and a naive toggle would reopen it immediately.
    /// </summary>
    public long LastHiddenTicks { get; private set; }

    public void HidePanel()
    {
        if (!IsVisible) return;
        LastHiddenTicks = Environment.TickCount64;
        foreach (var host in _list.Children.OfType<WidgetHost>()) host.CloseSettings();
        if (_addPopup != null) _addPopup.IsOpen = false;
        _manager.SetPanelVisible(false);
        Hide();
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
    /// Cap the widget list at whatever the current monitor's work area leaves after
    /// the header and margins, so a long list scrolls instead of growing past the
    /// screen edge. Small screens and tall taskbars both land here.
    /// </summary>
    private void UpdateScrollerMaxHeight()
    {
        double scale = WindowEffects.GetDpiScale(this);
        if (scale <= 0) scale = 1;

        double availableDips = WindowEffects.GetWorkArea(_anchor).Height / scale - 2 * EdgeMargin;

        // Everything the window spends on chrome: root margins, the header, and the
        // window border. Derived from the laid-out sizes rather than hardcoded, so
        // it stays right if the header ever grows a row.
        double chrome = ActualHeight - _scroller.ActualHeight;
        if (chrome < 0 || double.IsNaN(chrome)) chrome = 0;

        _scroller.MaxHeight = Math.Max(120, availableDips - chrome);
    }

    /// <summary>
    /// Keep the panel pinned to its anchor as widgets are added or removed. The
    /// window grows downward from Top, so a taller panel would otherwise run past
    /// the bottom of the work area (or float away from the taskbar when it shrinks).
    /// </summary>
    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_anchored && IsVisible) ApplyPosition();
        // The clip region is in device pixels, so it has to be rebuilt for the new size.
        ScheduleCornerRadius();
    }

    /// <summary>
    /// Rebuild the rounded-corner clip once the HWND has actually taken its new
    /// size. SizeChanged fires from the layout pass, but with SizeToContent the
    /// window is resized after it вЂ” so building the region here would read the old
    /// (shorter) window rect and clip the bottom of the panel away, silently
    /// hiding whatever widgets did not fit the previous height.
    /// </summary>
    private void ScheduleCornerRadius()
    {
        if (_cornerUpdateQueued) return;
        _cornerUpdateQueued = true;
        // Background runs after both Render and Loaded, which is where the resize
        // and the reposition land.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
        {
            _cornerUpdateQueued = false;
            WindowEffects.SetCornerRadius(this, CornerRadius);
        }));
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

        Left = left / scale;
        Top = top / scale;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) HidePanel();
    }

    // ---------------------------------------------------------------- appearance

    private void ApplyWindowEffects()
    {
        WindowEffects.SetDarkMode(this, _services.Theme.IsDark);
        // Deferred for the same reason as OnSizeChanged: on an open the content has
        // not been laid out yet, so the window rect is still the previous one.
        ScheduleCornerRadius();

        // Acrylic is the material Windows uses for its own tray flyouts. If DWM
        // refuses it вЂ” an older build, or transparency effects switched off вЂ” the
        // theme falls back to opaque surfaces and the window paints them itself.
        bool translucent = WindowEffects.TryApplyBackdrop(this, WindowEffects.Backdrop.Acrylic);
        (Application.Current as App)?.ApplyTheme(translucent);

        if (translucent)
        {
            WindowEffects.ExtendFrameIntoClientArea(this);
            Background = Brushes.Transparent;
        }
        else
        {
            Background = Application.Current?.TryFindResource("WindowBackground") as Brush
                         ?? Brushes.White;
        }
    }

    private void OnThemeChanged()
    {
        WindowEffects.SetDarkMode(this, _services.Theme.IsDark);
        if (Background is SolidColorBrush { Color.A: 255 })
            Background = Application.Current?.TryFindResource("WindowBackground") as Brush;
    }

    protected override void OnClosed(EventArgs e)
    {
        _services.Theme.Changed -= OnThemeChanged;
        _manager.LayoutChanged -= RebuildList;
        _manager.Dispose();
        base.OnClosed(e);
    }
}


