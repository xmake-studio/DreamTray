using System.Runtime.InteropServices;

namespace DreamTray.Display;

/// <summary>
/// Resolution and refresh-rate control via the classic GDI display APIs
/// (<c>EnumDisplaySettingsEx</c> / <c>ChangeDisplaySettingsEx</c>). No elevation is
/// needed and a change applies per-monitor without disturbing the others.
///
/// Enumeration is *not* cheap, which is why nothing here reads the OS on the
/// calling thread. <c>QueryDisplayConfig</c> and every <c>EnumDisplaySettings</c>
/// call go down to the display driver, and a full scan is one CCD query plus two
/// <c>DisplayConfigGetDeviceInfo</c> calls per path plus one
/// <c>EnumDisplaySettings</c> per *mode* — several hundred driver round trips on a
/// two-monitor machine. That is normally tens of milliseconds and occasionally
/// hundreds when the GPU driver is busy, which is exactly the wrong thing to do
/// between a tray click and the panel appearing.
///
/// So the readers below serve a snapshot that a background scan publishes, and the
/// snapshot is refreshed when the display configuration actually changes (see
/// <see cref="AppServices"/>) and behind the panel each time it opens.
/// </summary>
public sealed class DisplayModeService
{
    private readonly Action<string> _log;

    public DisplayModeService(Action<string> log) => _log = log;

    // ---------------------------------------------------------------- snapshot

    /// <summary>What one device offers. Built once per scan, then never mutated.</summary>
    private sealed record DeviceModes(IReadOnlyList<DisplayMode> Modes, DisplayMode? Current)
    {
        public static readonly DeviceModes None = new([], null);
    }

    private sealed record Snapshot(
        IReadOnlyList<DisplayDevice> Devices,
        IReadOnlyDictionary<string, DeviceModes> ByDevice)
    {
        public static readonly Snapshot Empty =
            new([], new Dictionary<string, DeviceModes>(StringComparer.OrdinalIgnoreCase));

        public DeviceModes For(string deviceName) =>
            ByDevice.TryGetValue(deviceName, out var modes) ? modes : DeviceModes.None;
    }

    /// <summary>
    /// Replaced wholesale, never mutated, so readers — the UI thread among them —
    /// can take it without a lock and without ever waiting on a scan in flight.
    /// </summary>
    private volatile Snapshot _snapshot = Snapshot.Empty;

    /// <summary>True once a scan has completed, so a reader knows the list is real.</summary>
    private volatile bool _scanned;

    private readonly object _requestGate = new();
    private readonly List<Action> _waiting = [];
    private bool _scanRunning;
    private bool _rescanWanted;

    /// <summary>Start the first scan at app start, off the UI thread.</summary>
    public void WarmUp() => RefreshAsync();

    /// <summary>
    /// Re-scan off the calling thread and call <paramref name="onCompleted"/> (on a
    /// pool thread) once a snapshot taken *after* this call was published.
    ///
    /// A request that arrives while a scan is running does not collapse into it: the
    /// scan in flight may have read the hardware before whatever prompted the new
    /// request — a mode the user just applied, say — so another pass is queued and
    /// the callback waits for that one.
    /// </summary>
    public void RefreshAsync(Action? onCompleted = null)
    {
        lock (_requestGate)
        {
            if (onCompleted != null) _waiting.Add(onCompleted);
            if (_scanRunning)
            {
                _rescanWanted = true;
                return;
            }
            _scanRunning = true;
        }
        ThreadPool.QueueUserWorkItem(_ => ScanLoop());
    }

    private void ScanLoop()
    {
        while (true)
        {
            // Taken before the scan, so a callback only ever fires on a snapshot that
            // was read after its own request came in.
            Action[] batch;
            lock (_requestGate)
            {
                batch = [.. _waiting];
                _waiting.Clear();
            }

            try { Refresh(); }
            catch (Exception ex) { _log($"display mode re-scan failed: {ex.Message}"); }

            foreach (var callback in batch)
            {
                try { callback(); }
                catch (Exception ex) { _log($"display mode refresh callback threw: {ex.Message}"); }
            }

            lock (_requestGate)
            {
                if (!_rescanWanted)
                {
                    _scanRunning = false;
                    return;
                }
                _rescanWanted = false;
            }
        }
    }

    /// <summary>
    /// The scan itself. Only ever called from <see cref="ScanLoop"/>, which is what
    /// keeps two of them from running at once.
    /// </summary>
    private void Refresh()
    {
        var devices = EnumerateDevices();
        var byDevice = new Dictionary<string, DeviceModes>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in devices)
        {
            byDevice[device.DeviceName] =
                new DeviceModes(EnumerateModes(device.DeviceName),
                                ReadCurrentMode(device.DeviceName));
        }
        _snapshot = new Snapshot(devices, byDevice);
        _scanned = true;
    }

    /// <summary>
    /// Nothing known yet — get a scan moving and let the caller render what it has.
    /// This is the only path that can return an empty list on a machine that has
    /// displays, and it lasts until the first scan lands.
    /// </summary>
    private void EnsureScanned()
    {
        if (_scanned) return;
        // One reader can ask several of these in a single rebuild; a scan already on
        // its way answers all of them, and queueing another per call would have the
        // pool chasing its own tail until the first one lands.
        lock (_requestGate)
        {
            if (_scanRunning) return;
        }
        RefreshAsync();
    }

    // ---------------------------------------------------------------- readers

    /// <summary>
    /// Attached, active displays, primary first, as of the last scan. Does not touch
    /// the OS, so it is safe on the UI thread.
    /// </summary>
    public IReadOnlyList<DisplayDevice> GetDevices()
    {
        EnsureScanned();
        return _snapshot.Devices;
    }

    /// <summary>Distinct modes for a device, largest first, as of the last scan.</summary>
    public IReadOnlyList<DisplayMode> GetModes(string deviceName)
    {
        EnsureScanned();
        return _snapshot.For(deviceName).Modes;
    }

    /// <summary>The device's mode as of the last scan.</summary>
    public DisplayMode? GetCurrentMode(string deviceName)
    {
        EnsureScanned();
        return _snapshot.For(deviceName).Current;
    }

    // ---------------------------------------------------------------- scanning

    private IReadOnlyList<DisplayDevice> EnumerateDevices()
    {
        var config = DisplayConfigNames.Query();

        // Collect first, name second: "External display" is only numbered once we know
        // there is more than one of them, which matches how the brightness list reads.
        var found = new List<(string DeviceName, string Described, bool IsInternal, bool IsPrimary)>();
        var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        for (uint i = 0; EnumDisplayDevices(null, i, ref dd, 0); i++)
        {
            dd.cb = Marshal.SizeOf<DISPLAY_DEVICE>();
            bool attached = (dd.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0;
            if (!attached) continue;
            bool primary = (dd.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) != 0;

            // The adapter's DeviceString is the GPU name; the monitor's own description
            // lives on the child device, so query one level down. The CCD name is better
            // still when it is there, because it comes from the EDID rather than the INF.
            config.TryGetValue(dd.DeviceName, out var entry);
            string described = !string.IsNullOrWhiteSpace(entry.FriendlyName)
                ? entry.FriendlyName
                : GetMonitorName(dd.DeviceName) ?? dd.DeviceString;

            found.Add((dd.DeviceName, described, entry.IsInternal, primary));
        }

        int externals = found.Count(d => !d.IsInternal);
        int externalIndex = 0;
        var result = new List<DisplayDevice>();
        foreach (var d in found)
        {
            string name = d.IsInternal
                ? "Built-in display"
                : Describe(d.Described, externals == 1 ? null : ++externalIndex);
            result.Add(new DisplayDevice(d.DeviceName, name, d.IsPrimary));
        }
        return result.OrderByDescending(d => d.IsPrimary).ToList();
    }

    /// <summary>
    /// Same rule as the brightness list, so the two widgets agree: a monitor that
    /// only reports the driver's placeholder description gets a positional name.
    /// </summary>
    private static string Describe(string description, int? index) =>
        string.IsNullOrWhiteSpace(description) || description == "Generic PnP Monitor"
            ? (index is null ? "External display" : $"External display {index}")
            : description;

    private static string? GetMonitorName(string adapterName)
    {
        var child = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        if (EnumDisplayDevices(adapterName, 0, ref child, 0))
            return string.IsNullOrWhiteSpace(child.DeviceString) ? null : child.DeviceString;
        return null;
    }

    /// <summary>Distinct modes at the display's current colour depth, largest first.</summary>
    private static IReadOnlyList<DisplayMode> EnumerateModes(string deviceName)
    {
        const int bpp = 32;

        var set = new HashSet<DisplayMode>();
        var dm = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
        for (int i = 0; EnumDisplaySettings(deviceName, i, ref dm); i++)
        {
            if (dm.dmBitsPerPel != bpp) continue;
            if (dm.dmDisplayFrequency <= 1) continue; // 0/1 mean "adapter default"
            set.Add(new DisplayMode(dm.dmPelsWidth, dm.dmPelsHeight, dm.dmDisplayFrequency));
        }
        return set.OrderByDescending(m => m.Width * m.Height)
                  .ThenByDescending(m => m.RefreshHz)
                  .ToList();
    }

    private static DisplayMode? ReadCurrentMode(string deviceName)
    {
        var dm = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
        if (!EnumDisplaySettings(deviceName, ENUM_CURRENT_SETTINGS, ref dm)) return null;
        return new DisplayMode(dm.dmPelsWidth, dm.dmPelsHeight, dm.dmDisplayFrequency);
    }

    /// <summary>
    /// Apply a mode. The change is written to the registry so it survives a reboot,
    /// and validated first so an unsupported mode is rejected rather than blanking
    /// the screen.
    ///
    /// This one does block: it is a user-initiated change that the display itself
    /// takes a moment to accept, and there is nothing to show until it has.
    /// </summary>
    public bool SetMode(string deviceName, DisplayMode mode)
    {
        var dm = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
        if (!EnumDisplaySettings(deviceName, ENUM_CURRENT_SETTINGS, ref dm)) return false;

        dm.dmPelsWidth = mode.Width;
        dm.dmPelsHeight = mode.Height;
        dm.dmDisplayFrequency = mode.RefreshHz;
        dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;

        int test = ChangeDisplaySettingsEx(deviceName, ref dm, nint.Zero, CDS_TEST, nint.Zero);
        if (test != DISP_CHANGE_SUCCESSFUL)
        {
            _log($"display mode {mode} rejected for {deviceName} (code {test})");
            return false;
        }

        int result = ChangeDisplaySettingsEx(deviceName, ref dm, nint.Zero,
                                             CDS_UPDATEREGISTRY, nint.Zero);
        if (result != DISP_CHANGE_SUCCESSFUL)
        {
            _log($"display mode change failed for {deviceName} (code {result})");
            return false;
        }
        return true;
    }

    // ---------------------------------------------------------------- interop

    private const int ENUM_CURRENT_SETTINGS = -1;
    private const int CDS_UPDATEREGISTRY = 0x00000001;
    private const int CDS_TEST = 0x00000002;
    private const int DISP_CHANGE_SUCCESSFUL = 0;
    private const int DM_PELSWIDTH = 0x00080000;
    private const int DM_PELSHEIGHT = 0x00100000;
    private const int DM_DISPLAYFREQUENCY = 0x00400000;
    private const int DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x00000001;
    private const int DISPLAY_DEVICE_PRIMARY_DEVICE = 0x00000004;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields;
        public int dmPositionX, dmPositionY;
        public int dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight;
        public int dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType;
        public int dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? device, uint devNum,
                                                  ref DISPLAY_DEVICE info, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE dm);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(string deviceName, ref DEVMODE dm,
                                                       nint hwnd, int flags, nint param);
}
