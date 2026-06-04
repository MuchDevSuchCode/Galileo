using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PhotosPlus.Services;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace PhotosPlus.Models;

public enum ExplorerItemKind { Folder, File, Drive }

/// <summary>A folder, file, or drive shown in the file-explorer list.</summary>
public partial class ExplorerItem : ObservableObject
{
    public string Path { get; }
    public string Name { get; }
    public ExplorerItemKind Kind { get; }
    public bool IsFolder => Kind != ExplorerItemKind.File;
    public long Size { get; }
    public DateTime Modified { get; }
    public string TypeName { get; }

    [ObservableProperty] private bool _isAppHidden;
    [ObservableProperty] private ImageSource? _icon;

    private bool _iconRequested;

    public ExplorerItem(string path, ExplorerItemKind kind, long size, DateTime modified, string typeName, string? displayName = null)
    {
        Path = path;
        Kind = kind;
        Size = size;
        Modified = modified;
        TypeName = typeName;
        Name = displayName ?? (kind == ExplorerItemKind.Drive ? path : System.IO.Path.GetFileName(path));
        if (string.IsNullOrEmpty(Name)) Name = path;
    }

    public bool IsImage => Kind == ExplorerItemKind.File && PhotoLibrary.IsSupported(Path);

    public string SizeText => Kind == ExplorerItemKind.File ? FormatSize(Size) : "";
    public string ModifiedText => Modified == default ? "" : Modified.ToString("yyyy-MM-dd HH:mm");

    // Dim app-hidden folders when the user has chosen to reveal them.
    public double ItemOpacity => IsAppHidden ? 0.45 : 1.0;
    partial void OnIsAppHiddenChanged(bool value) => OnPropertyChanged(nameof(ItemOpacity));

    /// <summary>
    /// Loads the file/folder/drive icon with proper transparency via the shell (IShellItemImageFactory),
    /// falling back to GetThumbnailAsync only if that fails.
    /// </summary>
    public async Task LoadIconAsync(uint size = 96)
    {
        if (_iconRequested) return;
        _iconRequested = true;

        var path = Path;
        var px = (int)Math.Clamp(size, 32u, 256u);
        try
        {
            // Folders & drives: plain shell icon (transparent, orientation-corrected). We force
            // icon-only because the shell's folder *content* thumbnails come back inconsistently
            // through this API (some bottom-up/flipped, some opaque-white JPEGs).
            if (Kind != ExplorerItemKind.File)
            {
                var (pixels, w, h) = await Task.Run(() => ShellImaging.GetPixels(path, px, iconOnly: true));
                if (pixels is not null && w > 0 && h > 0)
                {
                    var wb = new WriteableBitmap(w, h);
                    using (var s = wb.PixelBuffer.AsStream()) s.Write(pixels, 0, pixels.Length);
                    Icon = wb;
                    return;
                }
                var folderThumb = await (await StorageFolder.GetFolderFromPathAsync(path))
                    .GetThumbnailAsync(ThumbnailMode.SingleItem, size, ThumbnailOptions.ResizeThumbnail);
                if (folderThumb is not null)
                {
                    using (folderThumb) { var b = new BitmapImage(); await b.SetSourceAsync(folderThumb); Icon = b; }
                }
                return;
            }

            // Files: WinRT thumbnail (correct orientation; photos are opaque so no alpha concern).
            var file = await StorageFile.GetFileFromPathAsync(path);
            var thumb = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, size, ThumbnailOptions.ResizeThumbnail);
            if (thumb is not null)
            {
                using (thumb)
                {
                    var bmp = new BitmapImage();
                    await bmp.SetSourceAsync(thumb);
                    Icon = bmp;
                }
            }
        }
        catch
        {
            _iconRequested = false; // allow a retry later
        }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        string[] units = { "KB", "MB", "GB", "TB" };
        double v = bytes;
        var u = -1;
        do { v /= 1024; u++; } while (v >= 1024 && u < units.Length - 1);
        return $"{v:0.#} {units[u]}";
    }
}
