using System.Windows;
using System.Windows.Threading;
using DreamTray.App.Themes;
using DreamTray.App.Views;
using DreamTray.Logging;

namespace DreamTray.App;

/// <summary>
/// Process entry point and lifetime owner.
///
/// There is no main window: the app lives in the tray, so
/// <c>ShutdownMode="OnExplicitShutdown"</c> keeps it alive while every window is
/// closed, and <see cref="TrayController"/> owns what the user actually sees.
/// </summary>
public partial class App : Application
{
    private static Mutex? _singleInstance;
    private AppServices? _services;
    private TrayController? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // `--dump` prints every detected sensor and exits — the fastest way to find
        // out why a metric reads zero on a machine.
        if (e.Args.Contains("--dump"))
        {
            DiagnosticDump.Run();
            Shutdown();
            return;
        }

        // `--tdp-probe` writes a limit and samples the readback, to tell an override
        // by the EC apart from a write that never lands.
        int tdpProbe = Array.IndexOf(e.Args, "--tdp-probe");
        if (tdpProbe >= 0)
        {
            int watts = tdpProbe + 1 < e.Args.Length && int.TryParse(e.Args[tdpProbe + 1], out int w) ? w : 22;
            TdpProbe.Run(watts);
            Shutdown();
            return;
        }

        int setBrightness = Array.IndexOf(e.Args, "--set-brightness");
        if (setBrightness >= 0 && setBrightness + 2 < e.Args.Length &&
            int.TryParse(e.Args[setBrightness + 2], out int percent))
        {
            DiagnosticDump.SetBrightness(e.Args[setBrightness + 1], percent);
            Shutdown();
            return;
        }

        // One tray icon, one sensor driver, one SMU handle. A second instance would
        // fight the first over all three. The self-test takes no exclusive resources,
        // so it is allowed to run alongside a live instance.
        bool selfTest = e.Args.Contains("--selftest");
        if (!selfTest)
        {
            _singleInstance = new Mutex(initiallyOwned: true, "DreamTray.SingleInstance", out bool first);
            if (!first)
            {
                Shutdown();
                return;
            }
        }

        DispatcherUnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Write($"fatal: {args.ExceptionObject}");

        Log.Write("--- DreamTray starting ---");

        _services = new AppServices(Dispatcher);
        ApplyTheme(translucent: false);
        _services.Theme.Changed += () => ApplyTheme(_lastTranslucent);

        if (selfTest)
        {
            _services.Plugins.LoadAll();
            int shotIndex = Array.IndexOf(e.Args, "--screenshot");
            string? shotDir = shotIndex >= 0 && shotIndex + 1 < e.Args.Length
                ? e.Args[shotIndex + 1]
                : null;
            Environment.ExitCode = SelfTest.Run(_services, shotDir);
            Shutdown();
            return;
        }

        _tray = new TrayController(_services);
        _tray.Start();

        // Plugins come up after the tray so a slow plugin never delays the icon.
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            _services.Plugins.LoadAll();
            _services.RestoreTdpOnStartup();
            // After the plugins, so plugin widgets are part of the panel that gets
            // built — and so nothing here delays the tray icon appearing.
            _tray.Prewarm();
            Log.Write("startup complete");
        });
    }

    private bool _lastTranslucent;

    /// <summary>
    /// Repaint every control. <paramref name="translucent"/> comes from whether the
    /// panel actually got a DWM backdrop, so the surfaces are only see-through when
    /// there is a material behind them.
    /// </summary>
    public void ApplyTheme(bool translucent)
    {
        _lastTranslucent = translucent;
        if (_services != null)
            ThemeManager.Apply(Resources, _services.Theme.IsDark, translucent);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // A widget or plugin throwing on the UI thread must not take the tray down.
        Log.Write($"unhandled UI exception: {e.Exception}");
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _services?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    /// <summary>Quit from the tray menu.</summary>
    public static void Quit() => Current.Shutdown();
}
