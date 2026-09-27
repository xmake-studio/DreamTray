using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using DreamTray.Logging;

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
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_DPICHANGED = 0x02E0;
    /// <summary>Version-4 notification that the shell is opening the icon's tooltip.</summary>
    private const int NIN_POPUPOPEN = 0x0406;

    /// <summary>
    /// How stale a cached icon rectangle may get while the pointer is sitting on the
    /// icon. Short enough that a taskbar rearranging under the cursor is caught,
    /// long enough that a hover does not mean one cross-process call per mouse move.
    /// </summary>
    private const int RectCacheRefreshMs = 200;

    private readonly uint _taskbarCreatedMessage;
    private readonly Guid _iconId = new("6f6a2f7a-2b1e-4b28-9f1a-2b0d4c8e1a11");

    private HwndSource? _source;
    private nint _iconHandle;
    private bool _added;
    /// <summary>Retries NIM_ADD while the shell is not ready to take the icon.</summary>
    private DispatcherTimer? _addRetry;
    private bool _light;
    /// <summary>Set between a press already acted on and the release that ends it.</summary>
    private bool _pressHandled;

    /// <summary>
    /// When the last click this icon acted on was being acted on, in the same clock
    /// <c>GetMessageTime</c> reports. Zero until the first one. See
    /// <see cref="RaiseActivated"/>.
    /// </summary>
    private int _lastClickHandledAt;
    private bool _anyClickHandled;

    /// <summary>
    /// Last rectangle the shell gave us, and when. See <see cref="CachedIconRect"/>.
    ///
    /// One immutable object rather than a Rect and a timestamp side by side, because
    /// it is written from the thread pool and read from the UI thread: a reference
    /// swap is atomic, whereas a 32-byte Rect assignment can be read half-updated,
    /// and half of one rectangle and half of another is a panel anchored nowhere.
    /// </summary>
    private sealed record IconRect(Rect Bounds, long AtTicks);

    private volatile IconRect _cached = new(Rect.Empty, 0);

    /// <summary>
    /// When a hover last queued a refresh. UI-thread only, and deliberately separate
    /// from <see cref="_cached"/>: throttling off the cached record would mean
    /// reading it, editing it and writing it back, and a background answer landing
    /// inside that would be overwritten with the stale rectangle it just replaced.
    /// </summary>
    private long _refreshQueuedAtTicks;

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

        // The app runs elevated and Explorer does not, so UIPI drops Explorer's
        // TaskbarCreated broadcast unless it is let through explicitly — and without
        // it an icon lost to an Explorer restart, or to a logon where the task ran
        // before the taskbar existed, never comes back.
        ChangeWindowMessageFilterEx(_source.Handle, _taskbarCreatedMessage, MSGFLT_ALLOW, nint.Zero);

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
        if (!Shell_NotifyIcon(NIM_ADD, ref data))
        {
            // At logon the autostart task can beat Explorer to the taskbar. TaskbarCreated
            // should announce it, but keep knocking as well rather than trust one message
            // to be the only way the icon ever appears.
            ScheduleAddRetry();
            return;
        }
        StopAddRetry();
        // Version 4 gives us proper mouse messages with screen coordinates in wParam.
        Shell_NotifyIcon(NIM_SETVERSION, ref data);
        _added = true;
        // Seed the cache now, so the very first click has a rectangle to use.
        RefreshIconRectInBackground();
    }

    private void ScheduleAddRetry()
    {
        if (_addRetry != null) return;
        Log.Write("tray: shell refused the icon (taskbar not ready?), retrying");
        _addRetry = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _addRetry.Tick += (_, _) =>
        {
            if (_source == null || _added) { StopAddRetry(); return; }
            Add();
            if (_added) Log.Write("tray: icon added after retry");
        };
        _addRetry.Start();
    }

    private void StopAddRetry()
    {
        _addRetry?.Stop();
        _addRetry = null;
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
        if (Shell_NotifyIconGetRect(ref id, out RECT r) != 0)
        {
            // The icon is in the overflow flyout, or the shell is not answering.
            // Either way we no longer know where it is, and a remembered rectangle
            // from before it was collapsed would anchor the panel to empty taskbar.
            _cached = new IconRect(Rect.Empty, Environment.TickCount64);
            return Rect.Empty;
        }

        var bounds = new Rect(r.left, r.top, r.right - r.left, r.bottom - r.top);
        _cached = new IconRect(bounds, Environment.TickCount64);
        return bounds;
    }

    /// <summary>
    /// The icon's rectangle as of the last time the shell was asked, without asking
    /// it again. Empty when it has never answered, or answered that the icon is
    /// hidden.
    ///
    /// <see cref="GetIconRect"/> is a cross-process call into Explorer, and a
    /// blocking one. On the path between a click and the panel appearing that is the
    /// single worst thing in this app: on a machine under load — which is exactly
    /// when someone opens a power panel — Explorer can take a long time to answer,
    /// and until it does nothing at all has happened on screen.
    ///
    /// So it is asked at the moments nobody is waiting: when the icon is registered,
    /// when the taskbar is rebuilt or its DPI changes, while the pointer hovers (a
    /// tray click is preceded by mouse moves over the icon, which arrive here as
    /// messages), and after each panel open completes. The click itself reads what
    /// those left behind.
    /// </summary>
    public Rect CachedIconRect => _cached.Bounds;

    /// <summary>
    /// Bring the cache up to date from a hover, throttled, and off this thread.
    ///
    /// Off this thread matters more than it looks. Hovering is an idle moment, so
    /// blocking here would usually be free — but "usually" is not the case being
    /// designed for. The whole point of the cache is the machine where Explorer is
    /// slow to answer, and doing it inline would mean that on exactly that machine
    /// the UI thread is stuck inside the hover that precedes the click, so the click
    /// waits anyway and nothing has been gained.
    /// </summary>
    private void RefreshRectThrottled()
    {
        // Claimed when the call is queued, not when it answers: two hovers a frame
        // apart would otherwise both see a stale clock and both queue a call.
        if (Environment.TickCount64 - _refreshQueuedAtTicks < RectCacheRefreshMs) return;
        _refreshQueuedAtTicks = Environment.TickCount64;
        RefreshIconRectInBackground();
    }

    /// <summary>
    /// Ask the shell where the icon is, off the UI thread, and keep the answer.
    /// Used after the panel is on screen: the rectangle is wanted for the *next*
    /// open, so the wait belongs anywhere but here.
    ///
    /// Shell_NotifyIconGetRect takes an HWND but does not require the calling thread
    /// to own it, and the answer lands in a single reference swap.
    /// </summary>
    public void RefreshIconRectInBackground()
    {
        if (_source == null) return;
        System.Threading.ThreadPool.QueueUserWorkItem(static state =>
        {
            var icon = (TrayIcon)state!;
            try { icon.GetIconRect(); }
            catch { /* the shell not answering is the case this exists for */ }
        }, this);
    }

    /// <summary>
    /// Act on one click, unless it was already in the queue when the previous one was
    /// acted on.
    ///
    /// A tray icon is a toggle, so every click that reaches the app flips the panel —
    /// which is right for clicks the user makes, and wrong for the ones they make
    /// while nothing is happening. When the UI thread stalls, the clicks do not go
    /// anywhere: they pile up behind it, and the moment it comes back they all arrive
    /// within a few milliseconds of each other and toggle the panel open, shut, open,
    /// shut. Whether the user is left looking at a panel is then down to how many
    /// times they clicked, which is not a thing they can be expected to get right.
    ///
    /// The message clock separates the two. A click the user made in response to
    /// something was posted after that something happened; a click from the backlog
    /// was posted before it. Only the first of a burst was ever a decision, so it is
    /// the only one that counts, and the panel ends up in the state that click asked
    /// for rather than in whichever state the parity of the queue landed on.
    /// </summary>
    private void RaiseActivated()
    {
        int postedAt = GetMessageTime();
        // Unchecked subtraction, never a comparison: the clock is a 32-bit tick count
        // and wraps every 49 days, and a wrap under a plain "<" would discard every
        // click for the length of the queue.
        if (_anyClickHandled && unchecked(postedAt - _lastClickHandledAt) < 0) return;

        // Claimed before the handler runs as well as after it, because the handler is
        // what pumps: an open reaches the hardware, and a click dispatched from inside
        // it would otherwise find the mark still on the click before this one.
        _anyClickHandled = true;
        _lastClickHandledAt = Environment.TickCount;
        try { Activated?.Invoke(); }
        finally { _lastClickHandledAt = Environment.TickCount; }
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
        else if (msg == WM_DPICHANGED)
        {
            // Rebuild goes down the Modify path when the icon is still registered,
            // which does not reseed the cache — but the taskbar has just been laid
            // out again, so the remembered rectangle is exactly the stale one.
            Rebuild();
            RefreshIconRectInBackground();
        }
        else if (msg == WM_TRAYCALLBACK)
        {
            int mouseMessage = (int)(lParam & 0xFFFF);
            switch (mouseMessage)
            {
                // Nothing is waiting on these, and a tray click is always preceded by
                // them. Refreshing here is what lets the click itself read a cached
                // rectangle instead of blocking on Explorer for it.
                case WM_MOUSEMOVE:
                case NIN_POPUPOPEN:
                    RefreshRectThrottled();
                    handled = true;
                    break;

                case WM_LBUTTONDOWN:
                    _pressHandled = true;
                    RaiseActivated();
                    handled = true;
                    break;

                // Two clicks inside the system double-click time (500 ms by default)
                // arrive as DOWN, UP, DBLCLK, UP — the second click's press is promoted
                // to DBLCLK. A tray icon has no separate double-click gesture, so this
                // is simply that click's press; without it every second click of a fast
                // double click would be dropped.
                case WM_LBUTTONDBLCLK:
                    _pressHandled = true;
                    RaiseActivated();
                    handled = true;
                    break;

                case WM_LBUTTONUP:
                    // The release of a click already acted on at press time. It only
                    // means anything when no press came with it — the keyboard's
                    // Enter/Space on the focused icon arrives as a bare UP.
                    if (_pressHandled) _pressHandled = false;
                    else RaiseActivated();
                    handled = true;
                    break;
                case WM_RBUTTONUP:
                    ContextMenuRequested?.Invoke();
                    handled = true;
                    break;
            }
        }
        return nint.Zero;
    }

    public void Dispose()
    {
        StopAddRetry();
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

    private const uint MSGFLT_ALLOW = 1;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ChangeWindowMessageFilterEx(nint hwnd, uint message, uint action, nint changeInfo);

    /// <summary>
    /// When the message being dispatched right now was posted. The tray callback is
    /// posted by the shell, so this is when the user actually clicked — not when the
    /// app got round to hearing about it.
    /// </summary>
    [DllImport("user32.dll")]
    private static extern int GetMessageTime();

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
