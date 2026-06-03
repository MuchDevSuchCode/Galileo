using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PhotosPlus.Models;

namespace PhotosPlus.Services;

/// <summary>
/// Enumerates supported image files from a folder and applies persisted
/// hidden/favorite flags from <see cref="AppState"/>.
/// </summary>
public sealed class PhotoLibrary
{
    private readonly AppState _state;

    public static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe", ".png", ".gif", ".bmp", ".tif", ".tiff",
        ".webp", ".heic", ".heif", ".avif", ".ico",
        // common RAW (decode depends on installed Microsoft Raw Image Extension)
        ".cr2", ".cr3", ".nef", ".arw", ".dng", ".raf", ".orf", ".rw2"
    };

    public PhotoLibrary(AppState state) => _state = state;

    public static bool IsSupported(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path));

    /// <summary>Loads all supported images in a folder, newest first.</summary>
    public List<PhotoItem> Load(string folder)
    {
        var items = new List<PhotoItem>();
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(folder)
                .Where(IsSupported);
        }
        catch
        {
            return items;
        }

        foreach (var path in files)
        {
            var item = new PhotoItem(path)
            {
                IsFavorite = _state.FavoritePaths.Contains(path),
                IsHidden = _state.HiddenPaths.Contains(path)
            };
            items.Add(item);
        }

        return items
            .OrderByDescending(i => i.LastModifiedUtc)
            .ThenBy(i => i.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Builds items from an explicit set of file paths (e.g. a multi-file drop).</summary>
    public List<PhotoItem> LoadFiles(IEnumerable<string> paths)
    {
        return paths
            .Where(IsSupported)
            .Select(path => new PhotoItem(path)
            {
                IsFavorite = _state.FavoritePaths.Contains(path),
                IsHidden = _state.HiddenPaths.Contains(path)
            })
            .OrderBy(i => i.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
