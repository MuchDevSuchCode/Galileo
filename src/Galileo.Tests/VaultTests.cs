using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Galileo.Services;
using Xunit;

namespace Galileo.Tests;

public class VaultTests
{
    private const string Pass = "correct horse battery staple";
    private readonly string _src;

    public VaultTests()
    {
        _src = Path.Combine(Path.GetTempPath(), "galileo-vault-src-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_src);
    }

    private async Task<Vault> CreateWithFilesAsync(params (string rel, string content)[] files)
    {
        Directory.CreateDirectory(VaultManager.VaultsRoot);
        var v = Vault.Create(VaultManager.VaultsRoot, "test", Pass); // returns unlocked
        var paths = new System.Collections.Generic.List<string>();
        foreach (var (rel, content) in files)
        {
            var p = Path.Combine(_src, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, content);
            paths.Add(p);
        }
        if (paths.Count > 0) await v.ImportPathsAsync(paths, deleteOriginals: false);
        await v.LockAsync();
        return v;
    }

    [Fact]
    public async Task LockUnlock_RoundTripsContent()
    {
        var v = await CreateWithFilesAsync(("a.txt", "alpha"), ("b.txt", "beta"));
        await v.UnlockWithPassphraseAsync(Pass);
        try
        {
            Assert.Equal("alpha", File.ReadAllText(Path.Combine(v.WorkingDir!, "a.txt")));
            Assert.Equal("beta", File.ReadAllText(Path.Combine(v.WorkingDir!, "b.txt")));
        }
        finally { await v.LockAsync(); }
    }

    [Fact]
    public async Task EmptyDirectories_SurviveLockUnlock()
    {
        Directory.CreateDirectory(VaultManager.VaultsRoot);
        var v = Vault.Create(VaultManager.VaultsRoot, "dirs", Pass);
        var folder = Path.Combine(_src, "album");
        Directory.CreateDirectory(Path.Combine(folder, "empty-sub"));
        File.WriteAllText(Path.Combine(folder, "pic.txt"), "x");
        await v.ImportPathsAsync(new[] { folder }, deleteOriginals: false);
        await v.LockAsync();

        await v.UnlockWithPassphraseAsync(Pass);
        try
        {
            Assert.True(Directory.Exists(Path.Combine(v.WorkingDir!, "album", "empty-sub")),
                "an empty imported subfolder must still exist after lock/unlock");
        }
        finally { await v.LockAsync(); }
    }

    [Fact]
    public async Task DeletingEverything_StaysDeleted_WhenExplicit()
    {
        var v = await CreateWithFilesAsync(("only.txt", "data"));
        await v.UnlockWithPassphraseAsync(Pass);
        File.Delete(Path.Combine(v.WorkingDir!, "only.txt"));
        v.NoteExplicitDeletion();
        await v.LockAsync();

        await v.UnlockWithPassphraseAsync(Pass);
        try
        {
            Assert.False(File.Exists(Path.Combine(v.WorkingDir ?? "", "only.txt")),
                "an explicitly deleted last file must not resurrect on the next unlock");
        }
        finally { await v.LockAsync(); }
    }

    [Fact]
    public async Task TransientlyEmptyWorkingFolder_DoesNotWipeTheVault()
    {
        var v = await CreateWithFilesAsync(("keep.txt", "keep me"));
        await v.UnlockWithPassphraseAsync(Pass);
        // Simulate the transient failure: the working folder loses its files with NO explicit
        // deletion recorded — the commit must keep the previous encrypted generation.
        File.Delete(Path.Combine(v.WorkingDir!, "keep.txt"));
        await v.LockAsync();

        await v.UnlockWithPassphraseAsync(Pass);
        try { Assert.Equal("keep me", File.ReadAllText(Path.Combine(v.WorkingDir!, "keep.txt"))); }
        finally { await v.LockAsync(); }
    }

    [Fact]
    public async Task CorruptIndex_IsNotAWrongPassphrase_AndNeverTriggersWipe()
    {
        var v = await CreateWithFilesAsync(("f.txt", "x"));
        var indexPath = Path.Combine(v.Root, "index.enc");
        var bytes = File.ReadAllBytes(indexPath);
        bytes[^1] ^= 0xFF; // corrupt the authenticated ciphertext
        File.WriteAllBytes(indexPath, bytes);

        var manager = new VaultManager();
        var reloaded = Vault.Load(v.Root);
        // Wipe-on-failure armed at 1 attempt: if the corrupt index were miscounted as a wrong
        // passphrase, this call would DESTROY the vault.
        await Assert.ThrowsAsync<InvalidDataException>(
            () => manager.UnlockWithPassphraseAsync(reloaded, Pass, wipeEnabled: true, wipeAfter: 1));
        Assert.True(Directory.Exists(v.Root), "vault must survive a corrupt-index unlock attempt");
        Assert.Equal(0, reloaded.FailedAttempts);
    }

    [Fact]
    public async Task MissingBlob_IsReported_AndItsEntryIsPreserved()
    {
        var v = await CreateWithFilesAsync(("one.txt", "1"), ("two.txt", "2"));
        var blob = Directory.GetFiles(Path.Combine(v.Root, "blobs"), "*.blob").First();
        File.Delete(blob);

        await v.UnlockWithPassphraseAsync(Pass);
        Assert.Single(v.MissingFilesOnUnlock);
        await v.LockAsync(); // must NOT silently drop the damaged entry from the index

        await v.UnlockWithPassphraseAsync(Pass);
        try { Assert.Single(v.MissingFilesOnUnlock); }
        finally { await v.LockAsync(); }
    }

    [Fact]
    public async Task SameSizeEdit_WithRestoredTimestamp_IsCommittedAtLock()
    {
        var v = await CreateWithFilesAsync(("t.txt", "AAAA"));
        await v.UnlockWithPassphraseAsync(Pass);
        var f = Path.Combine(v.WorkingDir!, "t.txt");
        var mtime = File.GetLastWriteTimeUtc(f);
        File.WriteAllText(f, "BBBB");           // same length…
        File.SetLastWriteTimeUtc(f, mtime);     // …and the original timestamp restored
        await v.LockAsync();                    // size+mtime says "unchanged"; the hash must not

        await v.UnlockWithPassphraseAsync(Pass);
        try { Assert.Equal("BBBB", File.ReadAllText(Path.Combine(v.WorkingDir!, "t.txt"))); }
        finally { await v.LockAsync(); }
    }
}
