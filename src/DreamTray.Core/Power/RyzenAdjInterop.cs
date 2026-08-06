using System.Runtime.InteropServices;

namespace DreamTray.Power;

/// <summary>
/// Minimal binding to <c>libryzenadj.dll</c> (the RyzenAdj project), which talks to
/// the AMD SMU mailbox to move the APU's power limits at runtime.
///
/// The DLL is loaded lazily and by path, not by the usual <c>[DllImport]</c>
/// resolution, so a machine without it degrades to "TDP control unavailable"
/// instead of crashing at first touch. Ship <c>libryzenadj.dll</c> +
/// <c>WinRing0x64.sys</c> + <c>WinRing0x64.dll</c> in <c>native\</c> next to the exe.
///
/// Requires elevation: the SMU mailbox is reached through a kernel driver.
/// </summary>
internal sealed class RyzenAdjInterop : IDisposable
{
    // libryzenadj's C API. All setters take milliwatts and return 0 on success.
    private delegate nint InitDelegate();
    private delegate void CleanupDelegate(nint ry);
    private delegate int SetLimitDelegate(nint ry, uint milliwatts);
    private delegate int TableDelegate(nint ry);
    private delegate float GetFloatDelegate(nint ry);
    private delegate int GetIntDelegate(nint ry);

    private nint _library;
    private nint _access;

    private CleanupDelegate? _cleanup;
    private SetLimitDelegate? _setStapm, _setFast, _setSlow;
    private TableDelegate? _initTable, _refreshTable;
    private GetFloatDelegate? _getStapmLimit, _getStapmValue,
                              _getFastLimit, _getFastValue,
                              _getSlowLimit, _getSlowValue;
    private GetIntDelegate? _getCpuFamily;

    public bool IsLoaded => _access != nint.Zero;
    public string Status { get; private set; } = "not initialised";
    public int CpuFamily { get; private set; } = -1;

    /// <summary>Candidate locations, in order: the native folder, then beside the exe, then PATH.</summary>
    private static IEnumerable<string> CandidatePaths()
    {
        string baseDir = AppContext.BaseDirectory;
        yield return Path.Combine(baseDir, "native", "libryzenadj.dll");
        yield return Path.Combine(baseDir, "libryzenadj.dll");
        yield return "libryzenadj.dll"; // let the loader search PATH
    }

    /// <summary>Load the DLL and open an SMU handle. Safe to call once; returns success.</summary>
    public bool TryInitialize()
    {
        if (IsLoaded) return true;

        PrepareWinRing0();

        string? loadedFrom = null;
        foreach (var path in CandidatePaths())
        {
            if (NativeLibrary.TryLoad(path, out _library)) { loadedFrom = path; break; }
        }
        if (_library == nint.Zero)
        {
            Status = "libryzenadj.dll not found — see native\\README.md";
            return false;
        }

        try
        {
            var init = Bind<InitDelegate>("init_ryzenadj");
            _cleanup = Bind<CleanupDelegate>("cleanup_ryzenadj");
            _setStapm = Bind<SetLimitDelegate>("set_stapm_limit");
            _setFast = Bind<SetLimitDelegate>("set_fast_limit");
            _setSlow = Bind<SetLimitDelegate>("set_slow_limit");
            _initTable = BindOptional<TableDelegate>("init_table");
            _refreshTable = BindOptional<TableDelegate>("refresh_table");
            _getStapmLimit = BindOptional<GetFloatDelegate>("get_stapm_limit");
            _getStapmValue = BindOptional<GetFloatDelegate>("get_stapm_value");
            _getFastLimit = BindOptional<GetFloatDelegate>("get_fast_limit");
            _getFastValue = BindOptional<GetFloatDelegate>("get_fast_value");
            _getSlowLimit = BindOptional<GetFloatDelegate>("get_slow_limit");
            _getSlowValue = BindOptional<GetFloatDelegate>("get_slow_value");
            _getCpuFamily = BindOptional<GetIntDelegate>("get_cpu_family");

            _access = init();
            if (_access == nint.Zero)
            {
                Status = "RyzenAdj could not open the SMU — " + DiagnoseInitFailure();
                Unload();
                return false;
            }

            _initTable?.Invoke(_access); // power-table reads fail silently without this
            CpuFamily = _getCpuFamily?.Invoke(_access) ?? -1;
            Status = $"RyzenAdj ready ({Path.GetFileName(loadedFrom)}, family {CpuFamily})";
            return true;
        }
        catch (Exception ex)
        {
            Status = $"RyzenAdj init failed: {ex.Message}";
            Unload();
            return false;
        }
    }

    /// <summary>
    /// Put WinRing0 where it can actually be found.
    ///
    /// RyzenAdj reaches the SMU through WinRing0, which has two location rules that
    /// bite here and produce the same unhelpful "could not open the SMU" either way:
    ///
    /// <list type="number">
    /// <item><c>WinRing0x64.dll</c> is a plain dependency of libryzenadj.dll, so the
    /// OS loader looks for it beside the *executable* and on PATH — not in whatever
    /// folder libryzenadj.dll was loaded from. Adding the native folder to the
    /// search path fixes that.</item>
    /// <item><c>WinRing0x64.sys</c> is located by WinRing0 itself, which builds the
    /// path from <c>GetModuleFileName(NULL)</c> — the main executable's directory,
    /// again not the DLL's. There is no API to redirect it, so the driver file has
    /// to be beside the exe; it is copied there if the user dropped it in native\.</item>
    /// </list>
    /// </summary>
    private void PrepareWinRing0()
    {
        string baseDir = AppContext.BaseDirectory;
        string nativeDir = Path.Combine(baseDir, "native");

        try
        {
            if (Directory.Exists(nativeDir)) SetDllDirectory(nativeDir);
        }
        catch { /* non-fatal: the DLL may already be beside the exe */ }

        // WinRing0 looks for its driver next to the exe and nowhere else.
        foreach (var file in new[] { "WinRing0x64.sys", "WinRing0x64.dll" })
        {
            try
            {
                string beside = Path.Combine(baseDir, file);
                string inNative = Path.Combine(nativeDir, file);
                if (!File.Exists(beside) && File.Exists(inNative))
                    File.Copy(inNative, beside);
            }
            catch (Exception ex)
            {
                // Read-only install directory: report it rather than failing opaquely
                // later with "could not open the SMU".
                Status = $"could not place {file} next to the executable: {ex.Message}";
            }
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string path);

    private T Bind<T>(string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, name));

    private T? BindOptional<T>(string name) where T : Delegate =>
        NativeLibrary.TryGetExport(_library, name, out nint p)
            ? Marshal.GetDelegateForFunctionPointer<T>(p)
            : null;

    /// <summary>
    /// Set the sustained limit. STAPM, slow and fast are written together: OEM
    /// software often lowers only one of them, and the lowest wins, so setting a
    /// single limit frequently appears to do nothing.
    /// </summary>
    public bool SetLimits(int watts)
    {
        if (!IsLoaded) return false;
        uint mw = (uint)(watts * 1000);
        // Fast (PPT boost) gets a little headroom so short bursts still behave.
        uint fastMw = (uint)(Math.Min(watts + 2, watts * 1.15) * 1000);

        int a = _setStapm!(_access, mw);
        int b = _setSlow!(_access, mw);
        int c = _setFast!(_access, fastMw);
        return a == 0 && b == 0 && c == 0;
    }

    /// <summary>Live limits from the SMU power table, or null when unavailable.</summary>
    public TdpReadback? Read()
    {
        if (!IsLoaded || _refreshTable == null || _getStapmLimit == null) return null;
        if (_refreshTable(_access) != 0) return null;
        try
        {
            return new TdpReadback(
                _getStapmLimit(_access), _getStapmValue?.Invoke(_access) ?? 0,
                _getFastLimit?.Invoke(_access) ?? 0, _getFastValue?.Invoke(_access) ?? 0,
                _getSlowLimit?.Invoke(_access) ?? 0, _getSlowValue?.Invoke(_access) ?? 0);
        }
        catch { return null; }
    }

    /// <summary>
    /// <c>init_ryzenadj</c> returns a null handle for several unrelated reasons and
    /// reports none of them. Check the ones that are actually observable so the UI
    /// can say something more useful than "it didn't work".
    /// </summary>
    private static string DiagnoseInitFailure()
    {
        string baseDir = AppContext.BaseDirectory;

        if (!IsElevated())
            return "DreamTray is not running as administrator, which the driver requires";

        foreach (var file in new[] { "WinRing0x64.sys", "WinRing0x64.dll" })
            if (!File.Exists(Path.Combine(baseDir, file)))
                return $"{file} is missing — it must sit next to DreamTray.exe (see native\\README.md)";

        // Memory Integrity blocks the WinRing0 driver on many current systems.
        if (IsMemoryIntegrityRunning())
            return "the WinRing0 driver was refused, most likely by Windows Memory Integrity " +
                   "(Core isolation). Turning that off is a security trade-off — your call";

        return "the driver could not be loaded; see the log";
    }

    private static bool IsElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    private static bool IsMemoryIntegrityRunning()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity");
            return key?.GetValue("Enabled") is int v && v == 1;
        }
        catch { return false; }
    }

    private void Unload()
    {
        if (_library != nint.Zero) { NativeLibrary.Free(_library); _library = nint.Zero; }
    }

    public void Dispose()
    {
        if (_access != nint.Zero) { try { _cleanup?.Invoke(_access); } catch { } _access = nint.Zero; }
        Unload();
    }
}
