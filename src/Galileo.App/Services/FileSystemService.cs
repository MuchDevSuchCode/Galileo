using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Galileo.Models;

namespace Galileo.Services;

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
                if (!d.IsReady) { items.Add(Drive(d.Name, d.Name.TrimEnd('\\'), 0, 0)); continue; }
                var label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? "Local Disk" : d.VolumeLabel;
                items.Add(Drive(d.Name, $"{label} ({d.Name.TrimEnd('\\')})", SafeTotal(d), SafeFree(d)));
            }
            catch { /* skip flaky drives */ }
        }
        return items;
    }

    /// <summary>Common user folders for the sidebar / home. Resolved through the Windows known-folder
    /// system (not guessed as fixed paths under the profile), so relocated/redirected Desktop and
    /// Downloads folders point where the user actually keeps them.</summary>
    public List<ExplorerItem> GetQuickAccess()
    {
        var paths = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            GetDownloadsPath(),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        };
        var items = new List<ExplorerItem>();
        foreach (var p in paths.Where(p => !string.IsNullOrEmpty(p) && Directory.Exists(p)).Distinct(StringComparer.OrdinalIgnoreCase))
            items.Add(new ExplorerItem(p!, ExplorerItemKind.Folder, 0, SafeWriteTime(p!), "Folder"));
        return items;
    }

    private static readonly Guid DownloadsFolderGuid = new("374DE290-123F-4565-9164-39C4925E467B");

    /// <summary>Downloads has no Environment.SpecialFolder value — resolve it via the shell's
    /// known-folder API (honors folder redirection); falls back to the profile default.</summary>
    public static string GetDownloadsPath()
    {
        try
        {
            var hr = SHGetKnownFolderPath(DownloadsFolderGuid, 0, IntPtr.Zero, out var raw);
            if (hr == 0 && raw != IntPtr.Zero)
            {
                try { return System.Runtime.InteropServices.Marshal.PtrToStringUni(raw) ?? FallbackDownloads(); }
                finally { System.Runtime.InteropServices.Marshal.FreeCoTaskMem(raw); }
            }
        }
        catch { /* fall through */ }
        return FallbackDownloads();
    }

    private static string FallbackDownloads() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPStruct)] Guid rfid,
        uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

    /// <summary>Lists a directory: folders first, then files, alphabetically.</summary>
    public List<ExplorerItem> List(string path, bool showWindowsHidden, bool showAppHidden)
        => List(path, showWindowsHidden, showAppHidden, out _);

    /// <summary>Lists a directory, reporting WHY it could not be (fully) read — the UI must be able
    /// to distinguish "this folder is empty" from "this folder could not be opened".</summary>
    public List<ExplorerItem> List(string path, bool showWindowsHidden, bool showAppHidden, out string? error)
    {
        error = null;
        var folders = new List<ExplorerItem>();
        var files = new List<ExplorerItem>();
        try
        {
            var di = new DirectoryInfo(path);
            if (!di.Exists)
            {
                error = "The folder is unavailable or no longer exists.";
                return folders;
            }

            try
            {
                foreach (var d in di.EnumerateDirectories())
                {
                    if (IsWindowsHidden(d.Attributes) && !showWindowsHidden) continue;
                    var appHidden = _state.HiddenFolders.Contains(d.FullName);
                    if (appHidden && !showAppHidden) continue;
                    folders.Add(new ExplorerItem(d.FullName, ExplorerItemKind.Folder, 0, SafeWrite(d), "Folder")
                    {
                        IsAppHidden = appHidden
                    });
                }

                foreach (var f in di.EnumerateFiles())
                {
                    if (IsWindowsHidden(f.Attributes) && !showWindowsHidden) continue;
                    files.Add(new ExplorerItem(f.FullName, ExplorerItemKind.File, SafeLength(f), SafeWrite(f), TypeName(f.Extension)));
                }
            }
            catch (UnauthorizedAccessException) { error = "Access to this folder is denied."; }
            catch (IOException ex) { error = ex.Message; }
        }
        catch (Exception ex)
        {
            // Inaccessible directory — return whatever we managed to read, but say why.
            error = ex.Message;
        }

        folders.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        files.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        folders.AddRange(files);
        return folders;
    }

    /// <summary>Recursively finds items under <paramref name="root"/> whose name contains the query.
    /// Walks manually (not RecurseSubdirectories) so app-hidden folders can be PRUNED — their contents
    /// must never surface in results, or search would defeat the hidden-folder privacy feature. Honors
    /// the same hidden-item toggles as <see cref="List"/> and never descends into reparse points.</summary>
    public List<ExplorerItem> Search(string root, string query, bool showWindowsHidden, bool showAppHidden, int max = 4000)
        => Search(root, query, showWindowsHidden, showAppHidden, System.Threading.CancellationToken.None, out _, max);

    /// <summary>Cancellable search that also reports whether the result list was TRUNCATED at the
    /// cap — the UI must disclose an incomplete result set rather than show an ordinary count.</summary>
    public List<ExplorerItem> Search(string root, string query, bool showWindowsHidden, bool showAppHidden,
        System.Threading.CancellationToken ct, out bool truncated, int max = 4000)
    {
        var results = new List<ExplorerItem>();
        var hitCap = false;
        truncated = false;
        if (string.IsNullOrWhiteSpace(query)) return results;

        void Walk(DirectoryInfo dir)
        {
            if (results.Count >= max) { hitCap = true; return; }
            ct.ThrowIfCancellationRequested(); // superseded by newer keystrokes — stop the scan
            IEnumerable<FileSystemInfo> entries;
            try { entries = dir.EnumerateFileSystemInfos().ToList(); }
            catch (OperationCanceledException) { throw; }
            catch { return; } // access denied etc. — keep what we have
            foreach (var info in entries)
            {
                if (results.Count >= max) { hitCap = true; return; }
                ct.ThrowIfCancellationRequested();
                if (IsWindowsHidden(info.Attributes) && !showWindowsHidden) continue;
                if (info is DirectoryInfo d)
                {
                    var appHidden = _state.HiddenFolders.Contains(d.FullName);
                    if (appHidden && !showAppHidden) continue;                      // prune the whole branch
                    if ((d.Attributes & FileAttributes.ReparsePoint) != 0) continue; // junction cycles / foreign targets
                    if (d.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                        results.Add(new ExplorerItem(d.FullName, ExplorerItemKind.Folder, 0, SafeWrite(d), "Folder") { IsAppHidden = appHidden });
                    Walk(d);
                }
                else if (info is FileInfo f && f.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                    results.Add(new ExplorerItem(f.FullName, ExplorerItemKind.File, SafeLength(f), SafeWrite(f), TypeName(f.Extension)));
            }
        }
        try { Walk(new DirectoryInfo(root)); }
        catch (OperationCanceledException) { throw; } // cancellation must reach the caller
        catch { }
        truncated = hitCap;
        return results;
    }

    private static ExplorerItem Drive(string root, string name, long total, long free) =>
        new(root, ExplorerItemKind.Drive, 0, default, "Drive", name, totalBytes: total, freeBytes: free);

    private static long SafeTotal(DriveInfo d) { try { return d.TotalSize; } catch { return 0; } }
    private static long SafeFree(DriveInfo d) { try { return d.TotalFreeSpace; } catch { return 0; } }

    private static bool IsWindowsHidden(FileAttributes a) =>
        a.HasFlag(FileAttributes.Hidden) || a.HasFlag(FileAttributes.System);

    internal static string TypeName(string ext) =>
        string.IsNullOrEmpty(ext) ? "File" : $"{ext.TrimStart('.').ToUpperInvariant()} File";

    private static long SafeLength(FileInfo f) { try { return f.Length; } catch { return 0; } }
    private static DateTime SafeWrite(FileSystemInfo i) { try { return i.LastWriteTime; } catch { return default; } }
    private static DateTime SafeWriteTime(string p) { try { return Directory.GetLastWriteTime(p); } catch { return default; } }
}
