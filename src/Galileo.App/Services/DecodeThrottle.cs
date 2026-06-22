using System;
using System.Threading;
using System.Threading.Tasks;

namespace Galileo.Services;

/// <summary>
/// Caps how many thumbnail/icon image decodes run at once. Fast virtualized scrolling through a
/// folder of many media files can queue hundreds of BitmapImage.SetSourceAsync calls, flooding the
/// decode pipeline and hard-crashing the WinUI render thread (0xc000027b, no managed exception).
/// Serializing decodes to a small number keeps the pipeline healthy.
/// </summary>
public static class DecodeThrottle
{
    private static readonly SemaphoreSlim Gate = new(4, 4);

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
