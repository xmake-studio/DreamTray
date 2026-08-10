using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DreamTray.App.Interop;

/// <summary>
/// The notification-area icon, driven straight through <c>Shell_NotifyIcon</c>.
///
/// WinForms' NotifyIcon would do this too, but pulling System.Windows.Forms into a
/// WPF process costs several MB of working set and a second UI framework's worth
/// of startup work — for one 16-pixel icon. This talks to the shell directly from
/// a message-only window instead.
///
/// It also handles the two things a tray icon must never get wrong: re-adding
/// itself when Explorer restarts, and re-rendering when the taskbar switches
/// between light and dark.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const int WM_APP = 0x8000;
    private const int WM_TRAYCALLBACK = WM_APP + 1;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_DPICHANGED = 0x02E0;

    private readonly uint _taskbarCreatedMessage;
    private readonly Guid _iconId = new("6f6a2f7a-2b1e-4b28-9f1a-2b0d4c8e1a11");

    private HwndSource? _source;
    private nint _iconHandle;
    private bool _added;
    private bool _light;
    /// <summary>Set between a press already acted on and the release that ends it.</summary>
    private bool _pressHandled;

    /// <summary>
    /// Left click (or Enter/Space on the keyboard-focused icon). Raised on the button
    /// going *down*: that is the moment the shell moves focus, and waiting for the
    /// release only makes the panel answer late on a slow click.
    /// </summary>
    public event Action? Activated;
    /// <summary>Right click — the app shows its context menu.</summary>
    public event Action? ContextMenuRequested;

    public TrayIcon(string tooltip, bool lightIcon)
    {
        Tooltip = tooltip;
        _light = lightIcon;
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

        var parameters = new HwndSourceParameters("DreamTray.TrayHost")
        {
            Width = 0,
            Height = 0,
            // HWND_MESSAGE would be lighter, but a message-only window cannot own the
            // foreground, and the shell requires a real window to route icon input.
            WindowStyle = 0, // WS_OVERLAPPED, never shown
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);

        Rebuild();
    }

    public string Tooltip { get; private set; }

    /// <summary>Swap between the black and white gear (taskbar theme changed).</summary>
    public void SetLight(bool light)
    {
        if (_light == light) return;
        _light = light;
        Rebuild();
    }

    public void SetTooltip(string tooltip)
    {
        Tooltip = tooltip;
        if (_added) Modify();
    }

    /// <summary>Re-render the icon for the current DPI/theme and (re)register it.</summary>
    private void Rebuild()
    {
        if (_source == null) return;

        if (_iconHandle != nint.Zero) { IconFactory.DestroyIcon(_iconHandle); _iconHandle = nint.Zero; }
        _iconHandle = IconFactory.CreateGear(TraySizeForDpi(), _light);

        if (_added) Modify();
        else Add();
    }

    /// <summary>
    /// The shell asks for SM_CXSMICON scaled to the taskbar's DPI. Reading the
    /// system metric directly gives the right number on a per-monitor-aware process.
    /// </summary>
    private static int TraySizeForDpi()
    {
        int size = GetSystemMetrics(SM_CXSMICON);
        return size <= 0 ? 16 : size;
    }

    private NOTIFYICONDATA BuildData()
    {
        var data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _source!.Handle,
            uID = 1,
            uFlags = NIF_ICON | NIF_MESSAGE | NIF_TIP | NIF_SHOWTIP,
            uCallbackMessage = WM_TRAYCALLBACK,
            hIcon = _iconHandle,
            szTip = Tooltip,
            uVersion = NOTIFYICON_VERSION_4,
        };
        return data;
    }

    private void Add()
    {
        var data = BuildData();
        if (!Shell_NotifyIcon(NIM_ADD, ref data)) return;
        // Version 4 gives us proper mouse messages with screen coordinates in wParam.
        Shell_NotifyIcon(NIM_SETVERSION, ref data);
        _added = true;
    }

    private void Modify()
    {
        var data = BuildData();
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    /// <summary>Show a shell balloon notification.</summary>
    public void ShowBalloon(string title, string message)
    {
        if (!_added) return;
        var data = BuildData();
        data.uFlags = NIF_INFO;
        data.szInfoTitle = title.Length > 63 ? title[..63] : title;
        data.szInfo = message.Length > 255 ? message[..255] : message;
        data.dwInfoFlags = 0; // no icon: this is status, not an alert
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    /// <summary>
    /// Screen rectangle of the icon, in physical pixels. Used to anchor the panel
    /// to the icon rather than guessing from the cursor. Empty when the icon is
    /// hidden in the overflow flyout.
    /// </summary>
    public Rect GetIconRect()
    {
        if (_source == null) return Rect.Empty;
        var id = new NOTIFYICONIDENTIFIER
        {
            cbSize = Marshal.SizeOf<NOTIFYICONIDENTIFIER>(),
            hWnd = _source.Handle,
            uID = 1,
        };
        if (Shell_NotifyIconGetRect(ref id, out RECT r) != 0) return Rect.Empty;
        return new Rect(r.left, r.top, r.right - r.left, r.bottom - r.top);
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == _taskbarCreatedMessage)
        {
            // Explorer restarted and forgot every icon; register again.
            _added = false;
            Rebuild();
            handled = true;
        }
        else if (msg == WM_TRAYCALLBACK)
        {
            int mouseMessage = (int)(lParam & 0xFFFF);
            switch (mouseMessage)
            {
                case WM_LBUTTONDOWN:
                    _pressHandled = true;
                    Activated?.Invoke();
                    handled = true;
                    break;

                // Two clicks inside the system double-click time (500 ms by default)
                // arrive as DOWN, UP, DBLCLK, UP — the second click's press is promoted
                // to DBLCLK. A tray icon has no separate double-click gesture, so this
                // is simply that click's press; without it every second click of a fast
                // double click would be dropped.
                case WM_LBUTTONDBLCLK:
                    _pressHandled = true;
                    Activated?.Invoke();
                    handled = true;
                    break;

                case WM_LBUTTONUP:
                    // The release of a click already acted on at press time. It only
                    // means anything when no press came with it — the keyboard's
                    // Enter/Space on the focused icon arrives as a bare UP.
                    if (_pressHandled) _pressHandled = false;
                    else Activated?.Invoke();
                    handled = true;
                    break;
                case WM_RBUTTONUP:
                    ContextMenuRequested?.Invoke();
                    handled = true;
                    break;
            }
        }
        else if (msg == WM_DPICHANGED)
        {
            Rebuild();
        }
        return nint.Zero;
    }

    public void Dispose()
    {
        if (_added && _source != null)
        {
            var data = BuildData();
            Shell_NotifyIcon(NIM_DELETE, ref data);
            _added = false;
        }
        if (_iconHandle != nint.Zero) { IconFactory.DestroyIcon(_iconHandle); _iconHandle = nint.Zero; }
        _source?.Dispose();
        _source = null;
    }

    // ---------------------------------------------------------------- interop

    private const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    private const int NIF_MESSAGE = 0x01, NIF_ICON = 0x02, NIF_TIP = 0x04,
                      NIF_INFO = 0x10, NIF_SHOWTIP = 0x80;
    private const int NOTIFYICON_VERSION_4 = 4;
    private const int SM_CXSMICON = 49;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public nint hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NOTIFYICONIDENTIFIER
    {
        public int cbSize;
        public nint hWnd;
        public int uID;
        public Guid guidItem;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);

    [DllImport("shell32.dll")]
    private static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER id, out RECT rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
