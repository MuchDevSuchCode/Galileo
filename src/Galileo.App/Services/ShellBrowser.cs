using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Galileo.Models;

namespace Galileo.Services;

/// <summary>Encodes a shell-namespace location (MTP / portable device) inside the explorer's
/// string-path model using a sentinel prefix, so navigation/history/tabs carry it unchanged.</summary>
public static class ShellLoc
{
    public const string Prefix = "shell:::";
    public static bool IsShell(string? path) => path is not null && path.StartsWith(Prefix, StringComparison.Ordinal);
    public static string Wrap(string parsingName) => Prefix + parsingName;
    public static string Unwrap(string loc) => loc.Substring(Prefix.Length);
}

/// <summary>
/// Browses the Windows shell namespace for portable/MTP devices (phones, cameras) which have no
/// filesystem paths — items are addressed by shell parsing names. Read side (enumerate / list /
/// parent / name); thumbnails reuse <see cref="ShellImaging"/> with the parsing name.
/// </summary>
public sealed class ShellBrowser
{
    /// <summary>Portable devices under "This PC" (anything that's a folder/storage but NOT on the filesystem).</summary>
    public List<(string Name, string ParsingName)> GetPortableDevices()
    {
        var list = new List<(string, string)>();
        IShellItem? computer = null;
        try
        {
            var fid = FOLDERID_ComputerFolder;
            var iid = IID_IShellItem;
            if (SHGetKnownFolderItem(ref fid, 0, IntPtr.Zero, ref iid, out computer) != 0 || computer is null) return list;

            foreach (var child in EnumChildren(computer))
            {
                try
                {
                    child.GetAttributes(SFGAO_FILESYSTEM | SFGAO_FOLDER | SFGAO_STORAGE, out var a);
                    var isFilesystem = (a & SFGAO_FILESYSTEM) != 0;
                    var isFolderish = (a & (SFGAO_FOLDER | SFGAO_STORAGE)) != 0;
                    if (!isFilesystem && isFolderish)
                        list.Add((GetName(child, SIGDN.NORMALDISPLAY), GetName(child, SIGDN.DESKTOPABSOLUTEPARSING)));
                }
                finally { Marshal.ReleaseComObject(child); }
            }
        }
        catch { /* no devices / shell error */ }
        finally { if (computer is not null) Marshal.ReleaseComObject(computer); }
        return list;
    }

    /// <summary>Children of a shell folder (by parsing name) as ExplorerItems carrying their ShellId.
    /// Size/date are left blank for v1 (property-store reads are deferred).</summary>
    public List<ExplorerItem> List(string parsingName)
    {
        var folders = new List<ExplorerItem>();
        var files = new List<ExplorerItem>();
        IShellItem? folder = null;
        try
        {
            var iid = IID_IShellItem;
            if (SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out folder) != 0 || folder is null)
                return folders;

            foreach (var child in EnumChildren(folder))
            {
                try
                {
                    child.GetAttributes(SFGAO_FOLDER | SFGAO_STREAM, out var a);
                    var isFolder = (a & SFGAO_FOLDER) != 0;
                    var name = GetName(child, SIGDN.PARENTRELATIVEPARSING); // includes extension
                    var pn = GetName(child, SIGDN.DESKTOPABSOLUTEPARSING);
                    if (string.IsNullOrEmpty(pn)) continue;
                    if (string.IsNullOrEmpty(name)) name = GetName(child, SIGDN.NORMALDISPLAY);

                    var kind = isFolder ? ExplorerItemKind.Folder : ExplorerItemKind.File;
                    var type = isFolder ? "Folder" : TypeName(System.IO.Path.GetExtension(name));
                    var item = new ExplorerItem(pn, kind, 0, default, type, displayName: name, shellId: pn);
                    (isFolder ? folders : files).Add(item);
                }
                finally { Marshal.ReleaseComObject(child); }
            }
        }
        catch { /* unreadable device folder */ }
        finally { if (folder is not null) Marshal.ReleaseComObject(folder); }

        folders.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        files.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        folders.AddRange(files);
        return folders;
    }

    /// <summary>Parent parsing name, or null when the parent is "This PC" (so Up returns to the home view).</summary>
    public string? GetParentParsingName(string parsingName)
    {
        IShellItem? item = null, parent = null, computer = null;
        try
        {
            var iid = IID_IShellItem;
            if (SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out item) != 0 || item is null) return null;
            if (item.GetParent(out parent) != 0 || parent is null) return null;
            var parentPn = GetName(parent, SIGDN.DESKTOPABSOLUTEPARSING);

            var fid = FOLDERID_ComputerFolder;
            var ciid = IID_IShellItem;
            if (SHGetKnownFolderItem(ref fid, 0, IntPtr.Zero, ref ciid, out computer) == 0 && computer is not null
                && string.Equals(parentPn, GetName(computer, SIGDN.DESKTOPABSOLUTEPARSING), StringComparison.OrdinalIgnoreCase))
                return null;

            return string.IsNullOrEmpty(parentPn) ? null : parentPn;
        }
        catch { return null; }
        finally
        {
            if (item is not null) Marshal.ReleaseComObject(item);
            if (parent is not null) Marshal.ReleaseComObject(parent);
            if (computer is not null) Marshal.ReleaseComObject(computer);
        }
    }

    public string DisplayName(string parsingName)
    {
        IShellItem? item = null;
        try
        {
            var iid = IID_IShellItem;
            if (SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out item) != 0 || item is null) return "Device";
            var n = GetName(item, SIGDN.NORMALDISPLAY);
            return string.IsNullOrEmpty(n) ? "Device" : n;
        }
        catch { return "Device"; }
        finally { if (item is not null) Marshal.ReleaseComObject(item); }
    }

    /// <summary>Temp area for device files streamed out for viewing.</summary>
    public static string TempRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Galileo", ".mtp");

    /// <summary>Streams a device file (by parsing name) to a temp copy and returns its path, so the
    /// existing path-based image/video/default openers can use it.</summary>
    public Task<string> CopyToTempAsync(string parsingName, string fileName) => Task.Run(() =>
    {
        IShellItem? item = null;
        try
        {
            var iid = IID_IShellItem;
            if (SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out item) != 0 || item is null)
                throw new IOException("Couldn't open the device item.");

            var bhid = BHID_Stream;
            var siid = IID_IStream;
            if (item.BindToHandler(IntPtr.Zero, ref bhid, ref siid, out var ptr) != 0 || ptr == IntPtr.Zero)
                throw new IOException("Couldn't read the device file.");

            var stm = (System.Runtime.InteropServices.ComTypes.IStream)Marshal.GetObjectForIUnknown(ptr);
            Marshal.Release(ptr);
            try
            {
                Directory.CreateDirectory(TempRoot);
                var dest = Path.Combine(TempRoot, Guid.NewGuid().ToString("N") + "_" + Sanitize(fileName));
                using var fs = File.Create(dest);
                var buf = new byte[1 << 20];
                var read = Marshal.AllocHGlobal(sizeof(int));
                try
                {
                    while (true)
                    {
                        stm.Read(buf, buf.Length, read);
                        var n = Marshal.ReadInt32(read);
                        if (n <= 0) break;
                        fs.Write(buf, 0, n);
                    }
                }
                finally { Marshal.FreeHGlobal(read); }
                return dest;
            }
            finally { Marshal.ReleaseComObject(stm); }
        }
        finally { if (item is not null) Marshal.ReleaseComObject(item); }
    });

    /// <summary>Removes the device temp area (call at launch — mirrors the zip temp cleanup).</summary>
    public void WipeTemp()
    {
        try { if (Directory.Exists(TempRoot)) Directory.Delete(TempRoot, recursive: true); } catch { }
    }

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return string.IsNullOrEmpty(name) ? "file" : name;
    }

    // ---- helpers ----

    private static IEnumerable<IShellItem> EnumChildren(IShellItem folder)
    {
        var bhid = BHID_EnumItems;
        var iid = IID_IEnumShellItems;
        if (folder.BindToHandler(IntPtr.Zero, ref bhid, ref iid, out var ptr) != 0 || ptr == IntPtr.Zero)
            yield break;

        var en = (IEnumShellItems)Marshal.GetObjectForIUnknown(ptr);
        Marshal.Release(ptr);
        try
        {
            while (en.Next(1, out var child, out var fetched) == 0 && fetched == 1 && child is not null)
                yield return child;
        }
        finally { Marshal.ReleaseComObject(en); }
    }

    private static string GetName(IShellItem item, SIGDN kind)
    {
        if (item.GetDisplayName(kind, out var p) != 0 || p == IntPtr.Zero) return "";
        try { return Marshal.PtrToStringUni(p) ?? ""; }
        finally { Marshal.FreeCoTaskMem(p); }
    }

    private static string TypeName(string ext) =>
        string.IsNullOrEmpty(ext) ? "File" : $"{ext.TrimStart('.').ToUpperInvariant()} File";

    // ---- COM ----
    private static Guid IID_IShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");
    private static Guid IID_IEnumShellItems = new("70629033-e363-4a28-a567-0db78006e6d7");
    private static Guid BHID_EnumItems = new("94f60519-2850-4924-aa5a-d15e84868039");
    private static Guid BHID_Stream = new("1CEBB3AB-7C10-499a-A417-92CA16C4CB83");
    private static Guid IID_IStream = new("0000000c-0000-0000-C000-000000000046");
    private static Guid FOLDERID_ComputerFolder = new("0AC0837C-BBF8-452A-850D-79D08E667CA7");

    private const uint SFGAO_STORAGE = 0x00080000, SFGAO_STREAM = 0x00400000,
                       SFGAO_FOLDER = 0x20000000, SFGAO_FILESYSTEM = 0x40000000;

    private enum SIGDN : uint
    {
        NORMALDISPLAY = 0x00000000,
        PARENTRELATIVEPARSING = 0x80018001,
        DESKTOPABSOLUTEPARSING = 0x80028000,
    }

    [DllImport("shell32.dll")]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderItem(ref Guid rfid, int flags, IntPtr hToken, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetParent(out IShellItem ppsi);
        [PreserveSig] int GetDisplayName(SIGDN sigdnName, out IntPtr ppszName);
        [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgao);
        [PreserveSig] int Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [ComImport, Guid("70629033-e363-4a28-a567-0db78006e6d7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumShellItems
    {
        [PreserveSig] int Next(uint celt, out IShellItem rgelt, out uint pceltFetched);
        [PreserveSig] int Skip(uint celt);
        [PreserveSig] int Reset();
        [PreserveSig] int Clone(out IEnumShellItems ppenum);
    }
}
