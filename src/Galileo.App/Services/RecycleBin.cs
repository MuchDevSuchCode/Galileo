using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Galileo.Models;

namespace Galileo.Services;

public sealed class RecycleEntry
{
    public string Id { get; set; } = "";
    public string OriginalPath { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsFolder { get; set; }
    public long Size { get; set; }
    public DateTime DeletedUtc { get; set; }
}

/// <summary>
/// Galileo's own recycle bin (independent of the Windows Recycle Bin). Deleted items are moved into
/// %LocalAppData%\Galileo\RecycleBin\store and tracked in index.json so they can be restored;
/// emptying / permanent-delete uses <see cref="SecureWipe"/> with the user's chosen method.
/// </summary>
public sealed class RecycleBin
{
    public const string Location = "bin:::"; // sentinel used as the explorer's _currentFolder

    private static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Galileo", "RecycleBin");
    private static string StoreDir => Path.Combine(Root, "store");
    private static string IndexPath => Path.Combine(Root, "index.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly object _lock = new();

    /// <summary>Stored file path: GUID + original extension (so previews/open by extension still work).</summary>
    public string StorePathOf(RecycleEntry e) =>
        Path.Combine(StoreDir, e.Id + (e.IsFolder ? "" : Path.GetExtension(e.Name)));

    public int Count => Load().Count;

    public List<RecycleEntry> Load()
    {
        try { if (File.Exists(IndexPath)) return JsonSerializer.Deserialize<List<RecycleEntry>>(File.ReadAllText(IndexPath)) ?? new(); }
        catch { }
        return new();
    }

    private void Save(List<RecycleEntry> entries)
    {
        try { Directory.CreateDirectory(Root); File.WriteAllText(IndexPath, JsonSerializer.Serialize(entries, Json)); }
        catch { }
    }

    /// <summary>Moves a file/folder into the bin (recoverable). Returns false if the path is gone.</summary>
    public bool MoveToBin(string path)
    {
        lock (_lock)
        {
            var isDir = Directory.Exists(path);
            if (!isDir && !File.Exists(path)) return false;
            Directory.CreateDirectory(StoreDir);

            var name = Path.GetFileName(path.TrimEnd('\\', '/'));
            var id = Guid.NewGuid().ToString("N");
            var dest = Path.Combine(StoreDir, id + (isDir ? "" : Path.GetExtension(name)));
            long size = 0;
            try { size = isDir ? DirSize(path) : new FileInfo(path).Length; } catch { }

            MoveAny(path, dest, isDir);
            var list = Load();
            list.Add(new RecycleEntry { Id = id, OriginalPath = path, Name = name, IsFolder = isDir, Size = size, DeletedUtc = DateTime.UtcNow });
            Save(list);
            return true;
        }
    }

    /// <summary>The bin's contents as explorer items (newest first; store files keep their extension).</summary>
    public List<ExplorerItem> ListItems()
    {
        var items = new List<ExplorerItem>();
        foreach (var e in Load().OrderByDescending(x => x.DeletedUtc))
        {
            var kind = e.IsFolder ? ExplorerItemKind.Folder : ExplorerItemKind.File;
            var type = e.IsFolder ? "Folder" : TypeName(Path.GetExtension(e.Name));
            items.Add(new ExplorerItem(StorePathOf(e), kind, e.Size, e.DeletedUtc.ToLocalTime(), type, displayName: e.Name));
        }
        return items;
    }

    /// <summary>Restores an item to its original location (conflict-renamed). Returns the restored path.</summary>
    public bool Restore(string storePath, out string restoredTo)
    {
        restoredTo = "";
        lock (_lock)
        {
            var list = Load();
            var e = list.FirstOrDefault(x => string.Equals(StorePathOf(x), storePath, StringComparison.OrdinalIgnoreCase));
            if (e is null) return false;
            try
            {
                var dest = UniquePath(e.OriginalPath, e.IsFolder);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                MoveAny(storePath, dest, e.IsFolder);
                restoredTo = dest;
            }
            catch { return false; }
            list.Remove(e);
            Save(list);
            return true;
        }
    }

    /// <summary>Permanently removes one entry, secure-wiping its bytes first.</summary>
    public async Task DeleteEntryAsync(string storePath, WipeMethod method)
    {
        RecycleEntry? e;
        lock (_lock) { e = Load().FirstOrDefault(x => string.Equals(StorePathOf(x), storePath, StringComparison.OrdinalIgnoreCase)); }
        if (e is null) return;
        await SecureWipe.WipePathAsync(storePath, method);
        lock (_lock) { var list = Load(); list.RemoveAll(x => x.Id == e.Id); Save(list); }
    }

    /// <summary>Empties the bin, secure-wiping every item with the chosen method.</summary>
    public async Task EmptyAsync(WipeMethod method, IProgress<string>? progress = null)
    {
        List<RecycleEntry> list;
        lock (_lock) { list = Load(); }
        foreach (var e in list) await SecureWipe.WipePathAsync(StorePathOf(e), method, progress);
        lock (_lock)
        {
            Save(new List<RecycleEntry>());
            try { foreach (var f in Directory.EnumerateFileSystemEntries(StoreDir)) TryRemove(f); } catch { }
        }
    }

    // ---- helpers ----

    private static void TryRemove(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); else File.Delete(path); } catch { }
    }

    private static void MoveAny(string src, string dest, bool isDir)
    {
        try { if (isDir) Directory.Move(src, dest); else File.Move(src, dest); return; }
        catch (IOException) { /* cross-volume → copy + delete */ }
        if (isDir) { CopyDir(src, dest); Directory.Delete(src, true); }
        else { File.Copy(src, dest, overwrite: true); File.Delete(src); }
    }

    private static void CopyDir(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), overwrite: true);
        foreach (var d in Directory.GetDirectories(src)) CopyDir(d, Path.Combine(dest, Path.GetFileName(d)));
    }

    private static long DirSize(string dir)
    {
        long total = 0;
        try { foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) { try { total += new FileInfo(f).Length; } catch { } } }
        catch { }
        return total;
    }

    private static string UniquePath(string path, bool isDir)
    {
        if (isDir ? !Directory.Exists(path) : !File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = isDir ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path);
        var ext = isDir ? "" : Path.GetExtension(path);
        for (var i = 2; i < 10000; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (isDir ? !Directory.Exists(candidate) : !File.Exists(candidate)) return candidate;
        }
        return path;
    }

    private static string TypeName(string ext) =>
        string.IsNullOrEmpty(ext) ? "File" : $"{ext.TrimStart('.').ToUpperInvariant()} File";
}
