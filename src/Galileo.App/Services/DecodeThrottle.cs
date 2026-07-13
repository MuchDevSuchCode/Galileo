using System;
using System.Threading;
using System.Threading.Tasks;

namespace Galileo.Services;

/// <summary>
/// Caps how many thumbnail/icon image decodes run at once. Fast virtualized scrolling through a
/// folder of many media files can queue hundreds of decodes; when those decodes ran through
/// BitmapImage.SetSourceAsync they flooded the WinUI render thread and hard-crashed it
/// (0xc000027b, no managed exception). A bound keeps the pipeline healthy.
///
/// The decode itself now runs off-thread via IShellItemImageFactory and only touches the UI thread
/// to copy pixels into a WriteableBitmap, so the render thread is no longer the bottleneck the cap
/// was protecting — the disk is. The cap therefore scales with the machine instead of sitting at 4,
/// which left an NVMe drive idle while thumbnails trickled in.
/// </summary>
public static class DecodeThrottle
{
    private static readonly SemaphoreSlim Gate =
        new(Math.Clamp(Environment.ProcessorCount / 2, 4, 12), 12);

    /// <summary>
    /// Runs <paramref name="work"/> while holding a decode slot; always releases it.
    /// Pass a <paramref name="ct"/> tied to the item's on-screen lifetime: a fast scroll queues
    /// hundreds of waiters, and cancelling the ones that scrolled off before a slot opened lets the
    /// queue drain without decoding now-invisible items — which is what otherwise keeps the decode
    /// pipeline flooded and crashes the render thread. Throws <see cref="OperationCanceledException"/>
    /// if cancelled while still waiting for a slot.
    /// </summary>
    public static async Task RunAsync(Func<Task> work, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try { await work(); }
        finally { Gate.Release(); }
    }
}
