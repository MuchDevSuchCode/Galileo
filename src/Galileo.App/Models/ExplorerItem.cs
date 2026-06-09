using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Galileo.Services;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace Galileo.Models;

public enum ExplorerItemKind { Folder, File, Drive }

/// <summary>A folder, file, or drive shown in the file-explorer list.</summary>
public partial class ExplorerItem : ObservableObject
{
    /// <summary>Whether folder icons get a content-preview overlay (set from settings).</summary>
    public static bool ShowFolderPreviews = true;

    /// <summary>Whether file extensions are shown in the displayed name (set from settings).</summary>
    public static bool ShowExtensions = true;

    /// <summary>User-chosen folder thumbnails (folder path → image path), shared from app state.</summary>
    public static IReadOnlyDictionary<string, string>? FolderThumbnails;

    public string Path { get; }
    public string Name { get; }

    /// <summary>Shell parsing name for items that live in the shell namespace (MTP / portable
    /// devices) and have no filesystem path; null for ordinary filesystem items.</summary>
    public string? ShellId { get; }
    public bool IsShellItem => ShellId is not null;

    /// <summary>Name shown in the UI — drops the extension for files when extensions are hidden.</summary>
    public string DisplayName => Kind == ExplorerItemKind.File && !ShowExtensions
        ? System.IO.Path.GetFileNameWithoutExtension(Path)
        : Name;
    public ExplorerItemKind Kind { get; }
    public bool IsFolder => Kind != ExplorerItemKind.File;
    public long Size { get; }
    public DateTime Modified { get; }
    public string TypeName { get; }

    [ObservableProperty] private bool _isAppHidden;
    [ObservableProperty] private ImageSource? _icon;

    private bool _iconRequested;

    /// <summary>Total / free bytes for drives (0 for other items).</summary>
    public long TotalBytes { get; }
    public long FreeBytes { get; }

    /// <summary>Show the capacity bar + free/total text (drives with a known size).</summary>
    public bool ShowCapacity => Kind == ExplorerItemKind.Drive && TotalBytes > 0;
    public double UsedPercent => TotalBytes > 0 ? (double)(TotalBytes - FreeBytes) / TotalBytes * 100 : 0;
    public string CapacityText => TotalBytes > 0 ? $"{FormatSize(FreeBytes)} free of {FormatSize(TotalBytes)}" : "";
    public Visibility CapacityVisibility => ShowCapacity ? Visibility.Visible : Visibility.Collapsed;

    public ExplorerItem(string path, ExplorerItemKind kind, long size, DateTime modified, string typeName, string? displayName = null, string? shellId = null, long totalBytes = 0, long freeBytes = 0)
    {
        Path = path;
        ShellId = shellId;
        Kind = kind;
        Size = size;
        Modified = modified;
        TypeName = typeName;
        TotalBytes = totalBytes;
        FreeBytes = freeBytes;
        Name = displayName ?? (kind == ExplorerItemKind.Drive ? path : System.IO.Path.GetFileName(path));
        if (string.IsNullOrEmpty(Name)) Name = path;
    }

    public bool IsImage => Kind == ExplorerItemKind.File && PhotoLibrary.IsSupported(IsShellItem ? Name : Path);

    public string SizeText => Kind == ExplorerItemKind.File ? FormatSize(Size) : "";
    public string ModifiedText => Modified == default ? "" : Modified.ToString("yyyy-MM-dd HH:mm");

    // Dim app-hidden folders when the user has chosen to reveal them.
    public double ItemOpacity => IsAppHidden ? 0.45 : 1.0;
    partial void OnIsAppHiddenChanged(bool value) => OnPropertyChanged(nameof(ItemOpacity));

    /// <summary>
    /// Loads the file/folder/drive icon with proper transparency via the shell (IShellItemImageFactory),
    /// falling back to GetThumbnailAsync only if that fails.
    /// </summary>
    /// <summary>Clears the cached icon so the next <see cref="LoadIconAsync"/> regenerates it
    /// (e.g. after the folder's chosen thumbnail changes).</summary>
    public void ResetIcon()
    {
        _iconRequested = false;
        Icon = null;
    }

    public async Task LoadIconAsync(uint size = 96)
    {
        if (_iconRequested) return;
        _iconRequested = true;

        var path = Path;
        var px = (int)Math.Clamp(size, 32u, 256u);

        // Throttle concurrent decodes — a fast scroll through hundreds of media files otherwise
        // floods the decode pipeline and crashes the render thread.
        await DecodeThrottle.RunAsync(async () =>
        {
        try
        {
            // Shell-namespace items (MTP/portable devices) have no filesystem path: get the thumbnail
            // straight from the shell by parsing name (photos thumbnail; folders get the folder icon).
            if (IsShellItem)
            {
                var (sp, sw, sh) = await Task.Run(() => ShellImaging.GetPixels(ShellId!, px, iconOnly: Kind != ExplorerItemKind.File));
                if (sp is not null && sw > 0 && sh > 0)
                {
                    var wb = new WriteableBitmap(sw, sh);
                    using (var s = wb.PixelBuffer.AsStream()) s.Write(sp, 0, sp.Length);
                    Icon = wb;
                }
                return;
            }

            // Folders & drives: Galileo's own flat icon (not the Windows shell icon). Folders overlay
            // a content preview (the first image inside), composited ourselves so it stays upright.
            if (Kind != ExplorerItemKind.File)
            {
                var folderKind = Kind == ExplorerItemKind.Folder ? IconFactory.FolderKindFor(path) : FolderKind.Normal;
                var pixels = Kind == ExplorerItemKind.Drive
                    ? await Task.Run(() => IconFactory.RenderDrive(px))
                    : await Task.Run(() => IconFactory.RenderFolder(px, folderKind));
                // Themed media folders keep their glyph; only plain folders get a content preview overlay.
                if (Kind == ExplorerItemKind.Folder && ShowFolderPreviews && folderKind == FolderKind.Normal)
                    await TryOverlayFirstImageAsync(path, pixels, px, px, px);

                var wb = new WriteableBitmap(px, px);
                using (var s = wb.PixelBuffer.AsStream()) s.Write(pixels, 0, pixels.Length);
                Icon = wb;
                return;
            }

            // Files: prefer a real preview thumbnail (photo/doc) — correct orientation.
            var file = await StorageFile.GetFileFromPathAsync(path);
            StorageItemThumbnail? thumb = null;
            try { thumb = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, size, ThumbnailOptions.ResizeThumbnail); }
            catch { }

            if (thumb is not null && thumb.Type != ThumbnailType.Icon)
            {
                using (thumb) { var b = new BitmapImage(); await b.SetSourceAsync(thumb); Icon = b; }
                return;
            }

            // No real preview. Keep app/shortcut branding (those icons aren't Windows' generic ones)
            // via the shell; for everything else use Galileo's own page-with-extension-badge icon.
            var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            var appIcon = ext is ".exe" or ".lnk" or ".url" or ".msi" or ".ico" or ".scr" or ".cpl";
            if (appIcon)
            {
                var (ip, iw, ih) = await Task.Run(() => ShellImaging.GetPixels(path, px, iconOnly: false));
                if (ip is null) (ip, iw, ih) = await Task.Run(() => ShellImaging.GetPixels(path, px, iconOnly: true));
                if (ip is not null && iw > 0 && ih > 0)
                {
                    var wbApp = new WriteableBitmap(iw, ih);
                    using (var s = wbApp.PixelBuffer.AsStream()) s.Write(ip, 0, ip.Length);
                    Icon = wbApp;
                    thumb?.Dispose();
                    return;
                }
            }

            var fp = await Task.Run(() => IconFactory.RenderFile(px, ext));
            var wbFile = new WriteableBitmap(px, px);
            using (var s = wbFile.PixelBuffer.AsStream()) s.Write(fp, 0, fp.Length);
            Icon = wbFile;
            thumb?.Dispose();
        }
        catch
        {
            _iconRequested = false; // allow a retry later
        }
        });
    }

    private static async Task TryOverlayFirstImageAsync(string folderPath, byte[] folderPixels, int fw, int fh, int px)
    {
        // Prefer the user's chosen thumbnail for this folder; fall back to the first image inside.
        var imgPath = ChosenThumbnail(folderPath) ?? await Task.Run(() => FindFirstImage(folderPath));
        if (imgPath is null) return;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(imgPath);
            using var thumb = await file.GetThumbnailAsync(ThumbnailMode.PicturesView, (uint)Math.Max(48, px * 3 / 5), ThumbnailOptions.ResizeThumbnail);
            if (thumb is null || thumb.Type == ThumbnailType.Icon) return; // only real photo thumbnails

            var decoder = await BitmapDecoder.CreateAsync(thumb);
            var sb = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            var pixels = new byte[4 * sb.PixelWidth * sb.PixelHeight];
            sb.CopyToBuffer(pixels.AsBuffer());
            ImageCompositor.OverlayPhoto(folderPixels, fw, fh, pixels, sb.PixelWidth, sb.PixelHeight);
        }
        catch { /* leave the plain folder icon */ }
    }

    /// <summary>The user-chosen thumbnail image for a folder, if one is set and still exists.</summary>
    private static string? ChosenThumbnail(string folderPath)
    {
        if (FolderThumbnails is null) return null;
        if (!FolderThumbnails.TryGetValue(folderPath, out var imgPath)) return null;
        return File.Exists(imgPath) && PhotoLibrary.IsSupported(imgPath) ? imgPath : null;
    }

    /// <summary>First supported image in a folder (bounded scan), or null.</summary>
    private static string? FindFirstImage(string folderPath)
    {
        try
        {
            var n = 0;
            foreach (var f in Directory.EnumerateFiles(folderPath))
            {
                if (PhotoLibrary.IsSupported(f)) return f;
                if (++n > 300) break; // don't scan huge non-image folders forever
            }
        }
        catch { /* access denied etc. */ }
        return null;
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
