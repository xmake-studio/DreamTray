using System.Runtime.InteropServices;

namespace DreamTray.Sensors;

// ---------------------------------------------------------------------------
// Low-level counters that LibreHardwareMonitor either gets wrong on some hardware
// or does not expose at all. These use documented Windows APIs rather than
// model-specific knowledge, so they answer on any supported machine.
//
// All PDH counters are added with PdhAddEnglishCounter so they work on a
// non-English Windows UI.
// ---------------------------------------------------------------------------

/// <summary>
/// Per-logical-processor busy fraction via NtQuerySystemInformation
/// (SystemProcessorPerformanceInformation). No PDH, no localized counter names.
/// </summary>
public sealed class CpuLoadReader
{
    [StructLayout(LayoutKind.Sequential)]
    private struct PerfInfo
    {
        public long IdleTime;     // 100ns
        public long KernelTime;   // includes IdleTime
        public long UserTime;
        public long Reserved0;
        public long Reserved1;
        public uint Reserved2;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(
        int infoClass, byte[] buffer, int bufferLen, out int returnLen);

    private const int SystemProcessorPerformanceInformation = 8;

    private readonly int _cpuCount = Environment.ProcessorCount;
    private readonly long[] _prevIdle;
    private readonly long[] _prevTotal;
    private readonly int _stride;
    private readonly byte[] _buffer;
    private bool _primed;

    public int CoreCount => _cpuCount;

    public CpuLoadReader()
    {
        _prevIdle = new long[_cpuCount];
        _prevTotal = new long[_cpuCount];
        _stride = Marshal.SizeOf<PerfInfo>();
        _buffer = new byte[_stride * _cpuCount];
    }

    /// <summary>Fill <paramref name="outLoads"/> with per-thread load 0..1 and return the mean.</summary>
    public float Read(float[] outLoads)
    {
        int status = NtQuerySystemInformation(
            SystemProcessorPerformanceInformation, _buffer, _buffer.Length, out _);
        if (status != 0) return 0f; // leave zeros on failure

        var handle = GCHandle.Alloc(_buffer, GCHandleType.Pinned);
        try
        {
            nint basePtr = handle.AddrOfPinnedObject();
            int n = Math.Min(_cpuCount, outLoads.Length);
            float sum = 0;
            for (int i = 0; i < _cpuCount; i++)
            {
                var pi = Marshal.PtrToStructure<PerfInfo>(basePtr + i * _stride);
                long idle = pi.IdleTime;
                long total = pi.KernelTime + pi.UserTime; // Kernel already includes idle
                long dIdle = idle - _prevIdle[i];
                long dTotal = total - _prevTotal[i];
                _prevIdle[i] = idle;
                _prevTotal[i] = total;

                if (_primed && i < n)
                {
                    float load = dTotal > 0 ? 1f - (float)dIdle / dTotal : 0f;
                    load = Math.Clamp(load, 0f, 1f);
                    outLoads[i] = load;
                    sum += load;
                }
            }
            _primed = true;
            return n > 0 ? sum / n : 0f;
        }
        finally { handle.Free(); }
    }
}

/// <summary>
/// Live per-core CPU frequency. Effective clock = base MHz × "% Processor
/// Performance" / 100 — this PDH counter reflects boost (values &gt;100%) and is
/// what Task Manager shows. Base MHz comes from CallNtPowerInformation.
/// LibreHardwareMonitor's per-core Clock sensors read NaN on several AMD parts.
/// </summary>
public sealed class CpuFreqReader : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessorPowerInformation
    {
        public uint Number, MaxMhz, CurrentMhz, MhzLimit, MaxIdleState, CurrentIdleState;
    }
    [DllImport("powrprof.dll")]
    private static extern uint CallNtPowerInformation(
        int level, nint input, uint inputSize, byte[] output, uint outputSize);

    private readonly float _baseGhz;
    private readonly PdhArrayCounter _counter;

    public CpuFreqReader()
    {
        _baseGhz = ReadBaseMhz() / 1000f;
        _counter = new PdhArrayCounter(@"\Processor Information(*)\% Processor Performance");
    }

    private static uint ReadBaseMhz()
    {
        int n = Environment.ProcessorCount;
        int stride = Marshal.SizeOf<ProcessorPowerInformation>();
        var buf = new byte[stride * n];
        if (CallNtPowerInformation(11, nint.Zero, 0, buf, (uint)buf.Length) != 0) return 0;
        var h = GCHandle.Alloc(buf, GCHandleType.Pinned);
        try
        {
            uint mx = 0;
            for (int i = 0; i < n; i++)
            {
                var p = Marshal.PtrToStructure<ProcessorPowerInformation>(
                    h.AddrOfPinnedObject() + i * stride);
                if (p.MaxMhz > mx) mx = p.MaxMhz;
            }
            return mx;
        }
        finally { h.Free(); }
    }

    /// <summary>Average and max effective core frequency, in GHz.</summary>
    public void Read(out float avgGhz, out float maxGhz)
    {
        avgGhz = _baseGhz; maxGhz = _baseGhz;
        double sum = 0, mx = 0; int cores = 0;
        foreach (var (name, value) in _counter.Read())
        {
            if (name.Contains("_Total", StringComparison.OrdinalIgnoreCase)) continue;
            double ghz = _baseGhz * value / 100.0;
            sum += ghz; if (ghz > mx) mx = ghz; cores++;
        }
        if (cores > 0) { avgGhz = (float)(sum / cores); maxGhz = (float)mx; }
    }

    public void Dispose() => _counter.Dispose();
}

/// <summary>
/// Per-physical-drive "active time" (what Task Manager's Disk column shows),
/// derived from \PhysicalDisk(*)\% Idle Time as 100 − idle. PDH names the instances
/// "&lt;index&gt; &lt;drive letters&gt;" (e.g. "0 C:", "1 D: E:"), which is how the system
/// drive is identified.
/// </summary>
public sealed class DiskLoadReader : IDisposable
{
    private readonly PdhArrayCounter _counter = new(@"\PhysicalDisk(*)\% Idle Time");

    /// <summary>
    /// Active time of the system (C:) drive and of the next physical drive, 0..1.
    /// <paramref name="other"/> is -1 when the machine has only one physical drive.
    /// </summary>
    public void Read(out float system, out float other, out string otherLabel)
    {
        system = 0f; other = -1f; otherLabel = "";

        float sys = -1f, rest = -1f;
        string restLabel = "";
        foreach (var (name, idle) in _counter.Read())
        {
            if (name.Contains("_Total", StringComparison.OrdinalIgnoreCase)) continue;
            float active = (float)Math.Clamp((100.0 - idle) / 100.0, 0.0, 1.0);

            if (sys < 0 && name.Contains("C:", StringComparison.OrdinalIgnoreCase)) sys = active;
            else if (rest < 0) { rest = active; restLabel = FirstLetter(name); }
        }
        // No instance carried "C:" (unusual): promote the other drive to the left-hand
        // slot so the system readout isn't blank.
        if (sys < 0) { sys = MathF.Max(0, rest); rest = -1f; restLabel = ""; }
        system = sys;
        other = rest;
        otherLabel = restLabel;
    }

    /// <summary>
    /// First drive letter of a PhysicalDisk instance name ("0 D: G:" → "D:").
    /// A disk with no mounted volume has no letters; label it by its index.
    /// </summary>
    private static string FirstLetter(string instance)
    {
        int colon = instance.IndexOf(':');
        if (colon > 0) return instance.Substring(colon - 1, 2).ToUpperInvariant();
        return instance.Split(' ')[0] + ":";
    }

    public void Dispose() => _counter.Dispose();
}

/// <summary>
/// Pagefile bytes actually in use — what a user means by "swap", and what Task
/// Manager reports. LibreHardwareMonitor only exposes commit charge; commit counts
/// reserved-but-never-touched pages, so estimating swap as "commit beyond physical"
/// overstates it several-fold on a machine with RAM to spare (23 GB against a real
/// 2.4 GB on the development box).
///
/// \Paging File(_Total)\% Usage is the size-weighted percentage across every
/// pagefile. The size it is a percentage *of* is the commit limit minus physical
/// RAM, since Windows sets the limit to RAM + pagefiles; deriving it that way
/// rather than from a one-shot WMI query keeps it right when Windows grows a
/// system-managed pagefile at runtime.
/// </summary>
public sealed class PagefileReader : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct PerformanceInformation
    {
        public int cb;
        public nint CommitTotal, CommitLimit, CommitPeak;
        public nint PhysicalTotal, PhysicalAvailable, SystemCache, KernelTotal, KernelPaged, KernelNonpaged;
        public nint PageSize;
        public int HandleCount, ProcessCount, ThreadCount;
    }

    [DllImport("psapi.dll")]
    private static extern bool GetPerformanceInfo(out PerformanceInformation pi, int size);

    private const double Gib = 1024 * 1024 * 1024;

    private readonly PdhArrayCounter _counter = new(@"\Paging File(*)\% Usage");

    /// <summary>
    /// Pagefile in use, in GiB. Zero when the machine has no pagefile — which is a
    /// true reading, not a failure.
    /// </summary>
    public float Read()
    {
        double total = -1, single = -1; int instances = 0;
        foreach (var (name, value) in _counter.Read())
        {
            if (name.Contains("_Total", StringComparison.OrdinalIgnoreCase)) total = value;
            else { single = value; instances++; }
        }
        // With one pagefile its own percentage is the aggregate, so accept it if the
        // system did not synthesise a _Total instance.
        if (total < 0 && instances == 1) total = single;
        if (total < 0) return 0f;

        if (!GetPerformanceInfo(out var pi, Marshal.SizeOf<PerformanceInformation>())) return 0f;
        double sizeGib = (double)(pi.CommitLimit - pi.PhysicalTotal) * pi.PageSize / Gib;
        if (sizeGib <= 0) return 0f;

        return (float)Math.Max(0, total / 100.0 * sizeGib);
    }

    public void Dispose() => _counter.Dispose();
}

/// <summary>
/// Thin wrapper over a wildcard PDH counter. Owns the query handle and the unmanaged
/// result buffer, which it reuses between reads so sampling allocates nothing.
/// </summary>
internal sealed class PdhArrayCounter : IDisposable
{
    [DllImport("pdh.dll")]
    private static extern uint PdhOpenQuery(string? dataSource, nint userData, out nint query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounter(nint query, string path, nint userData, out nint counter);
    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(nint query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArray(
        nint counter, uint format, ref uint bufferSize, out uint itemCount, nint buffer);
    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(nint query);

    private const uint PDH_FMT_DOUBLE = 0x00000200;
    private const uint PDH_MORE_DATA = 0x800007D2;
    // PDH_FMT_COUNTERVALUE_ITEM = szName ptr + { CStatus(4) + pad(4) + double(8) }.
    private static readonly int ItemSize = nint.Size + 16;

    private nint _query, _counter, _buffer;
    private uint _bufferSize;
    private readonly bool _ok;

    public PdhArrayCounter(string path)
    {
        if (PdhOpenQuery(null, nint.Zero, out _query) == 0 &&
            PdhAddEnglishCounter(_query, path, nint.Zero, out _counter) == 0)
        {
            PdhCollectQueryData(_query); // prime: a rate counter needs two samples
            _ok = true;
        }
    }

    /// <summary>Collect one sample. Yields (instanceName, value) for each instance.</summary>
    public IEnumerable<(string Name, double Value)> Read()
    {
        if (!_ok || PdhCollectQueryData(_query) != 0) yield break;

        uint size = _bufferSize;
        uint status = PdhGetFormattedCounterArray(_counter, PDH_FMT_DOUBLE, ref size, out uint count, _buffer);
        if (status == PDH_MORE_DATA || _buffer == nint.Zero)
        {
            // Grow (or first-time allocate) the buffer, then re-collect into it.
            if (_buffer != nint.Zero) Marshal.FreeHGlobal(_buffer);
            _bufferSize = size;
            _buffer = Marshal.AllocHGlobal((int)size);
            size = _bufferSize;
            status = PdhGetFormattedCounterArray(_counter, PDH_FMT_DOUBLE, ref size, out count, _buffer);
        }
        if (status != 0) yield break;

        for (int i = 0; i < count; i++)
        {
            nint item = _buffer + i * ItemSize;
            string? name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item));
            if (name == null) continue;
            double value = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item + nint.Size + 8));
            yield return (name, value);
        }
    }

    public void Dispose()
    {
        if (_query != nint.Zero) { PdhCloseQuery(_query); _query = nint.Zero; }
        if (_buffer != nint.Zero) { Marshal.FreeHGlobal(_buffer); _buffer = nint.Zero; }
    }
}
