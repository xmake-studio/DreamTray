using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DreamTray;

/// <summary>
/// Keeps the process in RAM while it waits for a click.
///
/// A tray app spends hours with no visible window, and the memory manager ages the
/// pages of a process in that state out of its working set. The pages the panel
/// needs go first — nothing touches them while it is closed, while the plugins and
/// the sampler touch theirs every second — and after a long enough idle the next
/// open reads them back from disk. The open logs show exactly that shape: every
/// phase slow at once after an idle stretch, every phase fast on a reopen a few
/// seconds later.
///
/// What this deliberately does not do is opt out of power throttling. A window-less
/// process gets low quality of service — efficiency cores, low clocks — and opting
/// out would make the open a fraction of a millisecond faster at the price of every
/// background sensor poll and plugin frame running on a performance core at full
/// clock. Measured with throttling forced on, the open is still a few milliseconds;
/// the background work is what the user pays for all day.
/// </summary>
public static class ProcessTuning
{
    /// <summary>
    /// Always room for this much above what the process holds when the minimum is
    /// set, so ordinary growth — a widget added, a sensor list filled — stays under
    /// the floor rather than becoming the first thing trimmed. Headroom costs nothing
    /// until it is used: the floor keeps resident only pages the process has.
    /// </summary>
    private const long HeadroomBytes = 64L * 1024 * 1024;
    private const long FloorBytes = 128L * 1024 * 1024;
    /// <summary>
    /// The most this will ever pin. Far above anything the app needs, and low enough
    /// that a runaway plugin cannot turn the floor into a reason the machine pages.
    /// </summary>
    private const long CeilingBytes = 512L * 1024 * 1024;

    private static readonly object Gate = new();
    private static bool _failed;

    /// <summary>
    /// Keep everything the process currently holds in RAM, with room to grow.
    ///
    /// A hard minimum working set: the memory manager will not trim the process below
    /// it, however long it sits idle. It costs RAM and nothing else — no CPU, no
    /// wakeups — and only the RAM the process already has in use.
    ///
    /// Only ever raised, and safe to call as often as convenient: when there is
    /// nothing to do it is two counter reads. The floor is read back from the system
    /// rather than remembered, because it does not only change here: a "memory
    /// cleaner" emptying the working set resets it, and a floor remembered as set
    /// would never be put back.
    /// </summary>
    public static void KeepResident(Action<string>? log = null)
    {
        lock (Gate)
        {
            if (_failed) return;
            try
            {
                nint process = GetCurrentProcess();
                long current = Environment.WorkingSet;
                long wanted = Math.Clamp(current + HeadroomBytes, FloorBytes, CeilingBytes);
                // Left alone while it still has half its headroom (or is at the
                // ceiling): raising it a few megabytes at every close is churn, and a
                // log line each time.
                if (GetProcessWorkingSetSizeEx(process, out nint minimum, out _, out uint flags)
                    && (flags & QUOTA_LIMITS_HARDWS_MIN_ENABLE) != 0
                    && (minimum >= wanted || minimum >= current + HeadroomBytes / 2))
                    return;

                EnablePrivilege(SE_INC_WORKING_SET_NAME);
                // The maximum is only a hint once it is disabled as a hard limit, but it
                // still has to be at least the minimum for the call to be accepted.
                long maximum = CeilingBytes * 4;
                if (!SetProcessWorkingSetSizeEx(process, (nint)wanted, (nint)maximum,
                        QUOTA_LIMITS_HARDWS_MIN_ENABLE | QUOTA_LIMITS_HARDWS_MAX_DISABLE))
                {
                    _failed = true;
                    log?.Invoke($"resident floor refused: {new Win32Exception().Message}");
                    return;
                }
                log?.Invoke($"resident floor {wanted / (1024 * 1024)} MB " +
                            $"(working set {current / (1024 * 1024)} MB)");
            }
            catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
            {
                _failed = true;
            }
        }
    }

    /// <summary>
    /// "Increase a process working set" is granted to every user but present in the
    /// token disabled, and raising the minimum checks it is enabled.
    /// </summary>
    private static void EnablePrivilege(string name)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out nint token))
            return;
        try
        {
            if (!LookupPrivilegeValue(null, name, out long luid)) return;
            var privileges = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
            AdjustTokenPrivileges(token, false, ref privileges, 0, nint.Zero, nint.Zero);
        }
        finally { CloseHandle(token); }
    }

    // ---------------------------------------------------------------- interop

    private const uint QUOTA_LIMITS_HARDWS_MIN_ENABLE = 0x1;
    private const uint QUOTA_LIMITS_HARDWS_MAX_DISABLE = 0x8;

    private const string SE_INC_WORKING_SET_NAME = "SeIncreaseWorkingSetPrivilege";
    private const uint TOKEN_ADJUST_PRIVILEGES = 0x20;
    private const uint TOKEN_QUERY = 0x8;
    private const uint SE_PRIVILEGE_ENABLED = 0x2;

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public long Luid;
        public uint Attributes;
    }

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessWorkingSetSizeEx(
        nint process, out nint minimum, out nint maximum, out uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSizeEx(nint process, nint minimum, nint maximum, uint flags);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(nint process, uint access, out nint token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out long luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(
        nint token, bool disableAll, ref TOKEN_PRIVILEGES state, int length, nint previous, nint returnLength);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);
}
