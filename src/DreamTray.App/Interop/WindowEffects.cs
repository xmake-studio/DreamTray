using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DreamTray.App.Interop;

/// <summary>
/// The DWM attributes that make a plain WPF window look like a Windows 11 surface:
/// rounded corners, a dark-aware frame, and a system backdrop material.
///
/// Every call is best-effort. On a build that does not know an attribute DWM just
/// returns a failure code, so the window falls back to its solid theme colour
/// rather than rendering wrong — which is why <see cref="TryApplyBackdrop"/>
/// reports whether the material actually took.
/// </summary>
internal static class WindowEffects
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_CLOAK = 13;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    private const int DWMWCP_DONOTROUND = 1;
    private const int DWMWCP_ROUND = 2;
    private const int DWMWCP_ROUNDSMALL = 3;

    public enum Backdrop
    {
        None = 1,
        /// <summary>Opaque, wallpaper-tinted. The right material for a main window.</summary>
        Mica = 2,
        /// <summary>Translucent, blurs what is behind. The right material for a flyout.</summary>
        Acrylic = 3,
        /// <summary>Mica Alt — the tabbed-window variant.</summary>
        MicaAlt = 4,
    }

    public static void SetDarkMode(Window window, bool dark)
    {
        nint hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == nint.Zero) return;
        int value = dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
    }

    public static void SetRoundedCorners(Window window, bool small = false)
    {
        nint hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == nint.Zero) return;
        int value = small ? DWMWCP_ROUNDSMALL : DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref value, sizeof(int));
    }

    /// <summary>
    /// Round the window to an arbitrary radius by clipping it to a rounded-rect
    /// region. DWM only offers its own two radii (~8 and ~4 dips), so anything
    /// larger has to come from a region — which clips the backdrop material too,
    /// so the acrylic follows the new corners.
    ///
    /// Must be re-applied whenever the window resizes: a region is in device
    /// pixels and does not scale with the window.
    /// </summary>
    public static void SetCornerRadius(Window window, double radiusDips)
    {
        nint hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == nint.Zero) return;
        if (!GetWindowRect(hwnd, out RECT rect)) return;

        int width = rect.right - rect.left;
        int height = rect.bottom - rect.top;
        if (width <= 0 || height <= 0) return;

        // DWM's own rounding would cut a smaller arc out of our larger one, so it
        // has to be turned off before the region takes over.
        int noRound = DWMWCP_DONOTROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref noRound, sizeof(int));

        double scale = GetDpiScale(window);
        if (scale <= 0) scale = 1;
        // CreateRoundRectRgn takes the full ellipse size, not the radius, and the
        // region is exclusive of the right/bottom edge.
        int diameter = (int)Math.Round(radiusDips * scale * 2);
        diameter = Math.Max(0, Math.Min(diameter, Math.Min(width, height)));

        nint region = CreateRoundRectRgn(0, 0, width + 1, height + 1, diameter, diameter);
        if (region == nint.Zero) return;
        // Ownership passes to the window on success; on failure we still own it.
        if (SetWindowRgn(hwnd, region, true) == 0) DeleteObject(region);
    }

    /// <summary>
    /// Request a system backdrop. Returns true only when DWM accepted it — the
    /// caller uses that to decide between a translucent tint and a solid fill.
    ///
    /// With "Transparency effects" turned off in Settings, DWM still returns
    /// success for the attribute but paints a flat grey instead of the material.
    /// Reporting that as a live backdrop is what left the window a washed-out
    /// grey, so the user's preference is checked before the attribute is set.
    /// </summary>
    public static bool TryApplyBackdrop(Window window, Backdrop backdrop)
    {
        nint hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == nint.Zero) return false;

        if (backdrop != Backdrop.None && !IsTransparencyEnabled())
        {
            int none = (int)Backdrop.None;
            DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref none, sizeof(int));
            return false;
        }

        int value = (int)backdrop;
        return DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref value, sizeof(int)) == 0;
    }

    /// <summary>
    /// Whether Settings › Personalisation › Colours › "Transparency effects" is on.
    /// Missing value means on, which is the Windows default.
    /// </summary>
    public static bool IsTransparencyEnabled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("EnableTransparency") is not int v || v != 0;
        }
        catch { return true; }
    }

    /// <summary>
    /// Extend the glass frame over the whole client area. Without this the backdrop
    /// is drawn only behind the (nonexistent) title bar and the body stays black.
    /// </summary>
    public static void ExtendFrameIntoClientArea(Window window)
    {
        nint hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == nint.Zero) return;
        var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        DwmExtendFrameIntoClientArea(hwnd, ref margins);
    }

    /// <summary>Work area (excluding the taskbar) of the monitor under a screen point, in pixels.</summary>
    public static Rect GetWorkArea(Point screenPoint) => MonitorRect(screenPoint, work: true);

    /// <summary>
    /// Full bounds of the monitor under a screen point, in pixels — the work area
    /// plus whatever the taskbar occupies. A flyout that has to start off-screen
    /// measures against this, not the work area: the taskbar edge is where it should
    /// appear to come from, not where it should start.
    /// </summary>
    public static Rect GetMonitorArea(Point screenPoint) => MonitorRect(screenPoint, work: false);

    private static Rect MonitorRect(Point screenPoint, bool work)
    {
        var pt = new POINT { X = (int)screenPoint.X, Y = (int)screenPoint.Y };
        nint monitor = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor == nint.Zero || !GetMonitorInfo(monitor, ref info))
            return new Rect(0, 0, 1920, 1080);
        var r = work ? info.rcWork : info.rcMonitor;
        return new Rect(r.left, r.top, r.right - r.left, r.bottom - r.top);
    }

    public static Point GetCursorPosition()
    {
        GetCursorPos(out POINT p);
        return new Point(p.X, p.Y);
    }

    /// <summary>Outer size of a window in device pixels, straight from the OS.</summary>
    public static bool TryGetSize(Window window, out int width, out int height)
    {
        width = height = 0;
        nint hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == nint.Zero || !GetWindowRect(hwnd, out RECT rect)) return false;
        width = rect.right - rect.left;
        height = rect.bottom - rect.top;
        return width > 0 && height > 0;
    }

    /// <summary>
    /// Move a window, in device pixels, without going through WPF's Left/Top.
    /// Those convert through DIPs and run the property system on every change, which
    /// is too much per-frame overhead for a slide; this is the bare SetWindowPos.
    ///
    /// Note the absence of NOCOPYBITS. That flag tells Windows to discard the client
    /// area and invalidate all of it instead of moving the pixels it already has —
    /// which is fatal for a window travelling in from off-screen, because the parts
    /// still outside the monitor cannot be repainted and so end up undefined.
    /// Letting the bits move with the window is what keeps it whole.
    /// </summary>
    public static void MoveTo(Window window, int xPixels, int yPixels)
    {
        nint hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == nint.Zero) return;
        SetWindowPos(hwnd, nint.Zero, xPixels, yPixels, 0, 0,
            SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    /// <summary>
    /// Hide a window from the screen while leaving it fully live: still visible to
    /// the window manager, still laid out, still painting. This is what the shell
    /// uses for windows on other virtual desktops, and it is the only way to let a
    /// window compose a complete frame at a position the user must not see it in.
    /// </summary>
    public static void SetCloaked(Window window, bool cloaked)
    {
        nint hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == nint.Zero) return;
        int value = cloaked ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_CLOAK, ref value, sizeof(int));
    }

    /// <summary>Scale factor of the monitor a window is on (1.0 at 96 DPI).</summary>
    public static double GetDpiScale(Window window)
    {
        var source = PresentationSource.FromVisual(window);
        return source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
    }

    // ---------------------------------------------------------------- interop

    private const int MONITOR_DEFAULTTONEAREST = 2;

    private const int SWP_NOSIZE = 0x0001;
    private const int SWP_NOZORDER = 0x0004;
    private const int SWP_NOACTIVATE = 0x0010;
    private const int SWP_NOOWNERZORDER = 0x0200;

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS { public int Left, Right, Top, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(nint hwnd, ref MARGINS margins);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(POINT pt, int flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(nint monitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        nint hwnd, nint insertAfter, int x, int y, int cx, int cy, int flags);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(nint hwnd, nint region, bool redraw);

    [DllImport("gdi32.dll")]
    private static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint obj);
}
