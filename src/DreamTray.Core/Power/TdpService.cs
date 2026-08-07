using Microsoft.Win32;

namespace DreamTray.Power;

/// <summary>
/// Owns the APU power limit: the value the user picked, the policy that keeps it
/// applied, and the AC/battery defaults.
///
/// The policy lives here rather than in the TDP widget because it has to keep
/// working with the panel closed and even with the widget removed — OEM power
/// software (Armoury Crate, MyASUS, Lenovo Vantage, …) rewrites the limits on
/// power-source changes and on its own timers, so DreamTray re-asserts them.
///
/// Everything is a no-op when <see cref="IsAvailable"/> is false, so the rest of
/// the app never has to special-case a machine without PawnIO.
/// </summary>
public sealed class TdpService : ITdpControl, IDisposable
{
    private readonly PawnIoSmu _adj = new();
    private readonly Action<string> _log;
    private readonly object _gate = new();

    private System.Threading.Timer? _reapplyTimer;
    private int _appliedWatts;
    private bool _lastOnAc;

    public TdpService(Action<string> log)
    {
        _log = log;
        _adj.TryInitialize();
        _log($"tdp: {_adj.Status}");
        _lastOnAc = MachineCapabilities.IsOnAcPower;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    // ---------------------------------------------------------------- ITdpControl

    public string StatusText => _adj.Status;
    public bool IsAvailable => _adj.IsLoaded;
    public int MinWatts { get; set; } = FallbackMinWatts;
    public int MaxWatts { get; set; } = FallbackMaxWatts;
    public int AppliedWatts => _appliedWatts;

    public bool Apply(int watts)
    {
        if (!IsAvailable) return false;
        watts = Math.Clamp(watts, MinWatts, MaxWatts);
        lock (_gate)
        {
            bool ok = _adj.SetLimits(watts);
            if (ok) _appliedWatts = watts;
            else _log($"tdp: applying {watts} W failed");
            return ok;
        }
    }

    public TdpReadback? Read()
    {
        lock (_gate) return _adj.Read();
    }

    // ---------------------------------------------------------------- slider range

    /// <summary>
    /// Used when the silicon cannot be probed at all. Wide enough to be usable on
    /// anything from a 15 W ultraportable up, narrow enough not to invite damage;
    /// <see cref="DetectRange"/> replaces it as soon as the SMU answers.
    /// </summary>
    public const int FallbackMinWatts = 4;
    public const int FallbackMaxWatts = 45;

    /// <summary>
    /// Work out sensible slider bounds for whatever chip this actually is, from the
    /// limits the firmware is running with right now.
    ///
    /// There is no "what is this part rated for" query — the SMU exposes the live
    /// power table and nothing else — so the highest of the three configured limits
    /// (STAPM, slow, fast) stands in for the top of the range. That is the number the
    /// OEM decided this chassis can cool, which is the honest ceiling anyway: a
    /// 15 W-class ultraportable lands near 15–30 W and a 54 W-class part near 60.
    ///
    /// It reads whatever is in force at this moment, so probing on battery can
    /// under-report. Callers therefore only ever *widen* a range they already have
    /// (see the auto-detect path in AppServices), which makes a low first reading
    /// self-correcting on a later launch.
    ///
    /// Returns null when the power table is unreadable or gives a nonsense value.
    /// </summary>
    public (int Min, int Max)? DetectRange()
    {
        var r = Read();
        if (r == null) return null;

        float highest = Math.Max(r.StapmLimit, Math.Max(r.SlowLimit, r.FastLimit));
        // The SMU returns 0 for an uninitialised table, and absurd values when the
        // table layout does not match the DLL's model — neither is a range.
        if (highest is < 5f or > 300f || float.IsNaN(highest)) return null;

        int max = (int)Math.Ceiling(highest / 5f) * 5;
        // A quarter of the ceiling, which is roughly where every AMD mobile part
        // stops being able to hold a limit at all.
        int min = Math.Clamp((int)Math.Round(max * 0.25), 4, max - 5);
        return (min, max);
    }

    // ---------------------------------------------------------------- policy

    private int _reapplySeconds;

    /// <summary>Re-apply the current limit every N seconds. 0 disables the timer.</summary>
    public int ReapplySeconds
    {
        get => _reapplySeconds;
        set
        {
            _reapplySeconds = Math.Max(0, value);
            RestartTimer();
        }
    }

    /// <summary>Apply <see cref="AcWatts"/>/<see cref="DcWatts"/> when the charger comes and goes.</summary>
    public bool UsePowerSourceDefaults { get; set; }
    public int AcWatts { get; set; } = 35;
    public int DcWatts { get; set; } = 15;

    /// <summary>Raised (on a threadpool thread) whenever the service changes the limit itself.</summary>
    public event Action<int>? LimitChangedByPolicy;

    /// <summary>
    /// Apply the right default for the current power source. Called at startup and
    /// on every AC transition when <see cref="UsePowerSourceDefaults"/> is on.
    /// </summary>
    public void ApplyPowerSourceDefault()
    {
        if (!IsAvailable || !UsePowerSourceDefaults) return;
        bool onAc = MachineCapabilities.IsOnAcPower;
        int target = onAc ? AcWatts : DcWatts;
        if (Apply(target))
        {
            _log($"tdp: {(onAc ? "AC" : "battery")} default applied — {target} W");
            LimitChangedByPolicy?.Invoke(target);
        }
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.StatusChange) return;
        bool onAc = MachineCapabilities.IsOnAcPower;
        if (onAc == _lastOnAc) return; // StatusChange also fires for battery-level ticks
        _lastOnAc = onAc;

        if (UsePowerSourceDefaults)
        {
            // The OEM service reacts to the same event; give it a moment to finish
            // writing its own limits so ours is the one that sticks.
            Task.Delay(2000).ContinueWith(_ => ApplyPowerSourceDefault());
        }
    }

    private void RestartTimer()
    {
        _reapplyTimer?.Dispose();
        _reapplyTimer = null;
        if (ReapplySeconds <= 0 || !IsAvailable) return;

        var period = TimeSpan.FromSeconds(ReapplySeconds);
        _reapplyTimer = new System.Threading.Timer(_ =>
        {
            if (_appliedWatts <= 0) return;
            lock (_gate) _adj.SetLimits(_appliedWatts);
        }, null, period, period);
    }

    public void Dispose()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _reapplyTimer?.Dispose();
        _adj.Dispose();
    }
}
