using System.Runtime;

namespace DreamTray;

/// <summary>
/// Hands memory back to the OS at the one moment this app can afford to pay for it.
///
/// A tray app spends almost all of its life with nothing on screen, and the panel
/// being closed is the only time a pause cannot be seen. The collector on its own
/// will not use that: with background GC on — which is what keeps a gen2 off the UI
/// thread while the panel *is* open — collections are deliberately unhurried and
/// non-compacting, so a heap that grew while the panel was up stays grown, and the
/// process sits at its high-water mark until something else forces the issue.
///
/// So the trim is scheduled explicitly, after a delay, and skipped if the panel came
/// back in the meantime. What it does not do is empty the working set: that is the
/// obvious next call and it is the wrong one here, because it pages out live pages
/// the next open immediately faults back in — buying a smaller number in Task
/// Manager at the cost of the slow open this whole exercise is about.
/// </summary>
public static class MemoryTrim
{
    /// <summary>
    /// Long enough that closing and reopening the panel does not collect between the
    /// two, short enough that the process is back down before anyone looks at it.
    /// </summary>
    private const int DelayMs = 2000;

    /// <summary>
    /// Below this the heap is already about as small as this app gets, and collecting
    /// would be CPU spent to reclaim nothing — which on a laptop is the wrong trade
    /// twice over. Someone toggling the panel repeatedly should not be paying for a
    /// gen2 per toggle.
    /// </summary>
    private const long FloorBytes = 48L * 1024 * 1024;

    /// <summary>
    /// Above this, the large object heap is compacted as well. LOH compaction is far
    /// more expensive than an ordinary gen2 and pointless on a heap that has nothing
    /// much on the LOH, so it is kept for the case it was meant for: an afternoon of
    /// sensor arrays and widget rebuilds having fragmented it.
    /// </summary>
    private const long LohCompactionThresholdBytes = 64L * 1024 * 1024;

    private static CancellationTokenSource? _pending;
    private static readonly object Gate = new();

    /// <summary>
    /// The panel closed. Trim shortly, unless <see cref="Cancel"/> gets there first.
    /// </summary>
    public static void Schedule(Action<string>? log = null)
    {
        CancellationTokenSource cts;
        lock (Gate)
        {
            _pending?.Cancel();
            _pending?.Dispose();
            _pending = cts = new CancellationTokenSource();
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(DelayMs, cts.Token).ConfigureAwait(false);
                Run(log);
            }
            catch (OperationCanceledException) { /* reopened; nothing to do */ }
            finally
            {
                lock (Gate)
                {
                    if (_pending == cts) _pending = null;
                }
                cts.Dispose();
            }
        });
    }

    /// <summary>The panel reopened before the trim ran. Leave the heap alone.</summary>
    public static void Cancel()
    {
        lock (Gate)
        {
            _pending?.Cancel();
            _pending = null;
        }
    }

    private static void Run(Action<string>? log)
    {
        long before = GC.GetTotalMemory(false);
        if (before < FloorBytes) return;

        // Compacting, and therefore blocking: a background collection sweeps but does
        // not move anything, so it frees without returning the space. Blocking here is
        // the point — this runs on the thread pool with the panel hidden.
        if (before >= LohCompactionThresholdBytes)
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;

        // Forced, not Optimized. Optimized lets the collector decide the collection
        // is not worth doing, which is the right default everywhere except here: the
        // moment has already been chosen for being free, and the entire reason for
        // the call is to hand back a heap that grew while the panel was open.
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

        long after = GC.GetTotalMemory(false);
        log?.Invoke($"memory trim: managed heap {before / (1024 * 1024)} -> " +
                    $"{after / (1024 * 1024)} MB");
    }
}
