using System.Windows.Threading;
using DreamTray.Display;
using DreamTray.Logging;
using DreamTray.Plugins;
using DreamTray.Power;
using DreamTray.Sensors;
using DreamTray.Settings;
using DreamTray.Startup;
using DreamTray.Theme;

namespace DreamTray;

/// <summary>
/// Composition root. Constructs every service once, wires the ones that depend on
/// each other, and hands out the <see cref="IPluginHost"/> views that plugins and
/// widgets see. The app layer holds exactly one of these.
/// </summary>
public sealed class AppServices : IDisposable
{
    public SettingsStore Settings { get; }
    public ThemeService Theme { get; }
    public SensorSampler Sensors { get; }
    public BrightnessService Brightness { get; }
    public DisplayModeService DisplayModes { get; }
    public TdpService Tdp { get; }
    public PowerPolicyService PowerPolicy { get; }
    public AutostartService Autostart { get; }
    public PluginManager Plugins { get; }
    public IHardwareControl Hardware { get; }

    /// <summary>Called when a plugin or widget asks for a tray balloon.</summary>
    public Action<string, string>? NotificationSink { get; set; }

    public AppServices(Dispatcher dispatcher)
    {
        Settings = new SettingsStore(Log.Write);
        Theme = new ThemeService(dispatcher)
        {
            Preference = Enum.TryParse<ThemePreference>(Settings.Current.Theme, out var p)
                ? p : ThemePreference.System,
        };
        Sensors = new SensorSampler(dispatcher, Log.Write);
        Brightness = new BrightnessService(Log.Write);
        DisplayModes = new DisplayModeService(Log.Write);
        Tdp = new TdpService(Log.Write);
        PowerPolicy = new PowerPolicyService(Log.Write);
        Autostart = new AutostartService(Log.Write);
        Hardware = new HardwareControl(this);

        DetectTdpRange();
        ApplyTdpSettings();

        Plugins = new PluginManager(this, Log.Write);
    }

    /// <summary>
    /// Establish the power-limit range for the chip this copy is actually running on,
    /// so nothing in the app has to assume a particular CPU. Runs at every start:
    /// the probe reads the limits in force at that moment, so a first run on battery
    /// reports a low ceiling and a later run on the charger corrects it upwards.
    /// The range is only ever widened, and only while the user has not set it by hand.
    /// </summary>
    private void DetectTdpRange()
    {
        var t = Settings.Current.Tdp;
        int min = t.MinWatts, max = t.MaxWatts;

        if (t.RangeAutoDetected && Tdp.IsAvailable && Tdp.DetectRange() is { } probed)
        {
            min = min > 0 ? Math.Min(min, probed.Min) : probed.Min;
            max = Math.Max(max, probed.Max);
            Log.Write($"tdp: detected range {probed.Min}–{probed.Max} W, using {min}–{max} W");
        }

        // Nothing to probe (no PawnIO, or an unreadable power table) and nothing
        // stored: fall back rather than leaving a zero-width slider.
        if (min <= 0) min = TdpService.FallbackMinWatts;
        if (max <= min) max = Math.Max(TdpService.FallbackMaxWatts, min + 5);

        // The per-source defaults are seeded from the range for the same reason: a
        // fixed pair of watt numbers would only suit the machine they were picked on.
        bool changed = min != t.MinWatts || max != t.MaxWatts
                       || t.AcWatts <= 0 || t.DcWatts <= 0;

        if (t.AcWatts <= 0) t.AcWatts = max;
        if (t.DcWatts <= 0) t.DcWatts = (int)Math.Round(max * 0.45);

        t.MinWatts = min;
        t.MaxWatts = max;
        t.AcWatts = Math.Clamp(t.AcWatts, min, max);
        t.DcWatts = Math.Clamp(t.DcWatts, min, max);

        if (changed) Settings.Save();
    }

    /// <summary>Push the persisted TDP policy into the service. Call after editing it.</summary>
    public void ApplyTdpSettings()
    {
        var t = Settings.Current.Tdp;
        Tdp.MinWatts = t.MinWatts;
        Tdp.MaxWatts = t.MaxWatts;
        Tdp.AcWatts = t.AcWatts;
        Tdp.DcWatts = t.DcWatts;
        Tdp.UsePowerSourceDefaults = t.UsePowerSourceDefaults;
        Tdp.ReapplySeconds = t.ReapplySeconds;
    }

    /// <summary>
    /// Re-assert the user's limit at startup: the machine has just booted, so
    /// whatever the OEM service set is in force, not what DreamTray last applied.
    /// </summary>
    public void RestoreTdpOnStartup()
    {
        if (!Tdp.IsAvailable) return;
        var t = Settings.Current.Tdp;
        if (t.UsePowerSourceDefaults) Tdp.ApplyPowerSourceDefault();
        else if (t.LastWatts > 0) Tdp.Apply(t.LastWatts);
    }

    /// <summary>A host view scoped to one plugin or widget's settings bag.</summary>
    public IPluginHost CreateHost(IStorage storage) => new Host(this, storage);

    public void Dispose()
    {
        Plugins.Dispose();
        Sensors.Dispose();
        Brightness.Dispose();
        Tdp.Dispose();
        Theme.Dispose();
        Settings.Dispose();
        Log.Shutdown();
    }

    // ---------------------------------------------------------------- host view

    private sealed class Host(AppServices services, IStorage storage) : IPluginHost
    {
        public SystemSnapshot? Latest => services.Sensors.Latest;
        public IStorage Storage => storage;
        public IHardwareControl Hardware => services.Hardware;
        public IThemeInfo Theme => services.Theme;

        public IDisposable SubscribeSensors(TimeSpan interval, Action<SystemSnapshot> onSample) =>
            services.Sensors.Subscribe(interval, onSample);

        public void Log(string message) => Logging.Log.Write(message);

        public void Notify(string title, string message) =>
            services.NotificationSink?.Invoke(title, message);
    }

    // ---------------------------------------------------------------- hardware facade

    private sealed class HardwareControl(AppServices s) : IHardwareControl
    {
        public IReadOnlyList<DisplayTarget> GetDisplays(bool refresh = false) =>
            s.Brightness.GetDisplays(refresh);

        public void RefreshDisplaysAsync(Action? onCompleted = null) =>
            s.Brightness.RefreshAsync(onCompleted);

        public bool SetBrightness(string displayId, int percent) =>
            s.Brightness.SetBrightness(displayId, percent);

        public void SetAllBrightness(int percent) => s.Brightness.SetAll(percent);

        public ITdpControl? Tdp => s.Tdp.IsAvailable ? s.Tdp : null;

        public bool HasBattery => MachineCapabilities.HasBattery;

        public IReadOnlyList<DisplayDevice> GetDisplayDevices() => s.DisplayModes.GetDevices();
        public IReadOnlyList<DisplayMode> GetModes(string deviceName) => s.DisplayModes.GetModes(deviceName);
        public DisplayMode? GetCurrentMode(string deviceName) => s.DisplayModes.GetCurrentMode(deviceName);
        public bool SetMode(string deviceName, DisplayMode mode) => s.DisplayModes.SetMode(deviceName, mode);

        public bool SetWindowsDarkMode(bool dark) => s.Theme.SetWindowsDarkMode(dark);

        public IPowerPolicy? PowerPolicy => s.PowerPolicy.IsAvailable ? s.PowerPolicy : null;
    }
}
