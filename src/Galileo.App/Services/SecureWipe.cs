using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace Galileo.Services;

public enum WipeMethod { None, Zero, Random, Dod3, Dod7, Gutmann35 }

/// <summary>
/// Best-effort secure file wipe: overwrites a file's bytes with one or more passes before deleting,
/// then renames it to defeat name-based recovery. Methods mirror fileshredder.org (Zero / Random /
/// DoD 5220.22-M / DoD ECE / Gutmann 35-pass). NOTE: on SSDs/NVMe and copy-on-write filesystems
/// overwriting is not a guarantee (wear-leveling/TRIM) — this is best-effort, not forensic.
/// </summary>
public static class SecureWipe
{
    private const int Chunk = 1 << 20;

    public static WipeMethod Parse(string? s) => s switch
    {
        "Zero" => WipeMethod.Zero,
        "Random" => WipeMethod.Random,
        "Dod3" or "DoD3" => WipeMethod.Dod3,
        "Dod7" or "DoD7" => WipeMethod.Dod7,
        "Gutmann35" or "Gutmann" => WipeMethod.Gutmann35,
        _ => WipeMethod.None,
    };

    /// <summary>Overwrites + deletes a file, or recursively a folder, on a background thread.</summary>
    public static Task WipePathAsync(string path, WipeMethod method, IProgress<string>? progress = null) => Task.Run(() =>
    {
        if (Directory.Exists(path))
        {
            foreach (var f in SafeFiles(path)) WipeFile(f, method, progress);
            try { Directory.Delete(path, recursive: true); } catch { }
        }
        else
        {
            WipeFile(path, method, progress);
        }
    });

    private static IEnumerable<string> SafeFiles(string dir)
    {
        try { return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToList(); }
        catch { return Enumerable.Empty<string>(); }
    }

    private static void WipeFile(string path, WipeMethod method, IProgress<string>? progress)
    {
        try
        {
            if (!File.Exists(path)) return;
            var fi = new FileInfo(path);
            if (fi.Attributes.HasFlag(FileAttributes.ReadOnly)) fi.Attributes = FileAttributes.Normal;
            long len = fi.Length;

            if (method != WipeMethod.None && len > 0)
            {
                var passes = Passes(method);
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
                var buf = new byte[(int)Math.Min(len, Chunk)];
                for (var p = 0; p < passes.Count; p++)
                {
                    progress?.Report($"Wiping {fi.Name} — pass {p + 1}/{passes.Count}");
                    fs.Position = 0;
                    long written = 0;
                    while (written < len)
                    {
                        var n = (int)Math.Min(buf.Length, len - written);
                        Fill(buf, n, passes[p], written);
                        fs.Write(buf, 0, n);
                        written += n;
                    }
                    fs.Flush(flushToDisk: true);
                }
                fs.SetLength(0);
                fs.Flush(flushToDisk: true);
            }

            // Rename to a random name before deleting (defeats name-based recovery of the directory entry).
            var scrambled = Path.Combine(Path.GetDirectoryName(path)!, Guid.NewGuid().ToString("N"));
            try { File.Move(path, scrambled); File.Delete(scrambled); }
            catch { File.Delete(path); }
        }
        catch
        {
            try { File.Delete(path); } catch { /* give up */ }
        }
    }

    // pattern == null → cryptographic random; otherwise tile the pattern bytes (phase-continuous across chunks).
    private static void Fill(byte[] buf, int count, byte[]? pattern, long absoluteOffset)
    {
        if (pattern is null) { RandomNumberGenerator.Fill(buf.AsSpan(0, count)); return; }
        for (var i = 0; i < count; i++) buf[i] = pattern[(int)((absoluteOffset + i) % pattern.Length)];
    }

    private static List<byte[]?> Passes(WipeMethod m) => m switch
    {
        WipeMethod.Zero => new() { new byte[] { 0x00 } },
        WipeMethod.Random => new() { null },
        WipeMethod.Dod3 => new() { new byte[] { 0x00 }, new byte[] { 0xFF }, null },
        WipeMethod.Dod7 => new() { null, new byte[] { 0x00 }, new byte[] { 0xFF }, null, new byte[] { 0x00 }, new byte[] { 0xFF }, null },
        WipeMethod.Gutmann35 => Gutmann(),
        _ => new(),
    };

    // 4 random + the 27 canonical Gutmann patterns + 4 random = 35 passes.
    private static List<byte[]?> Gutmann()
    {
        var list = new List<byte[]?>();
        for (var i = 0; i < 4; i++) list.Add(null);
        byte[][] patterns =
        {
            new byte[]{0x55}, new byte[]{0xAA},
            new byte[]{0x92,0x49,0x24}, new byte[]{0x49,0x24,0x92}, new byte[]{0x24,0x92,0x49},
            new byte[]{0x00}, new byte[]{0x11}, new byte[]{0x22}, new byte[]{0x33}, new byte[]{0x44},
            new byte[]{0x55}, new byte[]{0x66}, new byte[]{0x77}, new byte[]{0x88}, new byte[]{0x99},
            new byte[]{0xAA}, new byte[]{0xBB}, new byte[]{0xCC}, new byte[]{0xDD}, new byte[]{0xEE}, new byte[]{0xFF},
            new byte[]{0x92,0x49,0x24}, new byte[]{0x49,0x24,0x92}, new byte[]{0x24,0x92,0x49},
            new byte[]{0x6D,0xB6,0xDB}, new byte[]{0xB6,0xDB,0x6D}, new byte[]{0xDB,0x6D,0xB6},
        };
        foreach (var p in patterns) list.Add(p);
        for (var i = 0; i < 4; i++) list.Add(null);
        return list;
    }
}
