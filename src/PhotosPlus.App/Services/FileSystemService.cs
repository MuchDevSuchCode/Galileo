using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PhotosPlus.Models;

namespace PhotosPlus.Services;

/// <summary>Enumerates the filesystem for the explorer: drives, quick-access roots and folders.</summary>
public sealed class FileSystemService
{
    private readonly AppState _state;
    public FileSystemService(AppState state) => _state = state;

    /// <summary>Drives shown under "This PC".</summary>
    public List<ExplorerItem> GetDrives()
    {
        var items = new List<ExplorerItem>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady) { items.Add(Drive(d.Name, d.Name.TrimEnd('\\'))); continue; }
                var label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? "Local Disk" : d.VolumeLabel;
                items.Add(Drive(d.Name, $"{label} ({d.Name.TrimEnd('\\')})"));
            }
            catch { /* skip flaky drives */ }
        }
        return items;
    }

    /// <summary>Common user folders for the sidebar / home.</summary>
    public List<ExplorerItem> GetQuickAccess()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var paths = new[]
        {
            profile,
            Path.Combine(profile, "Desktop"),
            Path.Combine(profile, "Downloads"),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        };
        var items = new List<ExplorerItem>();
        foreach (var p in paths.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
            items.Add(new ExplorerItem(p, ExplorerItemKind.Folder, 0, SafeWriteTime(p), "Folder"));
        return items;
    }

    /// <summary>Lists a directory: folders first, then files, alphabetically.</summary>
    public List<ExplorerItem> List(string path, bool showWindowsHidden, bool showAppHidden)
    {
        var folders = new List<ExplorerItem>();
        var files = new List<ExplorerItem>();
        try
        {
            var di = new DirectoryInfo(path);

            foreach (var d in SafeEnumerate(() => di.EnumerateDirectories()))
            {
                if (IsWindowsHidden(d.Attributes) && !showWindowsHidden) continue;
                var appHidden = _state.HiddenFolders.Contains(d.FullName);
                if (appHidden && !showAppHidden) continue;
                folders.Add(new ExplorerItem(d.FullName, ExplorerItemKind.Folder, 0, SafeWrite(d), "Folder")
                {
                    IsAppHidden = appHidden
                });
            }

            foreach (var f in SafeEnumerate(() => di.EnumerateFiles()))
            {
                if (IsWindowsHidden(f.Attributes) && !showWindowsHidden) continue;
                files.Add(new ExplorerItem(f.FullName, ExplorerItemKind.File, SafeLength(f), SafeWrite(f), TypeName(f.Extension)));
            }
        }
        catch
        {
            // Inaccessible directory — return whatever we managed to read.
        }

        folders.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        files.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        folders.AddRange(files);
        return folders;
    }

    private static ExplorerItem Drive(string root, string name) =>
        new(root, ExplorerItemKind.Drive, 0, default, "Drive", name);

    private static bool IsWindowsHidden(FileAttributes a) =>
        a.HasFlag(FileAttributes.Hidden) || a.HasFlag(FileAttributes.System);

    private static string TypeName(string ext) =>
        string.IsNullOrEmpty(ext) ? "File" : $"{ext.TrimStart('.').ToUpperInvariant()} File";

    private static IEnumerable<T> SafeEnumerate<T>(Func<IEnumerable<T>> source)
    {
        try { return source().ToList(); }
        catch { return Enumerable.Empty<T>(); }
    }

    private static long SafeLength(FileInfo f) { try { return f.Length; } catch { return 0; } }
    private static DateTime SafeWrite(FileSystemInfo i) { try { return i.LastWriteTime; } catch { return default; } }
    private static DateTime SafeWriteTime(string p) { try { return Directory.GetLastWriteTime(p); } catch { return default; } }
}
