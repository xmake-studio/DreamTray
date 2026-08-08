using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using DreamTray.App.Widgets;

namespace DreamTray.App.Views;

/// <summary>
/// The card drawn around every widget: title, a settings flyout, and — in edit
/// mode — a drag grip and a remove button.
///
/// The chrome is the panel's, not the widget's: a widget only supplies its body
/// and (optionally) a settings view, so a plugin cannot draw a card that looks out
/// of place next to the built-ins.
/// </summary>
internal sealed class WidgetHost : Border
{
    private readonly WidgetInstance _instance;
    private readonly Action<WidgetHost, MouseButtonEventArgs> _onDragStart;
    private readonly Action<string> _onRemove;

    private readonly TextBlock _title;
    private readonly Border _grip;
    private readonly Button _removeButton;
    private Popup? _settingsPopup;

    // The two elements the card borrows from the widget rather than owning. Both
    // are cached by the widget and outlive any single host, so they have to be
    // handed back before another host can adopt them. See Detach().
    private readonly Grid _header;
    private readonly ContentPresenter _body;
    private FrameworkElement? _accessory;

    public WidgetInstance Instance => _instance;

    public WidgetHost(WidgetInstance instance,
                      Action<WidgetHost, MouseButtonEventArgs> onDragStart,
                      Action<string> onRemove)
    {
        _instance = instance;
        _onDragStart = onDragStart;
        _onRemove = onRemove;

        Style = Ui.Find("Card");
        Margin = new Thickness(0, 0, 0, 8);

        _title = new TextBlock
        {
            Text = SafeTitle(),
            // Primary text, not the secondary grey: this is a heading, and Windows
            // only dims text that is genuinely subordinate.
            Style = Ui.Find("BodyText"),
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        _grip = BuildGrip();

        _removeButton = Ui.IconButton("\uE711", "Remove widget", () => _onRemove(_instance.InstanceId));
        _removeButton.Visibility = Visibility.Collapsed;

        var settingsButton = Ui.IconButton("\uE712", "Widget settings", ToggleSettings);
        settingsButton.Visibility = HasSettings() ? Visibility.Visible : Visibility.Collapsed;

        var header = _header = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(_grip, 0);
        Grid.SetColumn(_title, 1);
        Grid.SetColumn(settingsButton, 3);
        Grid.SetColumn(_removeButton, 4);
        header.Children.Add(_grip);
        header.Children.Add(_title);
        header.Children.Add(settingsButton);
        header.Children.Add(_removeButton);

        // The title's star column is what pushes the accessory right; it sits inside
        // the header so a status line costs no extra row.
        var accessory = _accessory = SafeAccessory();
        if (accessory != null)
        {
            accessory.VerticalAlignment = VerticalAlignment.Center;
            accessory.HorizontalAlignment = HorizontalAlignment.Right;
            accessory.Margin = new Thickness(8, 0, 4, 0);
            Grid.SetColumn(accessory, 2);
            Orphan(accessory);
            header.Children.Add(accessory);
        }

        var body = _body = new ContentPresenter();
        try { Orphan(_instance.Widget.View); body.Content = _instance.Widget.View; }
        catch (Exception ex)
        {
            Logging.Log.Write($"widget '{_instance.Factory.TypeId}' view failed: {ex}");
            body.Content = Ui.Caption("This widget failed to load. See the log for details.");
        }

        // A widget whose whole control fits in the header (the theme switch) hands back
        // a collapsed body; without this the card would keep the gap under the title
        // and the row it saved would come straight back as padding.
        if (body.Content is UIElement { Visibility: Visibility.Collapsed })
            header.Margin = new Thickness(0);

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(body);
        Child = stack;
    }

    /// <summary>
    /// Hand the widget's own elements back before this card is discarded. A widget
    /// caches its view (and any header accessory) and hands out the same instance
    /// every time, so a replacement card cannot adopt one that is still a logical
    /// child of this one — WPF throws rather than re-parenting, which would abort
    /// the rebuild and silently drop every widget below it.
    /// </summary>
    public void Detach()
    {
        CloseSettings();
        if (_accessory != null) _header.Children.Remove(_accessory);
        _body.Content = null;
    }

    /// <summary>
    /// Belt and braces for the same problem: an element still parented elsewhere
    /// (a host that was dropped without Detach, or a widget handing out something
    /// it also placed in its own view) is removed from that parent first.
    /// </summary>
    private static void Orphan(FrameworkElement? element)
    {
        switch (element?.Parent)
        {
            case Panel panel: panel.Children.Remove(element); break;
            case ContentPresenter presenter: presenter.Content = null; break;
            case ContentControl control: control.Content = null; break;
            case Decorator decorator: decorator.Child = null; break;
        }
    }

    private string SafeTitle()
    {
        try { return _instance.Widget.Title; }
        catch { return _instance.Factory.DisplayName; }
    }

    private FrameworkElement? SafeAccessory()
    {
        try { return _instance.Widget.HeaderAccessory; }
        catch (Exception ex)
        {
            Logging.Log.Write($"widget '{_instance.Factory.TypeId}' header accessory failed: {ex}");
            return null;
        }
    }

    private bool HasSettings()
    {
        // Probing means building the view once, so cache the answer rather than
        // constructing a settings panel for every widget on every panel open.
        try { return _instance.Widget.CreateSettingsView() != null; }
        catch { return false; }
    }

    private Border BuildGrip()
    {
        var grip = new Border
        {
            Background = Brushes.Transparent,
            Width = 20,
            Height = 24,
            Margin = new Thickness(0, 0, 4, 0),
            Cursor = Cursors.SizeAll,
            ToolTip = "Drag to reorder",
            Visibility = Visibility.Collapsed,
            Child = new TextBlock
            {
                Text = "\uE76F",
                Style = Ui.Find("GlyphText"),
                FontSize = 12,
                Foreground = Application.Current?.TryFindResource("TextTertiary") as Brush,
            },
        };
        grip.PreviewMouseLeftButtonDown += (_, e) => _onDragStart(this, e);
        return grip;
    }

    /// <summary>Show or hide the drag grip and remove button.</summary>
    public void SetEditMode(bool editing)
    {
        _grip.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        _removeButton.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
    }

    public void RefreshTitle() => _title.Text = SafeTitle();

    private void ToggleSettings()
    {
        if (_settingsPopup is { IsOpen: true })
        {
            _settingsPopup.IsOpen = false;
            return;
        }

        FrameworkElement? content;
        try { content = _instance.Widget.CreateSettingsView(); }
        catch (Exception ex)
        {
            Logging.Log.Write($"widget settings failed: {ex}");
            content = Ui.Caption("This widget's settings failed to load.");
        }
        if (content == null) return;

        var card = new Border
        {
            Style = Ui.Find("Card"),
            Padding = new Thickness(14),
            Background = Application.Current?.TryFindResource("FlyoutBackground") as Brush,
            Child = content,
        };
        // Same reasoning as the add-widget popup: a floating surface gets the light
        // hairline, not the dark card stroke.
        card.SetResourceReference(Border.BorderBrushProperty, "WindowStroke");

        _settingsPopup = new Popup
        {
            Child = card,
            PlacementTarget = this,
            Placement = PlacementMode.Bottom,
            HorizontalOffset = -8,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
            IsOpen = true,
        };
    }

    /// <summary>Close any open settings flyout (the panel is closing).</summary>
    public void CloseSettings()
    {
        if (_settingsPopup != null) _settingsPopup.IsOpen = false;
    }
}


