using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace DreamTray.Plugins.CyberLcd;

/// <summary>
/// Theme-aware control builders for plugin UI. Same pattern as
/// DreamTray.Plugin.CyberVfd's PluginUi: DreamTray's control styles are
/// ordinary application resources, so looking them up by key gives this
/// plugin the same Windows 11 appearance as the built-in widgets, including
/// live light/dark switching.
///
/// Adds two controls the CyberVfd plugin didn't need: <see cref="ColorSwatch"/>
/// (no color-picker exists anywhere in DreamTray yet, so this is a small
/// self-contained HSL popup rather than a WinForms ColorDialog dependency)
/// and <see cref="GradientStopEditor"/> (an arbitrary-count list of
/// position+color stops, for the "custom gradient" RGB modes).
/// </summary>
internal static class PluginUi
{
    private static Style? Style(string key) => Application.Current?.TryFindResource(key) as Style;

    public static TextBlock Body(string text) => new() { Text = text, Style = Style("BodyText") };

    public static TextBlock Caption(string text) => new()
    {
        Text = text,
        Style = Style("CaptionText"),
        TextWrapping = TextWrapping.Wrap,
    };

    public static TextBlock Value(string text) => new()
    {
        Text = text,
        Style = Style("ValueText"),
        HorizontalAlignment = HorizontalAlignment.Right,
    };

    /// <summary>Label left, control right — matches the host's widget rows.</summary>
    public static Grid Row(UIElement left, UIElement right, double topMargin = 0)
    {
        var grid = new Grid { Margin = new Thickness(0, topMargin, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 1);
        if (left is FrameworkElement fl) fl.VerticalAlignment = VerticalAlignment.Center;
        if (right is FrameworkElement fr)
        {
            fr.VerticalAlignment = VerticalAlignment.Center;
            fr.Margin = new Thickness(8, 0, 0, 0);
        }
        grid.Children.Add(left);
        grid.Children.Add(right);
        return grid;
    }

    public static Grid LabelRow(string label, UIElement right, double topMargin = 0) =>
        Row(Body(label), right, topMargin);

    public static ToggleButton Switch(bool initial, Action<bool> onChanged)
    {
        var toggle = new ToggleButton { IsChecked = initial, Style = Style("ToggleSwitch") };
        toggle.Checked += (_, _) => onChanged(true);
        toggle.Unchecked += (_, _) => onChanged(false);
        return toggle;
    }

    public static Slider Slider(double min, double max, double value, Action<double> onChanged)
    {
        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            Value = Math.Clamp(value, min, max),
            Style = Style("FluentSlider"),
            Width = 140,
        };
        slider.ValueChanged += (_, e) => onChanged(e.NewValue);
        return slider;
    }

    public static ComboBox Combo(IEnumerable<string> items, string? selected, Action<string> onChanged)
    {
        var combo = StyledCombo();
        foreach (var item in items) combo.Items.Add(item);
        if (selected != null) combo.SelectedItem = selected;
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is string s) onChanged(s);
        };
        return combo;
    }

    public static ComboBox StyledCombo(double? width = null)
    {
        var combo = new ComboBox { Style = Style("FluentComboBox"), MinWidth = 120 };
        if (width is double value) combo.Width = value;
        return combo;
    }

    public static TextBox TextInput(string text, double width = 220) => new()
    {
        Text = text,
        Style = Style("FluentTextBox"),
        Width = width,
    };

    public static Button Button(string text, Action onClick)
    {
        var button = new Button { Content = text, Style = Style("FluentButton") };
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>
    /// Vertical stack with the host's settings-card rhythm: every child that has not
    /// asked for a margin of its own gets the standard gap above it. Leaving the
    /// spacing to the container (rather than to each row) is what keeps a caption
    /// tucked under its row while the rows stay evenly spaced.
    /// </summary>
    public static StackPanel Stack(params UIElement[] children)
    {
        var panel = new StackPanel();
        foreach (var child in children)
        {
            if (child is FrameworkElement fe && panel.Children.Count > 0 && fe.Margin == default)
                fe.Margin = new Thickness(0, 10, 0, 0);
            panel.Children.Add(child);
        }
        return panel;
    }

    // ---------------------------------------------------------------- color swatch

    /// <summary>
    /// A small clickable color swatch. Clicking it opens a lightweight popup with
    /// Hue/Saturation/Lightness sliders, a live preview and a hex entry box --
    /// avoids pulling in a WinForms <c>ColorDialog</c> dependency for one control.
    /// </summary>
    public static Border ColorSwatch(Color initial, Action<Color> onChanged)
    {
        var current = initial;
        var swatch = new Border
        {
            Width = 32,
            Height = 20,
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(current),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
        };
        swatch.SetResourceReference(Border.BorderBrushProperty, "ControlStroke");

        swatch.MouseLeftButtonUp += (_, _) =>
        {
            var popup = BuildColorPopup(swatch, current, c =>
            {
                current = c;
                swatch.Background = new SolidColorBrush(c);
                onChanged(c);
            });
            popup.IsOpen = true;
        };

        return swatch;
    }

    /// <summary>
    /// Same recipe as <c>WidgetHost</c>'s widget-settings flyout: a "Card"-styled
    /// border painted with the flyout surface (not the translucent card fill,
    /// which assumes something is already behind it) and the window's light
    /// hairline stroke, so the popup reads as part of DreamTray rather than a
    /// stock WPF control floating over it.
    /// </summary>
    private static Popup BuildColorPopup(UIElement placementTarget, Color initial, Action<Color> onPick)
    {
        RgbToHsl(initial, out double h, out double s, out double l);

        var preview = new Border
        {
            Width = 36,
            Height = 36,
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(initial),
        };
        preview.SetResourceReference(Border.BorderBrushProperty, "ControlStroke");

        var hexBox = new TextBox { Width = 84, Text = HexOf(initial), Style = Style("FluentTextBox") };

        void Apply(Color c)
        {
            preview.Background = new SolidColorBrush(c);
            hexBox.Text = HexOf(c);
            onPick(c);
        }

        var hueSlider = Slider(0, 360, h, v => { h = v; Apply(HslToRgb(h, s, l)); });
        var satSlider = Slider(0, 100, s * 100, v => { s = v / 100.0; Apply(HslToRgb(h, s, l)); });
        var lightSlider = Slider(0, 100, l * 100, v => { l = v / 100.0; Apply(HslToRgb(h, s, l)); });
        hueSlider.Width = satSlider.Width = lightSlider.Width = 160;

        hexBox.LostFocus += (_, _) =>
        {
            if (TryParseHex(hexBox.Text, out var c))
            {
                RgbToHsl(c, out h, out s, out l);
                Apply(c);
            }
            else
            {
                hexBox.Text = HexOf(HslToRgb(h, s, l)); // revert on bad input
            }
        };

        var headerRow = new StackPanel { Orientation = Orientation.Horizontal };
        headerRow.Children.Add(preview);
        hexBox.Margin = new Thickness(10, 0, 0, 0);
        hexBox.VerticalAlignment = VerticalAlignment.Center;
        headerRow.Children.Add(hexBox);

        var content = Stack(
            headerRow,
            Row(Body("Hue"), hueSlider),
            Row(Body("Sat"), satSlider),
            Row(Body("Light"), lightSlider));

        var card = new Border
        {
            Style = Style("Card"),
            Padding = new Thickness(14),
            Background = Application.Current?.TryFindResource("FlyoutBackground") as Brush,
            Child = content,
        };
        card.SetResourceReference(Border.BorderBrushProperty, "WindowStroke");

        return new Popup
        {
            PlacementTarget = placementTarget,
            Placement = PlacementMode.Bottom,
            VerticalOffset = 4,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
            Child = card,
        };
    }

    private static string HexOf(Color c) => "#" + CyberLcdFrame.ColorToHex(c);

    private static bool TryParseHex(string text, out Color color) => CyberLcdFrame.TryParseHex(text, out color);

    private static void RgbToHsl(Color c, out double h, out double s, out double l)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;
        l = (max + min) / 2.0;
        if (delta < 1e-9) { h = 0; s = 0; return; }
        s = l < 0.5 ? delta / (max + min) : delta / (2.0 - max - min);
        if (max == r) h = 60 * (((g - b) / delta) % 6);
        else if (max == g) h = 60 * (((b - r) / delta) + 2);
        else h = 60 * (((r - g) / delta) + 4);
        if (h < 0) h += 360;
    }

    private static Color HslToRgb(double h, double s, double l)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
        double m = l - c / 2.0;
        double r, g, b;
        if (h < 60) (r, g, b) = (c, x, 0.0);
        else if (h < 120) (r, g, b) = (x, c, 0.0);
        else if (h < 180) (r, g, b) = (0.0, c, x);
        else if (h < 240) (r, g, b) = (0.0, x, c);
        else if (h < 300) (r, g, b) = (x, 0.0, c);
        else (r, g, b) = (c, 0.0, x);
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    // ---------------------------------------------------------------- gradient stop editor

    /// <summary>
    /// A vertical list of position+color rows (position slider 0..1, a
    /// <see cref="ColorSwatch"/>, a remove button) plus an "+ Add stop" button.
    /// Any stop count is allowed. `onChanged` fires with the full, position-sorted
    /// list after every edit.
    /// </summary>
    public static StackPanel GradientStopEditor(List<RgbStop> initial, Action<List<RgbStop>> onChanged)
    {
        var stops = new List<RgbStop>(initial.Count > 0 ? initial : DefaultStops());
        var list = new StackPanel();
        var root = new StackPanel();

        void Notify() => onChanged(new List<RgbStop>(stops.OrderBy(s => s.Pos)));

        void Rebuild()
        {
            list.Children.Clear();
            for (int i = 0; i < stops.Count; i++)
            {
                int idx = i; // capture
                var stop = stops[idx];

                var posValue = Value($"{stop.Pos:0.00}");
                posValue.MinWidth = 34;

                var posSlider = Slider(0, 1, stop.Pos, v =>
                {
                    stops[idx] = stops[idx] with { Pos = (float)v };
                    posValue.Text = $"{v:0.00}";
                    Notify();
                });
                posSlider.Width = 90;

                var swatch = ColorSwatch(stop.Color, c =>
                {
                    stops[idx] = stops[idx] with { Color = c };
                    Notify();
                });

                var remove = Button("✕", () =>
                {
                    if (stops.Count <= 2) return; // a gradient needs at least two stops
                    stops.RemoveAt(idx);
                    Rebuild();
                    Notify();
                });
                remove.Padding = new Thickness(6, 0, 6, 0);
                remove.IsEnabled = stops.Count > 2;

                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, i == 0 ? 0 : 6, 0, 0) };
                row.Children.Add(posSlider);
                posValue.Margin = new Thickness(6, 0, 10, 0);
                row.Children.Add(posValue);
                swatch.Margin = new Thickness(0, 0, 10, 0);
                row.Children.Add(swatch);
                row.Children.Add(remove);

                list.Children.Add(row);
            }
        }

        Rebuild();

        var add = Button("+ Add stop", () =>
        {
            float pos = stops.Count == 0 ? 0f : Math.Min(1f, stops.Max(s => s.Pos) + 0.1f);
            stops.Add(new RgbStop(pos, Colors.White));
            Rebuild();
            Notify();
        });
        add.HorizontalAlignment = HorizontalAlignment.Left;
        add.Margin = new Thickness(0, 8, 0, 0);

        root.Children.Add(list);
        root.Children.Add(add);
        return root;
    }

    private static List<RgbStop> DefaultStops() =>
    [
        new RgbStop(0f, Color.FromRgb(0x39, 0xE0, 0xC8)),
        new RgbStop(1f, Color.FromRgb(0x7C, 0x5C, 0xFF)),
    ];
}
