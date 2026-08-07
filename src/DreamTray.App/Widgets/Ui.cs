using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace DreamTray.App.Widgets;

/// <summary>
/// Small builders for the handful of layouts every widget and settings page uses.
///
/// The panel is built in code rather than XAML because widgets are created
/// dynamically from a registry — a XAML file per widget would mean a
/// DataTemplate lookup and a view-model layer for what is usually three controls.
/// Styles still come from the theme dictionary via <see cref="Style"/> lookups, so
/// nothing here hard-codes an appearance.
/// </summary>
internal static class Ui
{
    public static Style? Find(string key) => Application.Current?.TryFindResource(key) as Style;

    public static TextBlock Body(string text) =>
        new() { Text = text, Style = Find("BodyText") };

    public static TextBlock Caption(string text) =>
        new() { Text = text, Style = Find("CaptionText"), TextWrapping = TextWrapping.Wrap };

    public static TextBlock Value(string text) =>
        new() { Text = text, Style = Find("ValueText"), HorizontalAlignment = HorizontalAlignment.Right };

    public static TextBlock Glyph(string glyph, double size = 16) =>
        new() { Text = glyph, Style = Find("GlyphText"), FontSize = size };

    /// <summary>Label on the left, control hugging the right — the workhorse row.</summary>
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

    public static StackPanel Stack(params UIElement[] children)
    {
        var panel = new StackPanel();
        foreach (var c in children) panel.Children.Add(c);
        return panel;
    }

    public static Button Button(string content, Action onClick, bool accent = false)
    {
        var button = new Button { Content = content, Style = Find(accent ? "AccentButton" : "FluentButton") };
        button.Click += (_, _) => onClick();
        return button;
    }

    public static Button IconButton(string glyph, string tooltip, Action onClick)
    {
        var button = new Button
        {
            Content = glyph,
            ToolTip = tooltip,
            Style = Find("IconButton"),
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    public static ToggleButton Switch(bool initial, Action<bool> onChanged)
    {
        var toggle = new ToggleButton { IsChecked = initial, Style = Find("ToggleSwitch") };
        toggle.Checked += (_, _) => onChanged(true);
        toggle.Unchecked += (_, _) => onChanged(false);
        return toggle;
    }

    /// <summary>
    /// A slider that reports continuously while dragging (so brightness tracks the
    /// thumb) — callers are expected to coalesce, which the hardware services do.
    /// </summary>
    public static Slider Slider(double min, double max, double value, Action<double> onChanged,
                                double tick = 1)
    {
        var slider = new System.Windows.Controls.Slider
        {
            Minimum = min,
            Maximum = max,
            Value = Math.Clamp(value, min, max),
            Style = Find("FluentSlider"),
            SmallChange = tick,
            LargeChange = Math.Max(tick, (max - min) / 10),
            IsSnapToTickEnabled = true,
            TickFrequency = tick,
        };
        slider.ValueChanged += (_, e) => onChanged(e.NewValue);
        return slider;
    }

    /// <param name="dimmed">
    /// Optional predicate marking entries that are valid but off the beaten path — they
    /// stay selectable and are drawn faded so the ordinary choices stand out.
    /// </param>
    public static ComboBox Combo<T>(IEnumerable<T> items, T? selected, Action<T> onChanged,
                                    Func<T, string>? label = null, Func<T, bool>? dimmed = null)
    {
        var combo = new ComboBox { Style = Find("FluentComboBox") };
        var list = items.ToList();
        foreach (var item in list)
        {
            var entry = new ComboEntry<T>(item, label?.Invoke(item) ?? item?.ToString() ?? "");
            if (dimmed?.Invoke(item) == true)
                combo.Items.Add(new ComboBoxItem { Content = entry, Opacity = DimmedOpacity });
            else
                combo.Items.Add(entry);
        }

        if (selected != null)
        {
            int index = list.FindIndex(i => EqualityComparer<T>.Default.Equals(i, selected));
            if (index >= 0) combo.SelectedIndex = index;
        }

        combo.SelectionChanged += (_, _) =>
        {
            if (Unwrap(combo.SelectedItem) is ComboEntry<T> entry) onChanged(entry.Item);
        };
        return combo;
    }

    private const double DimmedOpacity = 0.5;

    private static object? Unwrap(object? item) =>
        item is ComboBoxItem container ? container.Content : item;

    /// <summary>Wrapper so a combo shows a friendly label without a DataTemplate.</summary>
    private sealed record ComboEntry<T>(T Item, string Label)
    {
        public override string ToString() => Label;
    }

    /// <summary>Integer entry with spin-free +/- semantics: a text box that validates on change.</summary>
    public static TextBox Number(int value, int min, int max, Action<int> onChanged, double width = 64)
    {
        var box = new TextBox
        {
            Text = value.ToString(),
            Style = Find("FluentTextBox"),
            Width = width,
            TextAlignment = TextAlignment.Right,
        };
        box.TextChanged += (_, _) =>
        {
            if (int.TryParse(box.Text, out int parsed))
                onChanged(Math.Clamp(parsed, min, max));
        };
        box.LostFocus += (_, _) =>
        {
            // Snap the displayed text back to what was actually accepted, so an
            // out-of-range or empty entry does not silently linger.
            if (!int.TryParse(box.Text, out int parsed)) parsed = value;
            box.Text = Math.Clamp(parsed, min, max).ToString();
        };
        return box;
    }

    public static CheckBox Check(string label, bool initial, Action<bool> onChanged)
    {
        var check = new CheckBox { Content = label, IsChecked = initial, Style = Find("FluentCheckBox") };
        check.Checked += (_, _) => onChanged(true);
        check.Unchecked += (_, _) => onChanged(false);
        return check;
    }

    public static Border Separator() => new()
    {
        Height = 1,
        Margin = new Thickness(0, 8, 0, 8),
        Background = Application.Current?.TryFindResource("CardStroke") as Brush,
    };

    /// <summary>A settings flyout body: fixed width, comfortable spacing.</summary>
    public static StackPanel SettingsPanel(params UIElement[] children)
    {
        var panel = Stack(children);
        panel.Width = 280;
        foreach (var child in panel.Children.OfType<FrameworkElement>())
            if (child.Margin == default) child.Margin = new Thickness(0, 0, 0, 8);
        return panel;
    }
}
