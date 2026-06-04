using System;
using System.Threading;
using System.Threading.Tasks;

namespace PhotosPlus.Services;

/// <summary>
/// Caps how many thumbnail/icon image decodes run at once. Fast virtualized scrolling through a
/// folder of many media files can queue hundreds of BitmapImage.SetSourceAsync calls, flooding the
/// decode pipeline and hard-crashing the WinUI render thread (0xc000027b, no managed exception).
/// Serializing decodes to a small number keeps the pipeline healthy.
/// </summary>
public static class DecodeThrottle
{
    private static readonly SemaphoreSlim Gate = new(4, 4);

    /// <summary>Runs <paramref name="work"/> while holding a decode slot; always releases it.</summary>
    public static async Task RunAsync(Func<Task> work)
    {
        await Gate.WaitAsync();
        try { await work(); }
        finally { Gate.Release(); }
    }
}
