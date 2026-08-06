using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace DreamTray.Plugins.CyberVfd;

/// <summary>
/// Theme-aware control builders for plugin UI.
///
/// A plugin cannot reference the host's internal helpers, but it does not need to:
/// DreamTray's control styles are ordinary application resources, so looking them
/// up by key ("BodyText", "FluentSlider", …) gives a plugin the same Windows 11
/// appearance as the built-in widgets, including live light/dark switching.
/// This class exists to show that pattern as much as to serve this plugin.
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
        var combo = new ComboBox { Style = Style("FluentComboBox"), MinWidth = 120 };
        foreach (var item in items) combo.Items.Add(item);
        if (selected != null) combo.SelectedItem = selected;
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is string s) onChanged(s);
        };
        return combo;
    }

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
}
