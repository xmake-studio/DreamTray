using System.Runtime.InteropServices;

namespace DreamTray.Sensors;

/// <summary>
/// The NVIDIA GPU values DreamTray shows, read straight from the driver's own
/// libraries instead of through LibreHardwareMonitor.
///
/// LHM's NVIDIA node refreshes everything it knows about the card on every update:
/// power topology, voltage rails, fan controllers, the D3D engine statistics, an
/// I2C probe for 12VHPWR pin sensors, and PCIe throughput — two NVML calls that each
/// block for the 20 ms window they measure over. On an RTX 3060 that is ~80 ms and
/// ~80 million cycles per tick, nineteen-twentieths of everything the sampler does,
/// once a second for as long as a plugin is streaming. The power-topology query
/// alone is 51 million of those cycles, for a percentage nothing here displays.
///
/// What the panel and the plugins use is five numbers and a temperature. NVML
/// answers the five in microseconds; the hot-spot temperature, which LHM reports
/// and <see cref="SensorService"/> prefers, comes from the one NVAPI call LHM gets
/// it from. Measured on the same card: ~0.2 million cycles a tick for the NVML
/// part, ~1.3 for the hot spot, and values identical to LHM's.
///
/// Only for a machine with exactly one NVIDIA GPU. With more, which NVML device is
/// which NVAPI handle takes a bus-id match this does not attempt, and LHM's node is
/// used as before. A failed read after opening — a driver update or a TDR
/// invalidating the handles — is reported to the caller, which hands the GPU back
/// to LHM for the life of the service.
/// </summary>
public sealed class NvidiaReader : IDisposable
{
    public readonly record struct Reading(
        float Load, float Temperature, float PowerW, float ClockMhz, float VramUsedGb, float VramTotalGb);

    private readonly nint _device;
    private readonly NvmlShutdown _shutdown;
    private readonly NvmlGetUtilization _getUtilization;
    private readonly NvmlGetTemperature _getTemperature;
    private readonly NvmlGetPower _getPower;
    private readonly NvmlGetClock _getClock;
    private readonly NvmlGetMemory _getMemory;

    // Hot spot, through NVAPI. Absent when NVAPI is, or when it sees a different
    // number of GPUs than NVML does — then the core temperature is reported instead.
    private readonly NvApiGetThermalSensors? _getThermalSensors;
    private readonly nint _nvapiGpu;
    private readonly uint _thermalMask;
    // Unmanaged, allocated once: the sensor struct carries two inline arrays, and
    // marshalling those would allocate both on every read.
    private nint _thermalBuffer;

    private bool _disposed;

    private NvidiaReader(nint nvml, nint device)
    {
        _device = device;
        _shutdown = Export<NvmlShutdown>(nvml, "nvmlShutdown");
        _getUtilization = Export<NvmlGetUtilization>(nvml, "nvmlDeviceGetUtilizationRates");
        _getTemperature = Export<NvmlGetTemperature>(nvml, "nvmlDeviceGetTemperature");
        _getPower = Export<NvmlGetPower>(nvml, "nvmlDeviceGetPowerUsage");
        _getClock = Export<NvmlGetClock>(nvml, "nvmlDeviceGetClockInfo");
        _getMemory = Export<NvmlGetMemory>(nvml, "nvmlDeviceGetMemoryInfo");

        if (TryOpenHotSpot(out var getThermal, out _nvapiGpu, out _thermalMask, out _thermalBuffer))
            _getThermalSensors = getThermal;
    }

    /// <summary>
    /// A reader for this machine's NVIDIA GPU, or null when there is not exactly one,
    /// or the driver's management library is not there to ask.
    /// </summary>
    public static NvidiaReader? TryOpen()
    {
        try
        {
            nint nvml = LoadNvml();
            if (nvml == nint.Zero) return null;
            if (Export<NvmlInit>(nvml, "nvmlInit_v2")() != NVML_SUCCESS) return null;

            if (Export<NvmlGetCount>(nvml, "nvmlDeviceGetCount_v2")(out uint count) != NVML_SUCCESS || count != 1
                || Export<NvmlGetHandle>(nvml, "nvmlDeviceGetHandleByIndex_v2")(0, out nint device) != NVML_SUCCESS)
            {
                Export<NvmlShutdown>(nvml, "nvmlShutdown")();
                return null;
            }
            return new NvidiaReader(nvml, device);
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException or BadImageFormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// One reading, or false when the GPU stopped answering. The load is the primary
    /// value and the one whose failure counts; the others read as zero where a card
    /// does not support them, as they would through LHM.
    /// </summary>
    public bool TryRead(out Reading reading)
    {
        reading = default;
        if (_disposed || _getUtilization(_device, out var utilization) != NVML_SUCCESS) return false;

        float temperature = _getTemperature(_device, NVML_TEMPERATURE_GPU, out uint celsius) == NVML_SUCCESS
            ? celsius : 0f;
        float hotSpot = ReadHotSpot();
        if (hotSpot > 0) temperature = hotSpot;

        float powerW = _getPower(_device, out uint milliwatts) == NVML_SUCCESS ? milliwatts / 1000f : 0f;
        float clock = _getClock(_device, NVML_CLOCK_GRAPHICS, out uint mhz) == NVML_SUCCESS ? mhz : 0f;

        float used = 0, total = 0;
        if (_getMemory(_device, out var memory) == NVML_SUCCESS)
        {
            used = (float)(memory.Used / Gib);
            total = (float)(memory.Total / Gib);
        }

        reading = new Reading(utilization.Gpu / 100f, temperature, powerW, clock, used, total);
        return true;
    }

    /// <summary>
    /// The hot-spot sensor, in °C, or 0 when there is none. Index 1 of NVAPI's
    /// thermal-sensor array in Q8 fixed point — the slot LHM reads as "GPU Hot Spot".
    /// </summary>
    private float ReadHotSpot()
    {
        if (_getThermalSensors is null || _thermalBuffer == nint.Zero || (_thermalMask & 0b10) == 0) return 0;
        PrepareThermalBuffer(_thermalBuffer, _thermalMask);
        if (_getThermalSensors(_nvapiGpu, _thermalBuffer) != NVAPI_OK) return 0;
        return Marshal.ReadInt32(_thermalBuffer, ThermalTemperaturesOffset + 4) / 256f;
    }

    private static bool TryOpenHotSpot(
        out NvApiGetThermalSensors? getThermal, out nint gpu, out uint mask, out nint buffer)
    {
        getThermal = null;
        gpu = nint.Zero;
        mask = 0;
        buffer = nint.Zero;
        if (!NativeLibrary.TryLoad("nvapi64.dll", typeof(NvidiaReader).Assembly,
                DllImportSearchPath.System32, out nint nvapi))
            return false;
        if (!NativeLibrary.TryGetExport(nvapi, "nvapi_QueryInterface", out nint queryPtr)) return false;
        var query = Marshal.GetDelegateForFunctionPointer<NvApiQueryInterface>(queryPtr);

        nint initPtr = query(NvAPI_Initialize);
        nint enumPtr = query(NvAPI_EnumPhysicalGPUs);
        nint thermalPtr = query(NvAPI_GPU_GetThermalSensors);
        if (initPtr == nint.Zero || enumPtr == nint.Zero || thermalPtr == nint.Zero) return false;
        if (Marshal.GetDelegateForFunctionPointer<NvApiInitialize>(initPtr)() != NVAPI_OK) return false;

        var handles = new nint[NVAPI_MAX_PHYSICAL_GPUS];
        if (Marshal.GetDelegateForFunctionPointer<NvApiEnumPhysicalGpus>(enumPtr)(handles, out int count) != NVAPI_OK
            || count != 1)
            return false;

        // The widest mask the driver accepts, found the way LHM finds it: grow it a
        // bit at a time until the call is refused.
        var thermal = Marshal.GetDelegateForFunctionPointer<NvApiGetThermalSensors>(thermalPtr);
        buffer = Marshal.AllocHGlobal(ThermalSize);
        uint widest = 0;
        for (int bit = 0; bit < 32; bit++)
        {
            PrepareThermalBuffer(buffer, 1u << bit);
            if (thermal(handles[0], buffer) != NVAPI_OK) break;
            widest = 1u << bit;
        }
        if (widest == 0)
        {
            Marshal.FreeHGlobal(buffer);
            buffer = nint.Zero;
            return false;
        }

        getThermal = thermal;
        gpu = handles[0];
        mask = (widest << 1) - 1;
        return true;
    }

    private static void PrepareThermalBuffer(nint buffer, uint mask)
    {
        for (int offset = 0; offset < ThermalSize; offset += 4) Marshal.WriteInt32(buffer, offset, 0);
        Marshal.WriteInt32(buffer, 0, ThermalSize | (2 << 16)); // struct size | version 2
        Marshal.WriteInt32(buffer, 4, (int)mask);
    }

    private static nint LoadNvml()
    {
        // Ships in System32 with every DCH driver; older installers put it under
        // NVSMI instead, which is where LHM looks as well.
        if (NativeLibrary.TryLoad("nvml.dll", typeof(NvidiaReader).Assembly, DllImportSearchPath.System32, out nint nvml))
            return nvml;
        string legacy = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvml.dll");
        return File.Exists(legacy) && NativeLibrary.TryLoad(legacy, out nvml) ? nvml : nint.Zero;
    }

    private static T Export<T>(nint library, string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // NVML counts its inits; this balances ours and leaves LHM's alone. The
        // libraries stay loaded — LHM holds them too.
        try { _shutdown(); } catch { /* the driver may already be gone */ }
        if (_thermalBuffer != nint.Zero)
        {
            Marshal.FreeHGlobal(_thermalBuffer);
            _thermalBuffer = nint.Zero;
        }
    }

    // ---------------------------------------------------------------- interop

    private const int NVML_SUCCESS = 0;
    private const int NVML_TEMPERATURE_GPU = 0;
    private const int NVML_CLOCK_GRAPHICS = 0;
    private const double Gib = 1024.0 * 1024 * 1024;

    private const int NVAPI_OK = 0;
    private const int NVAPI_MAX_PHYSICAL_GPUS = 64;
    private const uint NvAPI_Initialize = 0x0150E828;
    private const uint NvAPI_EnumPhysicalGPUs = 0xE5AC921F;
    private const uint NvAPI_GPU_GetThermalSensors = 0x65FE3AAD;

    // NV_GPU_THERMAL_EX v2: version, mask, 8 reserved words, 32 temperatures.
    private const int ThermalTemperaturesOffset = 4 + 4 + 8 * 4;
    private const int ThermalSize = ThermalTemperaturesOffset + 32 * 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlUtilization { public uint Gpu, Memory; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlMemory { public ulong Total, Free, Used; }

    private delegate int NvmlInit();
    private delegate int NvmlShutdown();
    private delegate int NvmlGetCount(out uint count);
    private delegate int NvmlGetHandle(uint index, out nint device);
    private delegate int NvmlGetUtilization(nint device, out NvmlUtilization utilization);
    private delegate int NvmlGetTemperature(nint device, int sensor, out uint celsius);
    private delegate int NvmlGetPower(nint device, out uint milliwatts);
    private delegate int NvmlGetClock(nint device, int type, out uint mhz);
    private delegate int NvmlGetMemory(nint device, out NvmlMemory memory);

    private delegate nint NvApiQueryInterface(uint id);
    private delegate int NvApiInitialize();
    private delegate int NvApiEnumPhysicalGpus([Out] nint[] handles, out int count);
    private delegate int NvApiGetThermalSensors(nint gpu, nint sensors);
}
