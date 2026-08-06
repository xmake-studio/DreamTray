using System.Runtime.InteropServices;
using DreamTray.Power;
using DreamTray.Sensors;

namespace DreamTray.App;

/// <summary>
/// <c>DreamTray.exe --dump</c> — opens a console and prints every hardware sensor
/// LibreHardwareMonitor can see, plus the state of the brightness and TDP
/// back-ends. When a reading is wrong or missing on a given machine, this is the
/// first thing to run: it shows the raw sensor names the mapping code keys off.
/// </summary>
internal static class DiagnosticDump
{
    [DllImport("kernel32.dll")] private static extern bool AllocConsole();

    public static int Run()
    {
        AllocConsole();
        var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        Console.SetOut(stdout);

        Console.WriteLine($"DreamTray diagnostics — elevated: {Startup.AutostartService.IsElevated}, " +
                          $"battery: {Power.MachineCapabilities.HasBattery}");
        Console.WriteLine();

        Console.WriteLine("=== hardware sensors ===");
        try { Console.WriteLine(SensorSampler.DumpSensors()); }
        catch (Exception ex) { Console.WriteLine($"sensor enumeration failed: {ex}"); }

        Console.WriteLine("=== snapshot ===");
        try
        {
            using var svc = new SensorService();
            svc.Read();
            Thread.Sleep(1000);
            var s = svc.Read();
            Console.WriteLine($"cpu {s.CpuLoad:P0} {s.CpuTemp:F1}°C {s.CpuClockAvg:F2}/{s.CpuClockMax:F2} GHz {s.CpuPower:F1} W");
            Console.WriteLine($"gpu {s.GpuLoad:P0} {s.GpuTemp:F1}°C {s.GpuClock:F0} MHz {s.GpuPower:F1} W");
            Console.WriteLine($"pkg {s.PackagePower:F1} W   ram {s.RamUsedGb:F1}/{s.RamTotalGb:F1} GB");
            Console.WriteLine($"batt {s.BatteryPower:+0.0;-0.0} W  level {s.BatteryLevel:P0}  ac {s.OnAcPower}  " +
                              $"kind {s.SystemPowerKind}  remaining {s.BatteryTimeRemaining}");
        }
        catch (Exception ex) { Console.WriteLine($"snapshot failed: {ex}"); }

        Console.WriteLine();
        Console.WriteLine("=== brightness ===");
        try
        {
            using var brightness = new Display.BrightnessService(Console.WriteLine);
            foreach (var d in brightness.GetDisplays(refresh: true))
                Console.WriteLine($"  {d.Id,-10} {d.Kind,-8} {d.Brightness,4}%  {d.Name}");
        }
        catch (Exception ex) { Console.WriteLine($"brightness enumeration failed: {ex}"); }

        Console.WriteLine();
        Console.WriteLine("=== display modes ===");
        try
        {
            var modes = new Display.DisplayModeService(Console.WriteLine);
            foreach (var dev in modes.GetDevices())
                Console.WriteLine($"  {dev.DeviceName}  {dev.FriendlyName}  current {modes.GetCurrentMode(dev.DeviceName)}");
        }
        catch (Exception ex) { Console.WriteLine($"display enumeration failed: {ex}"); }

        Console.WriteLine();
        Console.WriteLine("=== TDP ===");
        try
        {
            using var tdp = new TdpService(Console.WriteLine);
            Console.WriteLine($"  {tdp.StatusText}");
            Console.WriteLine($"  readback: {tdp.Read()?.ToString() ?? "unavailable"}");
            Console.WriteLine($"  detected slider range: " +
                              (tdp.DetectRange() is { } r ? $"{r.Min}–{r.Max} W" : "undetectable, using fallback"));
        }
        catch (Exception ex) { Console.WriteLine($"tdp probe failed: {ex}"); }

        Console.WriteLine();
        Console.WriteLine("Press Enter to close.");
        Console.ReadLine();
        return 0;
    }

    /// <summary>
    /// <c>--set-brightness &lt;id|all&gt; &lt;percent&gt;</c> — drives the real
    /// <see cref="Display.BrightnessService"/> from the command line and reads the
    /// value back. Isolates "the hardware refused it" from "the UI never called it",
    /// which is otherwise guesswork.
    /// </summary>
    public static int SetBrightness(string target, int percent)
    {
        AllocConsole();
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });

        using var brightness = new Display.BrightnessService(Console.WriteLine);
        var displays = brightness.GetDisplays(refresh: true);

        foreach (var d in displays)
            Console.WriteLine($"  before: {d.Id,-10} {d.Kind,-8} {d.Brightness,4}%  {d.Name}");

        if (target == "all") brightness.SetAll(percent);
        else if (!brightness.SetBrightness(target, percent))
        {
            Console.WriteLine($"no display with id '{target}'");
            return 1;
        }

        // The write is queued to a worker thread; give it time to reach the hardware.
        Thread.Sleep(1500);
        brightness.RefreshValues();

        Console.WriteLine();
        foreach (var d in brightness.GetDisplays())
            Console.WriteLine($"  after:  {d.Id,-10} {d.Kind,-8} {d.Brightness,4}%  {d.Name}");

        Console.WriteLine();
        Console.WriteLine("Press Enter to close.");
        Console.ReadLine();
        return 0;
    }
}
