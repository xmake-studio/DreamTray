using System.ComponentModel;
using System.Diagnostics;
using System.IO.Ports;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DreamTray.Plugins.CyberVfd;

/// <summary>
/// Finds the CyberVFD among the COM ports and keeps a data link to it.
///
/// The device is identified by handshake — send "CVFD?", expect "CVFD1 …" — rather
/// than by COM number, which Windows reassigns freely, or by VID/PID, which would
/// also match unrelated boards using the same USB-serial bridge. Ports that do not
/// answer are closed again untouched.
///
/// Talks to the port through the Win32 comm API directly rather than through
/// <see cref="SerialPort"/>. SerialPort opens every port overlapped and starts a
/// thread per port that waits on comm events, and it hands every byte the device
/// sends back — this one answers every frame — to the thread pool as an event to
/// raise, whether anyone subscribed or not. For a link that writes one line a
/// second, that was a standing pool of wakeups: the event thread, an I/O
/// completion, a pool worker and the pool's gate thread, every second, all day. A
/// synchronous handle needs none of them: a write is a write, and the echo is
/// discarded from the same thread on the next one.
/// </summary>
internal sealed class SerialLink : IDisposable
{
    private const string Probe = "CVFD?";
    private const string Magic = "CVFD1";

    /// <summary>How long one port is given to answer — long enough to cover a device
    /// that resets when the port is opened (ESP32-C3 boot is well under a second).</summary>
    private const int ProbeWindowMs = 1800;
    private const int ProbeRepeatMs = 400;

    private const int BaudRate = 115200;
    /// <summary>How long a read waits for its first byte (the handshake's poll).</summary>
    private const int ReadTimeoutMs = 300;
    /// <summary>A write that cannot be handed to the driver in this long is a dead link.</summary>
    private const int WriteTimeoutMs = 800;

    private SafeFileHandle? _handle;
    private string? _portName;

    public bool Connected => _handle is { IsInvalid: false, IsClosed: false };
    public string? PortName => _portName;

    public static string[] AvailablePorts()
    {
        try { return SerialPort.GetPortNames().Distinct().OrderBy(n => n).ToArray(); }
        catch { return []; }
    }

    /// <summary>Scan every COM port and connect to the first that answers the handshake.</summary>
    public bool TryConnect()
    {
        foreach (var name in AvailablePorts())
            if (TryConnectTo(name)) return true;
        return false;
    }

    /// <summary>Handshake a single named port (manual-port mode).</summary>
    public bool TryConnectTo(string name)
    {
        SafeFileHandle? handle = null;
        try
        {
            handle = Open(name);
            if (handle == null) return false;
            Thread.Sleep(60);
            PurgeComm(handle, PURGE_RXABORT | PURGE_RXCLEAR);

            // The probe is repeated across the window, not sent once -- opening the
            // port can make a USB-CDC device re-enumerate, so a single probe sent
            // into those first milliseconds can be lost to a device still booting.
            // Re-asking until the window is out gets an answer from a device that
            // woke up mid-handshake.
            var sw = Stopwatch.StartNew();
            long nextProbeAt = 0;
            var line = new StringBuilder();
            var buffer = new byte[256];
            while (sw.ElapsedMilliseconds < ProbeWindowMs)
            {
                if (sw.ElapsedMilliseconds >= nextProbeAt)
                {
                    if (!Write(handle, Probe + "\n")) break;
                    nextProbeAt = sw.ElapsedMilliseconds + ProbeRepeatMs;
                }

                // Returns at once with whatever has arrived, or after ReadTimeoutMs
                // with nothing -- the same poll ReadLine with a timeout was.
                if (!ReadFile(handle, buffer, buffer.Length, out int read, nint.Zero)) break;
                for (int i = 0; i < read; i++)
                {
                    if (buffer[i] != (byte)'\n')
                    {
                        // A chatty device on some other port must not grow this forever.
                        if (line.Length < 256) line.Append((char)buffer[i]);
                        continue;
                    }
                    if (line.ToString().Contains(Magic))
                    {
                        _handle = handle;
                        _portName = name;
                        return true;
                    }
                    line.Clear();
                }
            }
            Close(handle);
            return false;
        }
        catch
        {
            Close(handle);
            return false;
        }
    }

    /// <summary>Write one frame. Returns false (and drops the link) on any I/O error.</summary>
    public bool Send(string frame)
    {
        if (_handle is not { IsInvalid: false, IsClosed: false } handle) return false;
        try
        {
            if (!Write(handle, frame + "\n"))
            {
                Disconnect();
                return false;
            }
            // Drain anything the device echoed so the input buffer never grows.
            if (ClearCommError(handle, out _, out var status) && status.cbInQue > 0)
                PurgeComm(handle, PURGE_RXABORT | PURGE_RXCLEAR);
            return true;
        }
        catch
        {
            Disconnect();
            return false;
        }
    }

    public void Disconnect()
    {
        Close(_handle);
        _handle = null;
        _portName = null;
    }

    public void Dispose() => Disconnect();

    // ---------------------------------------------------------------- port

    /// <summary>
    /// Open and configure a port the way <see cref="SerialPort"/> did for this link:
    /// 115200 8N1, no flow control, DTR and then RTS raised in that order. The order
    /// is kept deliberately — some USB-serial boards treat RTS-without-DTR as a reset
    /// request — and so is everything else a device might notice.
    /// </summary>
    private static SafeFileHandle? Open(string name)
    {
        // Synchronous, not overlapped: there is one writer, and no read outside the
        // handshake, so nothing here needs I/O completion ports or an event thread.
        var handle = CreateFile(@"\\.\" + name, GENERIC_READ | GENERIC_WRITE, 0, nint.Zero,
                                OPEN_EXISTING, 0, nint.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }
        try
        {
            if (GetFileType(handle) != FILE_TYPE_CHAR) throw new IOException($"{name} is not a serial port");

            var dcb = new DCB { DCBlength = (uint)Marshal.SizeOf<DCB>() };
            if (!GetCommState(handle, ref dcb)) throw new Win32Exception();
            dcb.BaudRate = BaudRate;
            dcb.ByteSize = 8;
            dcb.Parity = NOPARITY;
            dcb.StopBits = ONESTOPBIT;
            dcb.SetFlag(FBINARY, 1);
            dcb.SetFlag(FPARITY, 0);
            dcb.SetFlag(FOUTXCTSFLOW, 0);
            dcb.SetFlag(FOUTXDSRFLOW, 0);
            dcb.SetFlag(FDTRCONTROL, DTR_CONTROL_DISABLE);
            dcb.SetFlag(FDSRSENSITIVITY, 0);
            dcb.SetFlag(FOUTX, 0);
            dcb.SetFlag(FINX, 0);
            dcb.SetFlag(FERRORCHAR, 0);
            dcb.SetFlag(FNULL, 0);
            if (dcb.GetFlag(FRTSCONTROL) == RTS_CONTROL_HANDSHAKE) dcb.SetFlag(FRTSCONTROL, RTS_CONTROL_DISABLE);
            dcb.SetFlag(FABORTONERROR, 0);
            dcb.XonChar = 17;
            dcb.XoffChar = 19;
            dcb.EofChar = 26;
            dcb.EvtChar = 26;
            if (!SetCommState(handle, ref dcb)) throw new Win32Exception();
            SetupComm(handle, 4096, 2048);

            var timeouts = new COMMTIMEOUTS
            {
                ReadIntervalTimeout = uint.MaxValue,
                ReadTotalTimeoutMultiplier = uint.MaxValue,
                ReadTotalTimeoutConstant = ReadTimeoutMs,
                WriteTotalTimeoutMultiplier = 0,
                WriteTotalTimeoutConstant = WriteTimeoutMs,
            };
            if (!SetCommTimeouts(handle, ref timeouts)) throw new Win32Exception();

            // Present as a terminal; steady DTR won't reset the board.
            dcb.SetFlag(FDTRCONTROL, DTR_CONTROL_ENABLE);
            if (!SetCommState(handle, ref dcb)) throw new Win32Exception();
            EscapeCommFunction(handle, SETDTR);
            dcb.SetFlag(FRTSCONTROL, RTS_CONTROL_ENABLE);
            if (!SetCommState(handle, ref dcb)) throw new Win32Exception();
            EscapeCommFunction(handle, SETRTS);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static bool Write(SafeFileHandle handle, string text)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(text);
        // A short count is the write timeout expiring: the device stopped draining.
        return WriteFile(handle, bytes, bytes.Length, out int written, nint.Zero) && written == bytes.Length;
    }

    /// <summary>Closing drops DTR first, as SerialPort's close did.</summary>
    private static void Close(SafeFileHandle? handle)
    {
        if (handle is null || handle.IsInvalid || handle.IsClosed) return;
        try
        {
            EscapeCommFunction(handle, CLRDTR);
            PurgeComm(handle, PURGE_RXABORT | PURGE_RXCLEAR | PURGE_TXABORT | PURGE_TXCLEAR);
        }
        catch { /* the device may already be gone */ }
        handle.Dispose();
    }

    // ---------------------------------------------------------------- interop

    private const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    private const uint OPEN_EXISTING = 3;
    private const int FILE_TYPE_CHAR = 2;

    private const byte NOPARITY = 0, ONESTOPBIT = 0;

    // DCB flag bit positions, as SerialStream names them.
    private const int FBINARY = 0, FPARITY = 1, FOUTXCTSFLOW = 2, FOUTXDSRFLOW = 3, FDTRCONTROL = 4,
                      FDSRSENSITIVITY = 6, FOUTX = 8, FINX = 9, FERRORCHAR = 10, FNULL = 11,
                      FRTSCONTROL = 12, FABORTONERROR = 14;
    private const int DTR_CONTROL_DISABLE = 0, DTR_CONTROL_ENABLE = 1;
    private const int RTS_CONTROL_DISABLE = 0, RTS_CONTROL_ENABLE = 1, RTS_CONTROL_HANDSHAKE = 2;

    private const uint SETRTS = 3, SETDTR = 5, CLRDTR = 6;
    private const uint PURGE_TXABORT = 0x1, PURGE_RXABORT = 0x2, PURGE_TXCLEAR = 0x4, PURGE_RXCLEAR = 0x8;

    [StructLayout(LayoutKind.Sequential)]
    private struct DCB
    {
        public uint DCBlength;
        public uint BaudRate;
        public uint Flags;
        public ushort wReserved;
        public ushort XonLim;
        public ushort XoffLim;
        public byte ByteSize;
        public byte Parity;
        public byte StopBits;
        public byte XonChar;
        public byte XoffChar;
        public byte ErrorChar;
        public byte EofChar;
        public byte EvtChar;
        public ushort wReserved1;

        // The two-bit fields (DTR and RTS control) are the only ones wider than a bit.
        private static uint Mask(int position) =>
            position is FDTRCONTROL or FRTSCONTROL ? 0b11u << position : 1u << position;

        public int GetFlag(int position) => (int)((Flags & Mask(position)) >> position);

        public void SetFlag(int position, int value) =>
            Flags = (Flags & ~Mask(position)) | (((uint)value << position) & Mask(position));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct COMMTIMEOUTS
    {
        public uint ReadIntervalTimeout;
        public uint ReadTotalTimeoutMultiplier;
        public uint ReadTotalTimeoutConstant;
        public uint WriteTotalTimeoutMultiplier;
        public uint WriteTotalTimeoutConstant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct COMSTAT
    {
        public uint Flags;
        public uint cbInQue;
        public uint cbOutQue;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, nint security,
                                                    uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll")]
    private static extern int GetFileType(SafeFileHandle handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetCommState(SafeFileHandle handle, ref DCB dcb);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetCommState(SafeFileHandle handle, ref DCB dcb);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetupComm(SafeFileHandle handle, uint inQueue, uint outQueue);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetCommTimeouts(SafeFileHandle handle, ref COMMTIMEOUTS timeouts);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool EscapeCommFunction(SafeFileHandle handle, uint function);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PurgeComm(SafeFileHandle handle, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ClearCommError(SafeFileHandle handle, out uint errors, out COMSTAT status);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(SafeFileHandle handle, byte[] buffer, int count, out int read, nint overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(SafeFileHandle handle, byte[] buffer, int count, out int written, nint overlapped);
}
