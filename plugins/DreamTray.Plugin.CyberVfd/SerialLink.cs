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
            sp.Write(Probe + "\n");

            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 1000)
            {
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
