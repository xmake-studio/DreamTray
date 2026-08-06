using System.Runtime.InteropServices;

namespace DreamTray.Display;

/// <summary>
/// Resolution and refresh-rate control via the classic GDI display APIs
/// (<c>EnumDisplaySettingsEx</c> / <c>ChangeDisplaySettingsEx</c>). These are
/// cheap, need no elevation, and apply per-monitor without disturbing the others.
/// </summary>
public sealed class DisplayModeService
{
    private readonly Action<string> _log;

    public DisplayModeService(Action<string> log) => _log = log;

    /// <summary>Attached, active displays, primary first.</summary>
    public IReadOnlyList<DisplayDevice> GetDevices()
    {
        var result = new List<DisplayDevice>();
        var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        for (uint i = 0; EnumDisplayDevices(null, i, ref dd, 0); i++)
        {
            dd.cb = Marshal.SizeOf<DISPLAY_DEVICE>();
            bool attached = (dd.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0;
            if (!attached) continue;
            bool primary = (dd.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) != 0;

            // The adapter's DeviceString is the GPU name; the monitor's friendly name
            // lives on the child device, so query one level down.
            string friendly = GetMonitorName(dd.DeviceName) ?? dd.DeviceString;
            result.Add(new DisplayDevice(dd.DeviceName, friendly, primary));
        }
        return result.OrderByDescending(d => d.IsPrimary).ToList();
    }

    private static string? GetMonitorName(string adapterName)
    {
        var child = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        if (EnumDisplayDevices(adapterName, 0, ref child, 0))
            return string.IsNullOrWhiteSpace(child.DeviceString) ? null : child.DeviceString;
        return null;
    }

    /// <summary>Distinct modes at the display's current colour depth, largest first.</summary>
    public IReadOnlyList<DisplayMode> GetModes(string deviceName)
    {
        var current = GetCurrentMode(deviceName);
        int bpp = current == null ? 32 : 32;

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

    public DisplayMode? GetCurrentMode(string deviceName)
    {
        var dm = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
        if (!EnumDisplaySettings(deviceName, ENUM_CURRENT_SETTINGS, ref dm)) return null;
        return new DisplayMode(dm.dmPelsWidth, dm.dmPelsHeight, dm.dmDisplayFrequency);
    }

    /// <summary>
    /// Apply a mode. The change is written to the registry so it survives a reboot,
    /// and validated first so an unsupported mode is rejected rather than blanking
    /// the screen.
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
