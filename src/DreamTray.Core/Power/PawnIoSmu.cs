using System.Runtime.InteropServices;

namespace DreamTray.Power;

/// <summary>
/// Talks to the AMD SMU mailbox through <b>PawnIO</b> — the sandboxed ring-0 driver
/// that replaced WinRing0 across the hardware-tooling ecosystem.
///
/// This used to go through RyzenAdj, which reaches the SMU with WinRing0. WinRing0
/// hands user mode an unrestricted "read/write any MSR and any physical page"
/// primitive (CVE-2020-14979), so it is on Microsoft's vulnerable-driver blocklist
/// and every current anti-cheat — Easy Anti-Cheat, BattlEye, Vanguard — refuses to
/// run alongside a process holding it. PawnIO instead runs *signed Pawn bytecode
/// modules* inside the kernel and exposes only the ioctls those modules declare, so
/// a module that drives the SMU mailbox grants no general memory access. That is
/// what makes precise TDP control possible without tripping anti-cheat.
///
/// The driver is installed once by the user (see <c>native\README.md</c>); this
/// class only loads <c>PawnIOLib.dll</c> and the <c>RyzenSMU</c> module blob. A
/// machine without either degrades to "TDP control unavailable" rather than
/// throwing.
///
/// Requires elevation: PawnIO's device object is admin-only.
/// </summary>
internal sealed class PawnIoSmu : IDisposable
{
    private nint _library;
    private nint _handle;

    private OpenDelegate? _open;
    private LoadDelegate? _load;
    private ExecuteDelegate? _execute;
    private CloseDelegate? _close;

    /// <summary>Cross-process lock every SMU/PCI tool honours (LHM, HWiNFO, RyzenAdj).</summary>
    private System.Threading.Mutex? _pciMutex;

    private SmuCommands _commands;
    private Mp1Mailbox _mp1;
    private bool _pmTableResolved;

    public bool IsLoaded => _handle != nint.Zero;
    public string Status { get; private set; } = "not initialised";
    public AmdCodeName CodeName { get; private set; } = AmdCodeName.Undefined;
    public uint PmTableVersion { get; private set; }

    // ------------------------------------------------------------------ init

    /// <summary>Load PawnIO + the RyzenSMU module and identify the silicon. Safe to re-call.</summary>
    public bool TryInitialize()
    {
        if (IsLoaded) return true;

        if (!TryLoadLibrary()) return false;      // sets Status
        if (!TryOpenAndLoadModule()) return false; // sets Status

        // The command IDs for the power limits differ by silicon generation, and
        // sending the wrong one to the SMU is not a no-op — so an unrecognised part
        // disables the feature rather than guessing.
        if (!TryIdentify()) { Shutdown(); return false; }

        // Resolving the power-table base is what makes Read() possible. It is not
        // fatal if it fails: setting limits still works, we just cannot read back.
        _pmTableResolved = TryResolvePmTable();

        Status = _pmTableResolved
            ? $"PawnIO ready ({CodeName}, PM table {PmTableVersion:X8})"
            : $"PawnIO ready ({CodeName}, power table unreadable — limits apply, readback disabled)";
        return true;
    }

    private bool TryLoadLibrary()
    {
        foreach (var path in PawnIoLibCandidates())
        {
            if (NativeLibrary.TryLoad(path, out _library)) break;
        }
        if (_library == nint.Zero)
        {
            Status = "PawnIO is not installed — see native\\README.md";
            return false;
        }

        try
        {
            _open = Bind<OpenDelegate>("pawnio_open");
            _load = Bind<LoadDelegate>("pawnio_load");
            _execute = Bind<ExecuteDelegate>("pawnio_execute");
            _close = Bind<CloseDelegate>("pawnio_close");
            return true;
        }
        catch (Exception ex)
        {
            Status = $"PawnIOLib is present but unusable: {ex.Message}";
            Unload();
            return false;
        }
    }

    /// <summary>The installer's default location first, then PATH.</summary>
    private static IEnumerable<string> PawnIoLibCandidates()
    {
        foreach (var programFiles in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                 })
        {
            if (!string.IsNullOrEmpty(programFiles))
                yield return Path.Combine(programFiles, "PawnIO", "PawnIOLib.dll");
        }
        yield return "PawnIOLib.dll"; // let the loader search PATH
    }

    private bool TryOpenAndLoadModule()
    {
        if (!TryReadModuleBlob(out byte[] blob, out string source))
        {
            Status = "the RyzenSMU PawnIO module could not be found — see native\\README.md";
            Unload();
            return false;
        }

        int hr = _open!(out _handle);
        if (hr < 0 || _handle == nint.Zero)
        {
            _handle = nint.Zero;
            Status = DiagnoseOpenFailure(hr);
            Unload();
            return false;
        }

        hr = _load!(_handle, blob, (nuint)blob.Length);
        if (hr < 0)
        {
            Status = $"PawnIO rejected the RyzenSMU module from {source} (0x{hr:X8}) — " +
                     "the blob is corrupt, or its signature does not match this PawnIO build";
            Shutdown();
            return false;
        }

        // Only meaningful once a module is loaded, and only advisory — carry on
        // without it if another tool has locked it down.
        TryOpenPciMutex();
        return true;
    }

    /// <summary>
    /// Find the RyzenSMU module blob.
    ///
    /// LibreHardwareMonitor already carries a signed copy as an embedded resource
    /// (it drives the same SMU for its AMD sensors), so the normal path needs no
    /// files from the user at all. A blob dropped in <c>native\</c> still wins, which
    /// is the escape hatch for running a newer module than the one LHM shipped with.
    /// </summary>
    private static bool TryReadModuleBlob(out byte[] blob, out string source)
    {
        string baseDir = AppContext.BaseDirectory;
        foreach (var path in new[]
                 {
                     Path.Combine(baseDir, "native", "RyzenSMU.bin"),
                     Path.Combine(baseDir, "RyzenSMU.bin"),
                 })
        {
            try
            {
                if (File.Exists(path))
                {
                    blob = File.ReadAllBytes(path);
                    source = Path.GetFileName(path);
                    return true;
                }
            }
            catch { /* unreadable — fall through to the embedded copy */ }
        }

        try
        {
            var assembly = typeof(LibreHardwareMonitor.Hardware.Computer).Assembly;
            // Matched by suffix rather than by full name: the resource namespace has
            // moved between LHM releases, the file name has not.
            string? name = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("PawnIo.RyzenSMU.bin", StringComparison.OrdinalIgnoreCase));
            if (name != null)
            {
                using var stream = assembly.GetManifestResourceStream(name);
                if (stream != null)
                {
                    using var buffer = new MemoryStream();
                    stream.CopyTo(buffer);
                    blob = buffer.ToArray();
                    source = "LibreHardwareMonitor";
                    return true;
                }
            }
        }
        catch { /* fall through */ }

        blob = [];
        source = string.Empty;
        return false;
    }

    private bool TryIdentify()
    {
        if (!TryExecute("ioctl_get_code_name", [], 1, out var outBuf))
        {
            Status = "PawnIO could not identify the processor";
            return false;
        }

        CodeName = (AmdCodeName)(long)outBuf[0];
        var commands = SmuCommands.For(CodeName);
        if (commands == null)
        {
            Status = $"TDP control is not mapped for this processor ({CodeName})";
            return false;
        }

        _commands = commands.Value;
        _mp1 = Mp1Mailbox.For(CodeName);
        if (_mp1.Message == 0)
        {
            Status = $"the MP1 mailbox address is not known for this processor ({CodeName})";
            return false;
        }

        return true;
    }

    private bool TryResolvePmTable()
    {
        if (!TryExecute("ioctl_resolve_pm_table", [], 2, out var outBuf)) return false;
        PmTableVersion = (uint)outBuf[0];
        return true;
    }

    private static string DiagnoseOpenFailure(int hr)
    {
        if (!IsElevated())
            return "DreamTray is not running as administrator, which PawnIO requires";
        return $"PawnIO's driver could not be opened (0x{hr:X8}) — the PawnIO service " +
               "may not be running; try reinstalling it (see native\\README.md)";
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

    // ------------------------------------------------------------------ limits

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

        lock (this)
        {
            bool stapm = SendMp1Command(_commands.Stapm, mw);

            // RyzenAdj's fallback: on some APUs the MP1 mailbox does not carry the
            // STAPM message and the PSMU one does, under a different id.
            if (!stapm && _commands.StapmPsmu is { } psmuId)
                stapm = SendPsmuCommand(psmuId, mw);

            bool slow = SendMp1Command(_commands.Slow, mw);
            bool fast = SendMp1Command(_commands.Fast, fastMw);
            return stapm && slow && fast;
        }
    }

    /// <summary>
    /// One request on the <b>MP1</b> mailbox, driven register by register.
    ///
    /// The module's own <c>ioctl_send_smu_command</c> cannot be used for this: it
    /// hardcodes one mailbox per processor, and for the whole Renoir-and-later APU
    /// line that is the <b>PSMU</b> (<c>0x3B10A20</c>), not MP1. The power-limit
    /// message ids are MP1's, so sending them through that ioctl reaches the wrong
    /// mailbox — where they are valid ids for unrelated commands, so the SMU answers
    /// OK and the limit never moves. That failure is completely silent, which is
    /// exactly what it looked like on a 7840HS.
    ///
    /// So the transaction is done here over the module's raw SMN register ioctls,
    /// against the MP1 addresses for this silicon. The protocol is AMD's and matches
    /// both RyzenAdj's <c>smu_service_req</c> and the module's internal
    /// <c>send_command</c>.
    /// </summary>
    private bool SendMp1Command(uint command, uint arg)
    {
        var mailbox = _mp1;
        if (mailbox.Message == 0) return false;

        // One lock for the whole exchange. Taking it per register would let another
        // SMU tool interleave its own transaction into the middle of ours.
        bool held = TryAcquirePciMutex();
        try
        {
            // 1. Wait for any in-flight command to retire.
            if (!WaitForResponse(mailbox.Response, out _)) return false;

            // 2. Clear the response register.
            if (!WriteRegister(mailbox.Response, 0)) return false;

            // 3. Arguments, then 4. the message id — in that order; writing the id
            //    is what triggers the SMU.
            if (!WriteRegister(mailbox.ArgBase, arg)) return false;
            for (uint i = 1; i < 6; i++)
                if (!WriteRegister(mailbox.ArgBase + 4 * i, 0)) return false;
            if (!WriteRegister(mailbox.Message, command)) return false;

            // 5. Wait for the reply, and 6. insist it is actually OK.
            if (!WaitForResponse(mailbox.Response, out uint response)) return false;
            return response == RepMsgOk;
        }
        finally { if (held) ReleasePciMutex(); }
    }

    /// <summary>One request on the PSMU mailbox, via the module's own ioctl.</summary>
    private bool SendPsmuCommand(uint command, uint arg)
    {
        // in[0] is the command, in[1..6] are the six mailbox argument slots.
        ulong[] input = [command, arg, 0, 0, 0, 0, 0];
        return TryExecute("ioctl_send_smu_command", input, 6, out _);
    }

    /// <summary>Poll a response register until it reports something, or we give up.</summary>
    private bool WaitForResponse(uint address, out uint value)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(SmuTimeoutMs);
        do
        {
            if (!ReadRegister(address, out value)) return false;
            if (value != 0) return true;
        }
        while (DateTime.UtcNow < deadline);

        value = 0;
        return false; // still busy — treat as a failed command rather than hanging
    }

    private bool ReadRegister(uint address, out uint value)
    {
        value = 0;
        if (!TryExecuteLocked("ioctl_read_smu_register", [address], 1, out var output)) return false;
        value = (uint)output[0];
        return true;
    }

    private bool WriteRegister(uint address, uint value) =>
        TryExecuteLocked("ioctl_write_smu_register", [address, value], 0, out _);

    /// <summary>SMU reply codes. Only <see cref="RepMsgOk"/> means the limit moved.</summary>
    private const uint RepMsgOk = 0x1;
    private const int SmuTimeoutMs = 200;

    /// <summary>Live limits from the SMU power table, or null when unavailable.</summary>
    public TdpReadback? Read()
    {
        if (!IsLoaded || !_pmTableResolved) return null;

        lock (this)
        {
            // Pull a fresh copy from the SMU into DRAM, then read it back.
            if (!TryExecute("ioctl_update_pm_table", [], 0, out _)) return null;

            // The six values live at byte offsets 0x00..0x14 as float32, and those
            // offsets are the same across every power-table version — unlike the
            // rest of the table, which moves around. Three qwords covers them.
            if (!TryExecute("ioctl_read_pm_table", [], 3, out var table)) return null;

            return new TdpReadback(
                LowFloat(table[0]), HighFloat(table[0]),   // STAPM limit / value
                LowFloat(table[1]), HighFloat(table[1]),   // fast  limit / value
                LowFloat(table[2]), HighFloat(table[2]));  // slow  limit / value
        }
    }

    private static float LowFloat(ulong qword) => BitConverter.UInt32BitsToSingle((uint)qword);
    private static float HighFloat(ulong qword) => BitConverter.UInt32BitsToSingle((uint)(qword >> 32));

    // ------------------------------------------------------------------ plumbing

    /// <summary>Run one ioctl, taking the cross-process PCI lock around it.</summary>
    private bool TryExecute(string name, ulong[] input, int outCount, out ulong[] output)
    {
        bool held = TryAcquirePciMutex();
        try { return TryExecuteLocked(name, input, outCount, out output); }
        finally { if (held) ReleasePciMutex(); }
    }

    /// <summary>
    /// Run one ioctl without touching the PCI lock — for callers that hold it across
    /// a multi-register sequence, where re-entering per register would let another
    /// process interleave.
    /// </summary>
    private bool TryExecuteLocked(string name, ulong[] input, int outCount, out ulong[] output)
    {
        output = outCount > 0 ? new ulong[outCount] : [];
        if (_execute == null || _handle == nint.Zero) return false;

        try
        {
            int hr = _execute(_handle, name, input, (nuint)input.Length,
                              output, (nuint)outCount, out _);
            return hr >= 0;
        }
        catch { return false; }
    }

    private void TryOpenPciMutex()
    {
        try
        {
            // Created by whichever tool gets there first; opening an existing one is
            // the normal case on a machine that also runs LHM or HWiNFO.
            _pciMutex = new System.Threading.Mutex(false, @"Global\Access_PCI");
        }
        catch
        {
            // An existing mutex with a restrictive ACL, or a sandbox that forbids
            // global names. Serialising within our own process still holds.
            _pciMutex = null;
        }
    }

    private bool TryAcquirePciMutex()
    {
        if (_pciMutex == null) return false;
        try { return _pciMutex.WaitOne(TimeSpan.FromMilliseconds(500)); }
        catch (AbandonedMutexException) { return true; } // previous owner crashed; we own it now
        catch { return false; }
    }

    private void ReleasePciMutex()
    {
        try { _pciMutex?.ReleaseMutex(); } catch { /* not held */ }
    }

    private T Bind<T>(string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, name));

    private void Shutdown()
    {
        if (_handle != nint.Zero) { try { _close?.Invoke(_handle); } catch { } _handle = nint.Zero; }
        _pciMutex?.Dispose();
        _pciMutex = null;
        Unload();
    }

    private void Unload()
    {
        if (_library != nint.Zero) { NativeLibrary.Free(_library); _library = nint.Zero; }
    }

    public void Dispose() => Shutdown();

    // ------------------------------------------------------------------ interop

    // All four return an HRESULT; negative is failure.
    private delegate int OpenDelegate(out nint handle);
    private delegate int LoadDelegate(nint handle, byte[] blob, nuint size);
    private delegate int CloseDelegate(nint handle);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private delegate int ExecuteDelegate(
        nint handle,
        [MarshalAs(UnmanagedType.LPStr)] string name,
        ulong[] input, nuint inputCount,
        ulong[] output, nuint outputCount,
        out nuint returnCount);
}

/// <summary>
/// SMU mailbox command ids for the three power limits. They are per-generation, and
/// the mapping is RyzenAdj's (<c>lib/api.c</c>) — the reference implementation for
/// this hardware.
/// </summary>
internal readonly struct SmuCommands(uint stapm, uint fast, uint slow, uint? stapmPsmu = null)
{
    public uint Stapm { get; } = stapm;
    public uint Fast { get; } = fast;
    public uint Slow { get; } = slow;

    /// <summary>
    /// STAPM's id on the PSMU mailbox, where one exists. Tried only when the MP1
    /// message is rejected — RyzenAdj carries the same fallback, and only for STAPM;
    /// there is no known PSMU equivalent for the fast and slow limits.
    /// </summary>
    public uint? StapmPsmu { get; } = stapmPsmu;

    public static SmuCommands? For(AmdCodeName codeName) => codeName switch
    {
        // Raven-era APUs.
        AmdCodeName.RavenRidge or AmdCodeName.RavenRidge2 or
        AmdCodeName.Picasso or AmdCodeName.Dali
            => new SmuCommands(0x1a, 0x1b, 0x1c),

        // Renoir onwards — the long-lived mapping, and what a 7840HS (Phoenix) uses.
        AmdCodeName.Renoir or AmdCodeName.Lucienne or AmdCodeName.Cezanne or
        AmdCodeName.Vangogh or AmdCodeName.Rembrandt or AmdCodeName.Mendocino or
        AmdCodeName.Phoenix or AmdCodeName.Phoenix2 or AmdCodeName.HawkPoint or
        AmdCodeName.StrixPoint or AmdCodeName.StrixHalo or
        AmdCodeName.KrackanPoint or AmdCodeName.KrackanPoint2
            => new SmuCommands(0x14, 0x15, 0x16, stapmPsmu: 0x31),

        // Dragon Range is a desktop die in a laptop socket and answers differently.
        AmdCodeName.DragonRange
            => new SmuCommands(0x4f, 0x3e, 0x5f),

        _ => null,
    };
}

/// <summary>
/// SMN addresses of the <b>MP1</b> mailbox's message, response and argument-base
/// registers. Per silicon generation, mirroring RyzenAdj's <c>get_smu</c> in
/// <c>lib/nb_smu_ops.c</c>.
///
/// These are needed because the PawnIO module's own send-command ioctl is wired to
/// one mailbox per processor, and on every APU from Renoir on that is the PSMU —
/// the wrong one for the power limits. See <see cref="PawnIoSmu.SendMp1Command"/>.
/// </summary>
internal readonly struct Mp1Mailbox(uint message, uint response, uint argBase)
{
    public uint Message { get; } = message;
    public uint Response { get; } = response;
    public uint ArgBase { get; } = argBase;

    public static Mp1Mailbox For(AmdCodeName codeName) => codeName switch
    {
        AmdCodeName.Rembrandt or AmdCodeName.Vangogh or AmdCodeName.Mendocino or
        AmdCodeName.Phoenix or AmdCodeName.Phoenix2 or AmdCodeName.HawkPoint
            => new Mp1Mailbox(0x3B10528, 0x3B10578, 0x3B10998),

        AmdCodeName.KrackanPoint or AmdCodeName.KrackanPoint2 or
        AmdCodeName.StrixPoint or AmdCodeName.StrixHalo
            => new Mp1Mailbox(0x3B10928, 0x3B10978, 0x3B10998),

        AmdCodeName.DragonRange
            => new Mp1Mailbox(0x3B10530, 0x3B1057C, 0x3B109C4),

        // Everything older: Raven/Picasso/Dali, Renoir, Lucienne, Cezanne.
        AmdCodeName.RavenRidge or AmdCodeName.RavenRidge2 or AmdCodeName.Picasso or
        AmdCodeName.Dali or AmdCodeName.Renoir or AmdCodeName.Lucienne or
        AmdCodeName.Cezanne
            => new Mp1Mailbox(0x3B10528, 0x3B10564, 0x3B10998),

        _ => default, // Message == 0 marks "unknown"
    };
}

/// <summary>
/// Processor codenames as reported by the RyzenSMU PawnIO module's
/// <c>ioctl_get_code_name</c>. The numbering is the module's, not AMD's.
/// </summary>
internal enum AmdCodeName
{
    Undefined = -1,
    Colfax = 0, Renoir = 1, Picasso = 2, Matisse = 3, Threadripper = 4,
    CastlePeak = 5, RavenRidge = 6, RavenRidge2 = 7, SummitRidge = 8,
    PinnacleRidge = 9, Rembrandt = 10, Vermeer = 11, Vangogh = 12, Cezanne = 13,
    Milan = 14, Dali = 15, Raphael = 16, GraniteRidge = 17, Naples = 18,
    FireFlight = 19, Rome = 20, Chagall = 21, Lucienne = 22, Phoenix = 23,
    Phoenix2 = 24, Mendocino = 25, Genoa = 26, StormPeak = 27, DragonRange = 28,
    Mero = 29, HawkPoint = 30, StrixPoint = 31, StrixHalo = 32,
    KrackanPoint = 33, KrackanPoint2 = 34, Turin = 35, TurinD = 36,
    Bergamo = 37, ShimadaPeak = 38,
}
