using System;
using System.IO;
using System.IO.Compression;
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
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Galileo", ".zip");

    public static bool IsArchive(string path) =>
        string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase);

    /// <summary>Extracts a .zip to a fresh temp folder and returns its path.</summary>
    public static async Task<string> ExtractToTempAsync(string zipPath)
    {
        var dest = Path.Combine(ZipTempRoot, Guid.NewGuid().ToString("N"));
        await ExtractToFolderAsync(zipPath, dest);
        return dest;
    }

    /// <summary>Extracts a .zip into <paramref name="destDir"/> (created if needed). Throws a clear
    /// message on corrupt or password-protected archives.</summary>
    public static async Task ExtractToFolderAsync(string zipPath, string destDir)
    {
        await Task.Run(() =>
        {
            Directory.CreateDirectory(destDir);
            try
            {
                ZipFile.ExtractToDirectory(zipPath, destDir, overwriteFiles: true);
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
        });
    }

    /// <summary>Securely-not-needed plain cleanup of leftover extracted archives (crash recovery).</summary>
    public static void WipeOrphans()
    {
        if (!Directory.Exists(ZipTempRoot)) return;
        try { Directory.Delete(ZipTempRoot, recursive: true); } catch { /* ignore */ }
    }
}
