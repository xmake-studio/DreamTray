using System.Diagnostics;
using System.Runtime.InteropServices;
using DreamTray.Power;

namespace DreamTray.App;

/// <summary>
/// <c>DreamTray.exe --tdp-probe [watts]</c> — answers the one question a stuck TDP
/// slider raises: does our write never take effect, or does it take effect and then
/// get overwritten by something else?
///
/// Many OEM laptops (Tongfang/Mechrevo barebones especially) have an EC that
/// re-asserts the power limits of its current Fn-key performance preset, in
/// firmware, whether or not the vendor's own tray application is running. The SMU
/// accepts our command and reports success; the EC then puts its own number back a
/// moment later. From a single before/after reading that is indistinguishable from
/// the write silently failing, so this samples the limit on a tight schedule.
///
/// Phase 1 writes once and watches the value decay back.
/// Phase 2 rewrites continuously to see whether a fast re-apply loop can hold it.
/// </summary>
internal static class TdpProbe
{
    [DllImport("kernel32.dll")] private static extern bool AllocConsole();

    /// <summary>Sample points after the single write, in milliseconds.</summary>
    private static readonly int[] SampleMs = [0, 100, 250, 500, 1000, 2000, 3000, 5000, 8000, 12000];

    public static int Run(int watts)
    {
        AllocConsole();
        var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        Console.SetOut(stdout);

        Console.WriteLine($"DreamTray TDP probe — target {watts} W, " +
                          $"elevated: {Startup.AutostartService.IsElevated}");
        Console.WriteLine();

        using var tdp = new TdpService(_ => { }); // quiet: the probe prints its own story
        Console.WriteLine($"backend: {tdp.StatusText}");
        if (!tdp.IsAvailable)
        {
            Console.WriteLine("\nBackend unavailable — nothing to probe.");
            return Finish(1);
        }

        // The service clamps to its configured range; widen it so the probe can ask
        // for whatever the caller specified.
        tdp.MinWatts = Math.Min(tdp.MinWatts, watts);
        tdp.MaxWatts = Math.Max(tdp.MaxWatts, watts);

        Console.WriteLine($"baseline: {Format(tdp.Read())}");
        Console.WriteLine();

        // ---- phase 1: write once, watch it decay -------------------------------
        Console.WriteLine($"=== phase 1 — single write of {watts} W ===");
        var clock = Stopwatch.StartNew();
        bool accepted = tdp.Apply(watts);
        Console.WriteLine($"  SMU accepted the write: {accepted}");
        Console.WriteLine();
        Console.WriteLine("   t (ms)   STAPM lim   fast lim   slow lim   STAPM now");

        float firstSeen = float.NaN;
        float lastSeen = float.NaN;
        foreach (int at in SampleMs)
        {
            int wait = at - (int)clock.ElapsedMilliseconds;
            if (wait > 0) Thread.Sleep(wait);

            var r = tdp.Read();
            if (r == null) { Console.WriteLine($"  {at,6}   <readback unavailable>"); continue; }

            if (float.IsNaN(firstSeen)) firstSeen = r.StapmLimit;
            lastSeen = r.StapmLimit;
            Console.WriteLine($"  {at,6}   {r.StapmLimit,9:F1}   {r.FastLimit,8:F1}   " +
                              $"{r.SlowLimit,8:F1}   {r.StapmValue,9:F1}");
        }

        Console.WriteLine();
        Console.WriteLine("  verdict: " + Verdict(watts, accepted, firstSeen, lastSeen));

        // ---- phase 2: hammer it ------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("=== phase 2 — re-applying every 250 ms for 6 s ===");
        Console.WriteLine("   t (ms)   STAPM lim   STAPM now");

        clock.Restart();
        float worst = float.NaN;
        while (clock.ElapsedMilliseconds < 6000)
        {
            tdp.Apply(watts);
            Thread.Sleep(250);
            var r = tdp.Read();
            if (r == null) continue;
            if (float.IsNaN(worst) || Math.Abs(r.StapmLimit - watts) > Math.Abs(worst - watts))
                worst = r.StapmLimit;
            Console.WriteLine($"  {clock.ElapsedMilliseconds,6}   {r.StapmLimit,9:F1}   {r.StapmValue,9:F1}");
        }

        Console.WriteLine();
        Console.WriteLine("  worst deviation from target while hammering: " +
                          (float.IsNaN(worst) ? "n/a" : $"{worst:F1} W"));
        Console.WriteLine("  " + (float.IsNaN(worst) || Math.Abs(worst - watts) > 2f
            ? "A fast re-apply loop does NOT hold the limit — something else owns it."
            : "A fast re-apply loop DOES hold the limit — raising the re-apply rate would fix the widget."));

        return Finish(0);
    }

    private static string Verdict(int watts, bool accepted, float first, float last)
    {
        if (!accepted)
            return "the SMU rejected the write outright — this is not an override, it is a failed command.";
        if (float.IsNaN(first))
            return "no readback at all; cannot tell.";

        bool tookEffect = Math.Abs(first - watts) <= 2f;
        bool heldOn = Math.Abs(last - watts) <= 2f;

        if (tookEffect && heldOn)
            return "the write took effect and held. The limit is being applied correctly.";
        if (tookEffect && !heldOn)
            return $"the write took effect ({first:F1} W) and was then overwritten ({last:F1} W) — " +
                   "something re-asserts the limit, almost certainly the EC's performance preset.";
        return $"the write was accepted but never took effect (still {first:F1} W immediately after) — " +
               "the limit is locked or owned by a higher-priority source.";
    }

    private static string Format(TdpReadback? r) =>
        r == null
            ? "unavailable"
            : $"STAPM {r.StapmLimit:F1} W (now {r.StapmValue:F1}), " +
              $"fast {r.FastLimit:F1} W (now {r.FastValue:F1}), " +
              $"slow {r.SlowLimit:F1} W (now {r.SlowValue:F1})";

    private static int Finish(int code)
    {
        Console.WriteLine();
        Console.WriteLine("Press Enter to close.");
        Console.ReadLine();
        return code;
    }
}
