using System.Text;

namespace DreamTray.Logging;

/// <summary>
/// Dead-simple append log. Diagnosing hardware access on someone else's laptop is
/// impossible without one, but it must never cost anything at runtime: writes are
/// buffered on a background flush and the file is truncated once it passes a cap.
///
/// The flush is armed by the first write after the last one went out, not run on a
/// fixed period: a timer that fires every two seconds to find nothing to write is a
/// wakeup every two seconds for the life of a process whose whole point is to sleep.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static readonly StringBuilder Pending = new();
    private static readonly Timer Flusher = new(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    private const int FlushDelayMs = 2000;

    private const long MaxBytes = 512 * 1024;

    public static string FilePath => Path.Combine(Settings.SettingsStore.Folder, "dreamtray.log");

    public static void Write(string message)
    {
        lock (Gate)
        {
            // Milliseconds, not seconds: the things worth diagnosing here — how long a
            // flyout took to appear, which phase of an open ate the time — all happen
            // well inside one second, and a second-resolution stamp collapses them
            // into a single indistinguishable line.
            bool armed = Pending.Length > 0;
            Pending.Append(DateTime.Now.ToString("HH:mm:ss.fff")).Append("  ").AppendLine(message);
            if (Pending.Length > 8192) FlushLocked();
            else if (!armed) Flusher.Change(FlushDelayMs, Timeout.Infinite);
        }
    }

    public static void Flush()
    {
        lock (Gate) FlushLocked();
    }

    private static void FlushLocked()
    {
        if (Pending.Length == 0) return;
        try
        {
            Directory.CreateDirectory(Settings.SettingsStore.Folder);
            var info = new FileInfo(FilePath);
            if (info.Exists && info.Length > MaxBytes) File.Delete(FilePath);
            File.AppendAllText(FilePath, Pending.ToString());
        }
        catch { /* logging must never be the thing that breaks the app */ }
        Pending.Clear();
    }

    /// <summary>Stop the flush timer (shutdown path).</summary>
    public static void Shutdown()
    {
        Flusher.Dispose();
        Flush();
    }
}
