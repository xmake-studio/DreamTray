namespace DreamTray.App.Widgets.BuiltIn;

// ---------------------------------------------------------------------------
// The read-only widgets. Each is a factory plus a few MetricRow definitions.
// ---------------------------------------------------------------------------

internal sealed class TemperatureWidgetFactory : IWidgetFactory
{
    public const string Id = "core.temps";
    public string TypeId => Id;
    public string DisplayName => "Temperatures";
    public string Description => "CPU and GPU temperature.";
    public string Glyph => "\uE9CA";
    public IWidget Create(IWidgetContext context) => new TemperatureWidget(context);
}

internal sealed class TemperatureWidget(IWidgetContext context) : MetricWidget(context)
{
    public override string Title => "Temperatures";
    protected override IReadOnlyList<MetricRow> Rows { get; } =
    [
        new("CPU", s => Fmt.Temp(s.CpuTemp)),
        new("GPU", s => Fmt.Temp(s.GpuTemp)),
    ];
}

internal sealed class ClocksWidgetFactory : IWidgetFactory
{
    public const string Id = "core.clocks";
    public string TypeId => Id;
    public string DisplayName => "Clocks";
    public string Description => "CPU average and peak core clock, GPU clock.";
    public string Glyph => "\uE916";
    public IWidget Create(IWidgetContext context) => new ClocksWidget(context);
}

internal sealed class ClocksWidget(IWidgetContext context) : MetricWidget(context)
{
    public override string Title => "Clocks";
    protected override IReadOnlyList<MetricRow> Rows { get; } =
    [
        new("CPU average", s => Fmt.Ghz(s.CpuClockAvg)),
        new("CPU peak core", s => Fmt.Ghz(s.CpuClockMax)),
        new("GPU", s => Fmt.Mhz(s.GpuClock)),
    ];
}

internal sealed class PowerRailsWidgetFactory : IWidgetFactory
{
    public const string Id = "core.rails";
    public string TypeId => Id;
    public string DisplayName => "Component power";
    public string Description => "Power drawn by the CPU cores, the GPU and the whole package.";
    public string Glyph => "\uEC4A";
    public IWidget Create(IWidgetContext context) => new PowerRailsWidget(context);
}

internal sealed class PowerRailsWidget : MetricWidget
{
    public PowerRailsWidget(IWidgetContext context) : base(context)
    {
        var rows = new List<MetricRow>
        {
            new("CPU cores", s => Fmt.Watts(s.CpuPower)),
            // Package minus cores: on an APU this is the GPU/SoC remainder, and unlike
            // the SMU's own "GPU core power" sensor it does not track CPU load.
            new("GPU / SoC", s => Fmt.Watts(s.GpuPower)),
            new("Package", s => Fmt.Watts(s.PackagePower)),
        };

        // On a laptop the pack is the only current sensor in the machine, so this row
        // doubles as whole-system draw while running off it. A desktop has no such
        // sensor and the row could only ever read "\u2014".
        if (context.Host.Hardware.HasBattery)
            rows.Add(new MetricRow("Battery", Fmt.BatteryFlow));

        Rows = rows;
    }

    public override string Title => "Component power";
    protected override IReadOnlyList<MetricRow> Rows { get; }
}

internal sealed class LoadWidgetFactory : IWidgetFactory
{
    public const string Id = "core.load";
    public string TypeId => Id;
    public string DisplayName => "Load";
    public string Description => "CPU, GPU and memory usage.";
    public string Glyph => "\uE9D9";
    public IWidget Create(IWidgetContext context) => new LoadWidget(context);
}

internal sealed class LoadWidget(IWidgetContext context) : MetricWidget(context)
{
    public override string Title => "Load";
    protected override IReadOnlyList<MetricRow> Rows { get; } =
    [
        new("CPU", s => Fmt.Percent(s.CpuLoad)),
        new("GPU", s => Fmt.Percent(s.GpuLoad)),
        new("Memory", s => s.RamTotalGb > 0 ? $"{s.RamUsedGb:F1} / {s.RamTotalGb:F1} GB" : "—"),
    ];
}

internal sealed class BatteryTimeWidgetFactory : IWidgetFactory
{
    public const string Id = "core.battery";
    public string TypeId => Id;
    public string DisplayName => "Battery";
    public string Description => "Charge level and estimated time to empty or full.";
    public string Glyph => "\uE83F";
    // Asked of the machine, not of the last snapshot: with the panel closed there is
    // no snapshot, and a desktop must not be offered a battery widget on that basis.
    public bool IsAvailable(IPluginHost host) => host.Hardware.HasBattery;
    public IWidget Create(IWidgetContext context) => new BatteryTimeWidget(context);
}

internal sealed class BatteryTimeWidget(IWidgetContext context) : MetricWidget(context)
{
    public override string Title => "Battery";
    protected override IReadOnlyList<MetricRow> Rows { get; } =
    [
        new("Charge", s => s.BatteryLevel >= 0 ? Fmt.Percent(s.BatteryLevel) : "—"),
        new("Source", s => !s.BatteryPresent ? "no battery" : s.OnAcPower ? "charger" : "battery"),
        // Windows only estimates time-to-empty; time-to-full is derived from the
        // measured charge rate and the pack's full-charge energy.
        new("Remaining", s => Fmt.Duration(s.BatteryTimeRemaining)),
    ];
}
