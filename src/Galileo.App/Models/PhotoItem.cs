using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace Galileo.Models;

/// <summary>
/// A single photo in the library. Hidden/favorite flags are mirrored from the
/// persistent <see cref="Services.AppState"/> store; the original file is never mutated.
/// </summary>
public partial class PhotoItem : ObservableObject
{
    public string Path { get; }
    public string FileName { get; }

    [ObservableProperty]
    private bool _isFavorite;

    [ObservableProperty]
    private bool _isHidden;

    // ImageSource, not BitmapImage: thumbnails come back as WriteableBitmap from the shell pipeline.
    [ObservableProperty]
    private ImageSource? _thumbnail;

    private bool _thumbnailRequested;
    private CancellationTokenSource? _thumbnailCts;

    // Computed Visibility properties let the gallery template bind directly without a
    // value converter (keeps project-local types out of the XAML compiler's metadata pass).
    public Visibility FavoriteVisibility => IsFavorite ? Visibility.Visible : Visibility.Collapsed;
    public Visibility HiddenVisibility => IsHidden ? Visibility.Visible : Visibility.Collapsed;

    partial void OnIsFavoriteChanged(bool value) => OnPropertyChanged(nameof(FavoriteVisibility));
    partial void OnIsHiddenChanged(bool value) => OnPropertyChanged(nameof(HiddenVisibility));

    public PhotoItem(string path)
    {
        Path = path;
        FileName = System.IO.Path.GetFileName(path);
    }

    /// <summary>Width / height of the source image (1.0 until <see cref="EnsureAspectAsync"/> runs).</summary>
    public double Aspect { get; private set; } = 1.0;
    private bool _aspectLoaded;

    /// <summary>Reads the image's pixel dimensions (cheap header read) to populate <see cref="Aspect"/>.</summary>
    /// <remarks>
    /// Uses <see cref="Services.ImageInfo"/> rather than Properties.GetImagePropertiesAsync: the latter
    /// allocates two WinRT objects per photo, and a gallery of hundreds of them starves the finalizer,
    /// the GC and the UI thread against each other. See <see cref="LoadThumbnailAsync"/>.
    /// </remarks>
    public async Task EnsureAspectAsync()
    {
        if (_aspectLoaded) return;

        await Task.Run(() =>
        {
            var dim = Services.ImageInfo.GetDimensions(Path);

            // Formats the header reader doesn't know (HEIC/AVIF/RAW): fall back to the shell thumbnail,
            // which preserves the source aspect. Still off-thread, still pooled, still no WinRT.
            if (dim is null)
            {
                using var img = Services.ShellImaging.GetImage(Path, 96, iconOnly: false);
                if (img.IsValid) dim = (img.Width, img.Height);
            }

            if (dim is { Width: > 0, Height: > 0 } d)
                Aspect = (double)d.Width / d.Height;
            // else leave Aspect at 1.0
        });

        _aspectLoaded = true;
    }

    /// <summary>Cancels a queued/in-flight thumbnail decode when the photo scrolls off-screen (its
    /// grid container is recycled). A fast scroll otherwise leaves hundreds of dead decodes flooding
    /// the pipeline; the decode simply re-runs when the photo scrolls back into view.</summary>
    public void CancelThumbnailLoad()
    {
        if (Thumbnail is not null) return; // finished loading — keep it
        try { _thumbnailCts?.Cancel(); } catch (ObjectDisposedException) { }
        _thumbnailRequested = false;
    }

    /// <summary>Lazily decodes a small thumbnail. Safe to call repeatedly.</summary>
    /// <remarks>
    /// Deliberately does NOT use StorageFile.GetThumbnailAsync + BitmapImage.SetSourceAsync — the same trap
    /// <see cref="ExplorerItem.LoadIconAsync"/> documents. Those WinRT objects are reclaimed by the finalizer,
    /// which has to marshal each one back to the STA to release it, so a gallery of photos deadlocks the
    /// finalizer, the GC and the UI thread against each other. SetSourceAsync also decodes on the UI/render
    /// thread. IShellItemImageFactory (what Explorer itself uses) runs entirely off-thread with an explicit
    /// COM release, and its buffer is pooled, so the whole path allocates nothing on the Large Object Heap.
    /// </remarks>
    public async Task LoadThumbnailAsync(uint size = 240)
    {
        if (_thumbnailRequested) return;
        _thumbnailRequested = true;
        _thumbnailCts?.Dispose();
        _thumbnailCts = new CancellationTokenSource();
        var ct = _thumbnailCts.Token;

        // Throttle concurrent decodes so a fast scroll can't flood the render thread. The token lets
        // a decode still waiting for a slot bail out the moment its photo scrolls off-screen.
        try
        {
            await Services.DecodeThrottle.RunAsync(async () =>
            {
                using var img = await Task.Run(
                    () => Services.ShellImaging.GetImage(Path, (int)size, iconOnly: false), ct);

                // Back on the UI thread: WriteableBitmap must be created there.
                if (ct.IsCancellationRequested) { _thumbnailRequested = false; return; }
                if (!img.IsValid)
                {
                    _thumbnailRequested = false; // unreadable / unsupported — placeholder stays
                    return;
                }

                var wb = new WriteableBitmap(img.Width, img.Height);
                using (var s = wb.PixelBuffer.AsStream())
                    s.Write(img.Pixels!, 0, img.ByteCount); // ByteCount, never Pixels.Length — pooled buffers have slack
                Thumbnail = wb;
            }, ct);
        }
        catch (OperationCanceledException)
        {
            _thumbnailRequested = false; // scrolled off before its decode slot opened — reload when realized again
        }
        catch
        {
            _thumbnailRequested = false;
        }
    }

    public DateTime LastModifiedUtc
    {
        get
        {
            try { return File.GetLastWriteTimeUtc(Path); }
            catch { return DateTime.MinValue; }
        }
    }
}
