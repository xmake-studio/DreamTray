using System.Runtime.InteropServices;

namespace DreamTray.Display;

/// <summary>
/// Friendly names and internal/external classification for the active display paths,
/// keyed by GDI device name (<c>\\.\DISPLAY1</c>).
///
/// <see cref="DisplayModeService"/> works in the classic GDI world, which knows only
/// the monitor's driver description — the same "Generic PnP Monitor" string for every
/// panel that ships without a proper INF. The CCD API (<c>QueryDisplayConfig</c>) has
/// the EDID-derived friendly name and, more usefully here, the connector technology,
/// which is what tells a laptop panel from a plugged-in monitor. That lets the mode
/// widget label displays the same way the brightness widget does.
/// </summary>
internal static class DisplayConfigNames
{
    internal readonly record struct Entry(string FriendlyName, bool IsInternal);

    /// <summary>Empty when the CCD API is unavailable or reports nothing usable.</summary>
    public static Dictionary<string, Entry> Query()
    {
        var result = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out int pathCount, out int modeCount) != ERROR_SUCCESS)
            return result;

        var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
        var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
        if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, nint.Zero) != ERROR_SUCCESS)
            return result;

        for (int i = 0; i < pathCount; i++)
        {
            var path = paths[i];

            var source = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
            {
                header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                {
                    type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                    size = Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                    adapterId = path.sourceInfo.adapterId,
                    id = path.sourceInfo.id,
                },
            };
            if (DisplayConfigGetDeviceInfo(ref source) != ERROR_SUCCESS) continue;
            if (string.IsNullOrWhiteSpace(source.viewGdiDeviceName)) continue;

            var target = new DISPLAYCONFIG_TARGET_DEVICE_NAME
            {
                header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                {
                    type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                    size = Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                    adapterId = path.targetInfo.adapterId,
                    id = path.targetInfo.id,
                },
            };
            if (DisplayConfigGetDeviceInfo(ref target) != ERROR_SUCCESS) continue;

            // A panel wired straight to the board reports one of the embedded
            // connector types; everything else arrived over a cable.
            uint tech = target.outputTechnology;
            bool isInternal = tech is OUTPUT_TECHNOLOGY_INTERNAL
                                   or OUTPUT_TECHNOLOGY_DISPLAYPORT_EMBEDDED
                                   or OUTPUT_TECHNOLOGY_UDI_EMBEDDED;

            // The friendly name is only present when the flag says the EDID had one.
            string name = (target.flags & 0x1) != 0 ? target.monitorFriendlyDeviceName : "";
            result[source.viewGdiDeviceName] = new Entry(name ?? "", isInternal);
        }

        return result;
    }

    // ---------------------------------------------------------------- interop

    private const int ERROR_SUCCESS = 0;
    private const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;
    private const uint OUTPUT_TECHNOLOGY_INTERNAL = 0x80000000;
    private const uint OUTPUT_TECHNOLOGY_DISPLAYPORT_EMBEDDED = 11;
    private const uint OUTPUT_TECHNOLOGY_UDI_EMBEDDED = 13;

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_RATIONAL
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public DISPLAYCONFIG_RATIONAL refreshRate;
        public uint scanLineOrdering;
        public int targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    /// <summary>Opaque here — we only need the buffer to be the right size.</summary>
    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct DISPLAYCONFIG_MODE_INFO
    {
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public uint type;
        public int size;
        public LUID adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint flags;
        public uint outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out int numPathArrayElements,
                                                          out int numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref int numPathArrayElements,
                                                 [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
                                                 ref int numModeInfoArrayElements,
                                                 [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
                                                 nint currentTopologyId);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME request);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME request);
}
