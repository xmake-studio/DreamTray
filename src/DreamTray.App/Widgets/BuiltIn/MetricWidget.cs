using System.Windows;
using System.Windows.Controls;

namespace DreamTray.App.Widgets.BuiltIn;

/// <summary>
/// Base for the read-only widgets: a list of "label — value" rows refreshed from
/// each snapshot. Subclasses only describe their rows, so adding a readout is a
/// few lines rather than a new view.
/// </summary>
internal abstract class MetricWidget(IWidgetContext context) : WidgetBase(context)
{
    private readonly List<TextBlock> _valueBlocks = [];

    /// <summary>The rows to show, top to bottom.</summary>
    protected abstract IReadOnlyList<MetricRow> Rows { get; }

    protected override bool NeedsSensors => true;

    protected override FrameworkElement BuildView()
    {
        var panel = new StackPanel();
        _valueBlocks.Clear();

        foreach (var row in Rows)
        {
            var value = Ui.Value("—");
            _valueBlocks.Add(value);
            panel.Children.Add(Ui.LabelRow(row.Label, value, panel.Children.Count == 0 ? 0 : 4));
        }
        return panel;
    }

    protected override void OnSample(SystemSnapshot snapshot)
    {
        var rows = Rows;
        for (int i = 0; i < _valueBlocks.Count && i < rows.Count; i++)
        {
            string text;
            try { text = rows[i].Format(snapshot); }
            catch { text = "—"; }
            _valueBlocks[i].Text = text;
        }
    }
}

/// <summary>One labelled readout inside a <see cref="MetricWidget"/>.</summary>
/// <param name="Label">Left-hand caption.</param>
/// <param name="Format">Turns a snapshot into the displayed value.</param>
internal sealed record MetricRow(string Label, Func<SystemSnapshot, string> Format);

/// <summary>Shared formatting so every widget renders "unknown" the same way.</summary>
internal static class Fmt
{
    /// <summary>Sensors read 0 when unavailable; showing "0 °C" would be a lie.</summary>
    public static string Temp(float celsius) => celsius > 0 ? $"{celsius:F0} °C" : "—";
    public static string Watts(float w) => w > 0 ? $"{w:F1} W" : "—";
    public static string Ghz(float ghz) => ghz > 0 ? $"{ghz:F2} GHz" : "—";
    public static string Mhz(float mhz) => mhz > 0 ? $"{mhz:F0} MHz" : "—";
    public static string Percent(float fraction) => $"{fraction * 100:F0}%";

    /// <summary>
    /// Battery flow as a signed rate. The sign carries the direction, so the row
    /// stays one short value instead of a value plus a word.
    /// </summary>
    public static string BatteryFlow(SystemSnapshot s) => s.SystemPowerKind switch
    {
        SystemPowerKind.Discharging => $"−{s.SystemPower:F1} W",
        SystemPowerKind.Charging => $"+{s.SystemPower:F1} W",
        SystemPowerKind.AcIdle => "idle on AC",
        _ => "—",
    };

    public static string Duration(TimeSpan? span)
    {
        if (span is not { TotalMinutes: > 0 }) return "—";
        var t = span.Value;
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes:00} min" : $"{t.Minutes} min";
    }
}
