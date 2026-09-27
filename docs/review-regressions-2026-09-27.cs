using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Galileo.Services;
using Xunit;

namespace Galileo.Tests;

public sealed class ReviewRegressionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "galileo-review-" + Guid.NewGuid().ToString("N"));
    private string FileAt(string relative, string text)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }
    private sealed class Callback : IProgress<TransferProgress>
    {
        public Action<TransferProgress> Action = _ => { };
        public void Report(TransferProgress p) => Action(p);
    }
    private static Task<ConflictChoice> Replace(ConflictInfo _) => Task.FromResult(new ConflictChoice { Action = ConflictAction.Overwrite });

    [Fact]
    public async Task CancelMoveAfterReplacement_PreservesOldDestination()
    {
        var first = FileAt("src/a.txt", "NEW");
        var second = FileAt("src/b.txt", "SECOND");
        var dest = FileAt("dest/a.txt", "OLD");
        FileAt("dest/b.txt", "OLD SECOND");
        using var transfer = new FileTransfer();
        var progress = new Callback { Action = p => { if (p.FilesDone == 1) transfer.Cancel(); } };
        var result = await transfer.RunAsync(Path.GetDirectoryName(dest)!, new[] { first, second }, true, progress, Replace);
        Assert.True(result.Canceled);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(dest), "Cancel deleted the pre-existing replacement destination");
        Assert.Equal("OLD", File.ReadAllText(dest));
    }

    [Fact]
    public async Task MoveTwoSameNamedSources_PreservesBothContents()
    {
        var first = FileAt("one/a.txt", "FIRST");
        var second = FileAt("two/a.txt", "SECOND");
        var dest = FileAt("dest/a.txt", "OLD");
        using var transfer = new FileTransfer();
        var result = await transfer.RunAsync(Path.GetDirectoryName(dest)!, new[] { first, second }, true, null, Replace);
        Assert.Equal(0, result.Errors);
        var contents = Directory.GetFiles(root, "*.txt", SearchOption.AllDirectories).Select(File.ReadAllText).ToArray();
        Assert.Contains("FIRST", contents);
        Assert.Contains("SECOND", contents);
    }

    [Fact]
    public async Task CancelMoveAfterDestinationAppears_DoesNotDeleteUnrelatedFile()
    {
        var first = FileAt("src/a.txt", "FIRST");
        var second = FileAt("src/b.txt", "SECOND");
        var destDir = Path.Combine(root, "dest");
        Directory.CreateDirectory(destDir);
        var dest = Path.Combine(destDir, "a.txt");
        // A read handle allows streamed copy but prevents the initial same-volume rename.
        using var holdFirst = new FileStream(first, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var holdSecond = new FileStream(second, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var transfer = new FileTransfer();
        var progress = new Callback { Action = p =>
        {
            if (p.BytesDone > 0 && p.FilesDone == 0 && !File.Exists(dest)) File.WriteAllText(dest, "UNRELATED");
            if (p.FilesDone == 1) transfer.Cancel();
        }};
        var result = await transfer.RunAsync(destDir, new[] { first, second }, true, progress);
        Assert.True(result.Canceled);
        Assert.True(File.Exists(dest), "Rollback deleted the unrelated file that appeared during copy");
        Assert.Equal("UNRELATED", File.ReadAllText(dest));
    }

    [Fact]
    public async Task SecondVaultOwner_DoesNotEraseFirstOwnersUnsavedEdits()
    {
        const string pass = "review-only passphrase";
        var source = FileAt("vault-source/a.txt", "ORIGINAL");
        var first = Vault.Create(VaultManager.VaultsRoot, "review", pass);
        await first.ImportPathsAsync(new[] { source }, deleteOriginals: false);
        await first.LockAsync();
        await first.UnlockWithPassphraseAsync(pass);
        var working = Path.Combine(first.WorkingDir!, "a.txt");
        File.WriteAllText(working, "UNSAVED EDIT");
        var second = Vault.Load(first.Root); // a second process owns a separate Vault object
        try
        {
            await second.UnlockWithPassphraseAsync(pass);
            Assert.Equal("UNSAVED EDIT", File.ReadAllText(working));
        }
        finally
        {
            await second.LockAsync();
            await first.LockAsync();
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
