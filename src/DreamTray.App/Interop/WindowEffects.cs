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
    private const int DWMWA_CLOAKED = 14;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    private const int DWM_CLOAKED_SHELL = 0x2;
    private const int DWM_CLOAKED_INHERITED = 0x4;

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
        if (!TryGetSize(window, out int width, out int height)) return;
        SetCornerRadius(window, radiusDips, width, height);
    }

    /// <summary>
    /// As above, for a size the caller already knows — which is the only correct one
    /// to use while handling WM_WINDOWPOSCHANGED. The message carries the new size in
    /// its WINDOWPOS, and asking the window for it there can still return the rect it
    /// had before the resize; a region built from that answer is short by exactly the
    /// resize, and it clips the window until something else happens to rebuild it.
    /// </summary>
    public static void SetCornerRadius(Window window, double radiusDips, int width, int height)
    {
        nint hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == nint.Zero) return;
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
    /// Drop any region set by <see cref="SetCornerRadius"/> and hand the corners back
    /// to DWM. A region is a 1-bit stencil in device pixels: it clips with hard,
    /// aliased edges and cuts straight through a backdrop material, which is exactly
    /// what makes an acrylic window look like it has chipped corners. DWM's own
    /// rounding is composited with the material and stays smooth, so a translucent
    /// window wants that instead.
    /// </summary>
    public static void ClearCornerRegion(Window window, bool small = false)
    {
        nint hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == nint.Zero) return;
        SetWindowRgn(hwnd, nint.Zero, true);
        int value = small ? DWMWCP_ROUNDSMALL : DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref value, sizeof(int));
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

    /// <summary>
    /// Whether this window is the one the user is currently working in.
    ///
    /// Asked of the window manager rather than tracked from WPF's Activated and
    /// Deactivated, because those are what needs checking: showing a window moves the
    /// foreground about, and both events can arrive out of a sequence that has since
    /// settled somewhere else entirely. This is where it settled.
    /// </summary>
    public static bool IsForegroundWindow(nint hwnd) =>
        hwnd != nint.Zero && GetForegroundWindow() == hwnd;

    /// <summary>
    /// The bounding box of the clip region currently on the window, in window
    /// coordinates and device pixels. Empty when the window has no region.
    ///
    /// This is the only honest answer to "is the region still the right size": the
    /// region lives in the window manager, and anything we remember about it here is
    /// a guess that can drift. A region shorter than the window clips the bottom off
    /// it — with rounded corners, since that is what the region is — while every
    /// number WPF reports stays perfectly correct.
    /// </summary>
    public static Rect GetRegionBox(Window window)
    {
        nint hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == nint.Zero) return Rect.Empty;
        // Returns one of the region-type constants, or ERROR (0) when there is none.
        if (GetWindowRgnBox(hwnd, out RECT r) == 0) return Rect.Empty;
        return new Rect(r.left, r.top, r.right - r.left, r.bottom - r.top);
    }

    /// <summary>
    /// The size a WM_WINDOWPOSCHANGED is reporting, in device pixels. False when the
    /// message is a move rather than a resize, or carries nothing usable.
    /// </summary>
    public static bool TryReadWindowPos(nint lParam, out int width, out int height)
    {
        width = height = 0;
        if (lParam == nint.Zero) return false;
        var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
        if ((pos.flags & SWP_NOSIZE) != 0) return false;
        width = pos.cx;
        height = pos.cy;
        return width > 0 && height > 0;
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
    ///
    /// NOSENDCHANGING, on the other hand, is worth having. Every move otherwise
    /// dispatches a WM_WINDOWPOSCHANGING through the window's WndProc and into WPF's
    /// own handling of it before the move happens, on the UI thread, on the frame
    /// callback, for each of the frames a slide is made of. Nothing here wants that
    /// message: this app hooks WM_WINDOWPOSCHANGED (the past tense) for its region
    /// check, and WPF's use of CHANGING is SizeToContent clamping, which StartSlide
    /// has already pinned to Manual for the duration. The move itself is unaffected —
    /// only the chance to veto or amend it, which nobody was taking.
    /// </summary>
    public static void MoveTo(Window window, int xPixels, int yPixels)
    {
        nint hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == nint.Zero) return;
        SetWindowPos(hwnd, nint.Zero, xPixels, yPixels, 0, 0,
            SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_NOSENDCHANGING);
    }

    /// <summary>
    /// Hide a window from the screen while leaving it fully live: still visible to
    /// the window manager, still laid out, still painting, its last frame still held
    /// by the compositor. This is what the shell uses for windows on other virtual
    /// desktops. A cloaked window is not hit-tested either, so one left in place
    /// takes no clicks meant for whatever is behind it.
    /// </summary>
    public static void SetCloaked(Window window, bool cloaked) =>
        SetCloaked(new WindowInteropHelper(window).Handle, cloaked);

    /// <summary>As above, for a handle the caller has already cached.</summary>
    public static void SetCloaked(nint hwnd, bool cloaked)
    {
        if (hwnd == nint.Zero) return;
        int value = cloaked ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_CLOAK, ref value, sizeof(int));
    }

    /// <summary>
    /// Whether something other than this app is keeping the window off the screen —
    /// the shell, which cloaks every window that belongs to a virtual desktop other
    /// than the current one. Our own cloak cannot lift that, so a window that finds
    /// itself in this state has to be shown again from scratch to be adopted by the
    /// desktop the user is on.
    /// </summary>
    public static bool IsCloakedByShell(nint hwnd)
    {
        if (hwnd == nint.Zero) return false;
        if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int reason, sizeof(int)) != 0) return false;
        return (reason & (DWM_CLOAKED_SHELL | DWM_CLOAKED_INHERITED)) != 0;
    }

    /// <summary>
    /// Mark a window as a tool window: never on the taskbar or in Alt+Tab, and — the
    /// part that matters for a window that stays shown — not owned by any one virtual
    /// desktop, so it is on whichever one the user has switched to.
    /// </summary>
    public static void SetToolWindow(nint hwnd) => SetExStyle(hwnd, WS_EX_TOOLWINDOW, true);

    /// <summary>
    /// Whether the window manager may give this window the foreground on its own —
    /// when it is clicked, or when the window above it closes. Switched off for a
    /// window that is shown but cloaked, which must never end up with keyboard focus
    /// the user cannot see.
    /// </summary>
    public static void SetActivatable(nint hwnd, bool activatable) =>
        SetExStyle(hwnd, WS_EX_NOACTIVATE, !activatable);

    private static void SetExStyle(nint hwnd, long bit, bool on)
    {
        if (hwnd == nint.Zero) return;
        long style = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        long wanted = on ? style | bit : style & ~bit;
        if (wanted != style) SetWindowLongPtr(hwnd, GWL_EXSTYLE, (nint)wanted);
    }

    /// <summary>Raise a window to the top of its band without activating it.</summary>
    public static void BringToFront(nint hwnd)
    {
        if (hwnd == nint.Zero) return;
        SetWindowPos(hwnd, HWND_TOP, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    /// <summary>Drop a window to the bottom of the z-order without activating anything.</summary>
    public static void SendToBack(nint hwnd)
    {
        if (hwnd == nint.Zero) return;
        SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    /// <summary>
    /// Give the foreground to the window the user would land on if this one had been
    /// hidden: the next one down the z-order that can actually take it. Hiding a
    /// window makes the window manager do this by itself; cloaking one does not, and
    /// without it the keyboard would stay with a panel nobody can see.
    /// </summary>
    public static void ActivateWindowBelow(nint hwnd)
    {
        if (hwnd == nint.Zero) return;
        for (nint next = GetWindow(hwnd, GW_HWNDNEXT); next != nint.Zero; next = GetWindow(next, GW_HWNDNEXT))
        {
            if (!IsWindowVisible(next) || IsIconic(next) || !IsWindowEnabled(next)) continue;
            long ex = GetWindowLongPtr(next, GWL_EXSTYLE);
            if ((ex & (WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE)) != 0) continue;
            if (DwmGetWindowAttribute(next, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
                continue;
            if (!GetWindowRect(next, out RECT r) || r.right <= r.left || r.bottom <= r.top) continue;
            SetForegroundWindow(next);
            return;
        }
    }

    /// <summary>
    /// Block until the compositor has taken every surface update already handed to
    /// it — for WPF, the frame its render thread has just presented. At most one
    /// composition interval.
    /// </summary>
    public static void WaitForComposition()
    {
        try { DwmFlush(); }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    /// <summary>Scale factor of the monitor a window is on (1.0 at 96 DPI).</summary>
    public static double GetDpiScale(Window window)
    {
        var source = PresentationSource.FromVisual(window);
        return source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
    }

    /// <summary>
    /// Scale factor of the monitor under a screen point, asked of the OS directly.
    ///
    /// The window-based overload reads WPF's cached transform, which is only correct
    /// once the window has actually been placed on the monitor in question: in a
    /// PerMonitorV2 process a window that has not been moved there yet still reports
    /// the DPI it was created with. Anything that converts the monitor's rectangle
    /// into DIPs *before* positioning has to ask about the monitor, not the window.
    /// </summary>
    public static double GetDpiScale(Point screenPoint)
    {
        var pt = new POINT { X = (int)screenPoint.X, Y = (int)screenPoint.Y };
        nint monitor = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
        if (monitor == nint.Zero) return 0;
        // Shcore is present from Windows 8.1 on; a failure just falls back to the
        // window's own scale at the call site.
        try
        {
            if (GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint dpiX, out _) != 0) return 0;
            return dpiX <= 0 ? 0 : dpiX / 96.0;
        }
        catch (DllNotFoundException) { return 0; }
        catch (EntryPointNotFoundException) { return 0; }
    }

    /// <summary>
    /// The scale the OS says the window's own monitor has — the same question
    /// <see cref="GetDpiScale(Window)"/> answers, asked of Windows instead of of WPF.
    ///
    /// The two must agree, and when they do not, WPF is the one that is wrong: its
    /// value is a cache refreshed only by WM_DPICHANGED, and a window that was hidden
    /// when the display configuration changed never receives one. Everything the panel
    /// computes — its width in pixels, its height budget, where its edges land — is a
    /// conversion between WPF's units and the monitor's, so a stale cache does not
    /// degrade the layout, it scales the whole window by the ratio of the two.
    /// Comparing them is the only way to catch that before it reaches the screen.
    /// </summary>
    public static double GetDpiScaleForWindow(nint hwnd)
    {
        if (hwnd == nint.Zero) return 0;
        // Windows 10 1607 and later. On anything older there is no per-monitor DPI to
        // go stale in the first place, so a failure here is safely "no disagreement".
        try
        {
            uint dpi = GetDpiForWindow(hwnd);
            return dpi == 0 ? 0 : dpi / 96.0;
        }
        catch (DllNotFoundException) { return 0; }
        catch (EntryPointNotFoundException) { return 0; }
    }

    /// <summary>
    /// Every monitor's bounds, work area and scale on one line, for the log entry
    /// written when the display configuration changes. A panel that comes back the
    /// wrong size after a resolution change is a report that cannot be acted on
    /// without knowing what the display layout actually became.
    /// </summary>
    public static string DescribeMonitors()
    {
        var parts = new List<string>();
        try
        {
            bool Callback(nint monitor, nint hdc, ref RECT rect, nint data)
            {
                var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (!GetMonitorInfo(monitor, ref info)) return true;
                double scale = 0;
                try
                {
                    if (GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0)
                        scale = dpiX / 96.0;
                }
                catch (DllNotFoundException) { }
                catch (EntryPointNotFoundException) { }

                RECT m = info.rcMonitor, w = info.rcWork;
                parts.Add(
                    $"[{m.left},{m.top} {m.right - m.left}x{m.bottom - m.top} " +
                    $"work {w.right - w.left}x{w.bottom - w.top} @{scale:F2}" +
                    $"{((info.dwFlags & MONITORINFOF_PRIMARY) != 0 ? " primary" : "")}]");
                return true;
            }
            EnumDisplayMonitors(nint.Zero, nint.Zero, Callback, nint.Zero);
        }
        catch (Exception ex) { return $"unavailable ({ex.Message})"; }
        return parts.Count == 0 ? "none" : string.Join(" ", parts);
    }

    // ---------------------------------------------------------------- interop

    private const int MONITOR_DEFAULTTONEAREST = 2;
    private const int MONITORINFOF_PRIMARY = 1;
    private const int MDT_EFFECTIVE_DPI = 0;

    private const int SWP_NOSIZE = 0x0001;
    private const int SWP_NOMOVE = 0x0002;
    private const int SWP_NOZORDER = 0x0004;
    private const int SWP_NOACTIVATE = 0x0010;
    private const int SWP_NOOWNERZORDER = 0x0200;
    private const int SWP_NOSENDCHANGING = 0x0400;

    private static readonly nint HWND_TOP = 0;
    private static readonly nint HWND_BOTTOM = 1;

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const long WS_EX_NOACTIVATE = 0x08000000;
    private const int GW_HWNDNEXT = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS { public int Left, Right, Top, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPOS
    {
        public nint hwnd, hwndInsertAfter;
        public int x, y, cx, cy;
        public uint flags;
    }

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
    private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(nint hwnd, ref MARGINS margins);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern long GetWindowLongPtr(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint hwnd, int command);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowEnabled(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(POINT pt, int flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(nint monitor, ref MONITORINFO info);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    private delegate bool MonitorEnumProc(nint monitor, nint hdc, ref RECT rect, nint data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc callback, nint data);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        nint hwnd, nint insertAfter, int x, int y, int cx, int cy, int flags);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(nint hwnd, nint region, bool redraw);

    [DllImport("user32.dll")]
    private static extern int GetWindowRgnBox(nint hwnd, out RECT rect);

    [DllImport("gdi32.dll")]
    private static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint obj);
}
