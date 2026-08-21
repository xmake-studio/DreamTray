using System.Diagnostics;
using System.IO.Ports;

namespace DreamTray.Plugins.CyberVfd;

/// <summary>
/// Finds the CyberVFD among the COM ports and keeps a data link to it.
///
/// The device is identified by handshake — send "CVFD?", expect "CVFD1 …" — rather
/// than by COM number, which Windows reassigns freely, or by VID/PID, which would
/// also match unrelated boards using the same USB-serial bridge. Ports that do not
/// answer are closed again untouched.
/// </summary>
internal sealed class SerialLink : IDisposable
{
    private const string Probe = "CVFD?";
    private const string Magic = "CVFD1";

    /// <summary>How long one port is given to answer — long enough to cover a device
    /// that resets when the port is opened (ESP32-C3 boot is well under a second).</summary>
    private const int ProbeWindowMs = 1800;
    private const int ProbeRepeatMs = 400;

    private SerialPort? _port;

    public bool Connected => _port is { IsOpen: true };
    public string? PortName => _port?.PortName;

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
        SerialPort? sp = null;
        try
        {
            sp = new SerialPort(name, 115200)
            {
                ReadTimeout = 300,
                WriteTimeout = 800,
                NewLine = "\n",
                DtrEnable = true, // present as a terminal; steady DTR won't reset the C3
                RtsEnable = true,
            };
            sp.Open();
            Thread.Sleep(60);
            sp.DiscardInBuffer();

            // The probe is repeated across the window, not sent once. Opening the port
            // asserts the control lines, and on a USB-CDC device that is enough to make
            // the firmware re-enumerate: a single probe sent into those first
            // milliseconds is written to a device that is still booting and is simply
            // lost, so the port looks like it is not the panel and the scan drops it —
            // then comes back and knocks it over again. Re-asking until the window is
            // out gets an answer from a device that woke up mid-handshake.
            var sw = Stopwatch.StartNew();
            long nextProbeAt = 0;
            while (sw.ElapsedMilliseconds < ProbeWindowMs)
            {
                if (sw.ElapsedMilliseconds >= nextProbeAt)
                {
                    sp.Write(Probe + "\n");
                    nextProbeAt = sw.ElapsedMilliseconds + ProbeRepeatMs;
                }

                string line;
                try { line = sp.ReadLine(); }
                catch (TimeoutException) { continue; }
                if (line.Contains(Magic))
                {
                    _port = sp;
                    return true;
                }
            }
            sp.Close();
            sp.Dispose();
            return false;
        }
        catch
        {
            try { sp?.Dispose(); } catch { /* ignore */ }
            return false;
        }
    }

    /// <summary>Write one frame. Returns false (and drops the link) on any I/O error.</summary>
    public bool Send(string frame)
    {
        if (_port is not { IsOpen: true }) return false;
        try
        {
            _port.Write(frame + "\n");
            // Drain anything the device echoed so the input buffer never grows.
            if (_port.BytesToRead > 0) _port.DiscardInBuffer();
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
        try { _port?.Close(); } catch { /* ignore */ }
        try { _port?.Dispose(); } catch { /* ignore */ }
        _port = null;
    }

    public void Dispose() => Disconnect();
}
