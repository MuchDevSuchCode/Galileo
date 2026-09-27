using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;

namespace Galileo.Services;

/// <summary>
/// Lets the explorer browse archives like folders: a .zip is extracted to a temp directory and
/// navigated into as an ordinary folder (so all explorer features work). Read-only — edits to the
/// extracted copy are not written back into the archive. Temp copies are wiped at startup.
/// </summary>
public static class ArchiveService
{
    public static string ZipTempRoot =>
        Path.Combine(AppPaths.Root, ".zip");

    public static bool IsArchive(string path) =>
        string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase);

    // Caps so a malicious/huge archive can't fill the disk or hang the browse. Generous for a photo/file
    // browser's zip-preview use; a real archive tool would raise them.
    private const long MaxTotalBytes = 8L * 1024 * 1024 * 1024; // 8 GB of decompressed output
    private const int MaxEntries = 200_000;

    /// <summary>Extracts a .zip to a fresh temp folder and returns its path.</summary>
    public static async Task<string> ExtractToTempAsync(string zipPath, CancellationToken ct = default)
    {
        var dest = Path.Combine(ZipTempRoot, Guid.NewGuid().ToString("N"));
        await ExtractToFolderAsync(zipPath, dest, ct);
        return dest;
    }

    /// <summary>Extracts a .zip into <paramref name="destDir"/> (created if needed), entry by entry so it
    /// can be cancelled, is capped against zip-bombs (total decompressed bytes + entry count), and refuses
    /// any entry whose path escapes the destination (zip-slip). Throws a clear message on corrupt or
    /// password-protected archives.</summary>
    public static async Task ExtractToFolderAsync(string zipPath, string destDir, CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            Directory.CreateDirectory(destDir);
            var root = Path.GetFullPath(destDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var buffer = new byte[81920];
            long total = 0;
            try
            {
                using var zip = ZipFile.OpenRead(zipPath);
                if (zip.Entries.Count > MaxEntries)
                    throw new InvalidOperationException($"Archive has too many entries ({zip.Entries.Count:N0}) to open safely.");
                foreach (var entry in zip.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    var target = Path.GetFullPath(Path.Combine(destDir, entry.FullName));
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Archive contains an entry that would extract outside the target folder.");
                    if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) { Directory.CreateDirectory(target); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    using var es = entry.Open();
                    using var fs = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
                    int read;
                    while ((read = es.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        total += read;
                        if (total > MaxTotalBytes)
                            throw new InvalidOperationException("Archive is too large to extract safely.");
                        fs.Write(buffer, 0, read);
                    }
                }
            }
            catch (InvalidDataException)
            {
                throw new InvalidDataException("This archive is corrupt or not a supported .zip.");
            }
            catch (NotSupportedException)
            {
                // Encrypted entries surface here on .NET's BCL zip reader.
                throw new NotSupportedException("Password-protected archives aren't supported.");
            }
        }, ct);
    }

    /// <summary>Securely-not-needed plain cleanup of leftover extracted archives (crash recovery).</summary>
    public static void WipeOrphans()
    {
        if (!Directory.Exists(ZipTempRoot)) return;
        try { Directory.Delete(ZipTempRoot, recursive: true); } catch { /* ignore */ }
    }
}
