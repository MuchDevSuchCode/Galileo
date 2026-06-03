using System;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace PhotosPlus.Models;

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

    [ObservableProperty]
    private BitmapImage? _thumbnail;

    private bool _thumbnailRequested;

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

    /// <summary>Lazily decodes a small thumbnail. Safe to call repeatedly.</summary>
    public async Task LoadThumbnailAsync(uint size = 240)
    {
        if (_thumbnailRequested) return;
        _thumbnailRequested = true;

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(Path);
            using StorageItemThumbnail thumb =
                await file.GetThumbnailAsync(ThumbnailMode.PicturesView, size, ThumbnailOptions.ResizeThumbnail);

            var bmp = new BitmapImage { DecodePixelWidth = (int)size };
            await bmp.SetSourceAsync(thumb);
            Thumbnail = bmp;
        }
        catch
        {
            // Unreadable / unsupported file — leave thumbnail null (placeholder shows).
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
