using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Galileo.Services;
using Xunit;

[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

namespace Galileo.Tests;

public class FileTransferTests : IDisposable
{
    private readonly string _root;

    public FileTransferTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "galileo-ft-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private string Dir(string name) { var d = Path.Combine(_root, name); Directory.CreateDirectory(d); return d; }

    private sealed class CancelAt : IProgress<TransferProgress>
    {
        private readonly FileTransfer _t; private readonly long _at;
        public CancelAt(FileTransfer t, long at) { _t = t; _at = at; }
        public void Report(TransferProgress p) { if (p.BytesDone > _at) _t.Cancel(); }
    }

    private static Task<ConflictChoice> Overwrite(ConflictInfo _) =>
        Task.FromResult(new ConflictChoice { Action = ConflictAction.Overwrite });

    [Fact]
    public async Task CancelledReplace_KeepsOldDestinationIntact()
    {
        var srcDir = Dir("src"); var dstDir = Dir("dst");
        var src = Path.Combine(srcDir, "big.bin");
        var dst = Path.Combine(dstDir, "big.bin");
        using (var fs = File.Create(src)) fs.SetLength(512L * 1024 * 1024);
        File.WriteAllText(dst, "OLD CONTENT");

        var t = new FileTransfer();
        var res = await t.RunAsync(dstDir, new[] { src }, move: false, new CancelAt(t, 4 << 20), Overwrite);

        Assert.True(res.Canceled);
        Assert.Equal("OLD CONTENT", File.ReadAllText(dst));
        Assert.Empty(Directory.GetFiles(dstDir, "*.galileo-partial"));
    }

    [Fact]
    public async Task SuccessfulReplace_CommitsNewContent()
    {
        var srcDir = Dir("src2"); var dstDir = Dir("dst2");
        var src = Path.Combine(srcDir, "f.txt"); File.WriteAllText(src, "NEW");
        var dst = Path.Combine(dstDir, "f.txt"); File.WriteAllText(dst, "OLD");

        var res = await new FileTransfer().RunAsync(dstDir, new[] { src }, move: false, null, Overwrite);

        Assert.Equal(1, res.FilesCompleted);
        Assert.Equal(0, res.Errors);
        Assert.Equal("NEW", File.ReadAllText(dst));
    }

    [Fact]
    public async Task Copy_PreservesTimestampsAndAttributes()
    {
        var srcDir = Dir("src3"); var dstDir = Dir("dst3");
        var src = Path.Combine(srcDir, "meta.txt"); File.WriteAllText(src, "data");
        var created = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var written = new DateTime(2021, 6, 7, 8, 9, 10, DateTimeKind.Utc);
        File.SetCreationTimeUtc(src, created);
        File.SetLastWriteTimeUtc(src, written);
        File.SetAttributes(src, File.GetAttributes(src) | FileAttributes.ReadOnly);

        try
        {
            await new FileTransfer().RunAsync(dstDir, new[] { src }, move: false, null, null);
            var dst = Path.Combine(dstDir, "meta.txt");
            Assert.Equal(created, File.GetCreationTimeUtc(dst));
            Assert.Equal(written, File.GetLastWriteTimeUtc(dst));
            Assert.True(File.GetAttributes(dst).HasFlag(FileAttributes.ReadOnly));
            File.SetAttributes(dst, FileAttributes.Normal);
        }
        finally { File.SetAttributes(src, FileAttributes.Normal); }
    }

    [Fact]
    public async Task DirectoryJunction_IsNeverTraversed()
    {
        var outside = Dir("outside");
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "outside data");
        var srcDir = Dir(Path.Combine("jsrc", "folder"));
        File.WriteAllText(Path.Combine(srcDir, "inside.txt"), "inside");
        var junction = Path.Combine(srcDir, "link");
        var mk = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{outside}\"")
        { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
        mk.WaitForExit();
        Assert.Equal(0, mk.ExitCode);

        var dstDir = Dir("jdst");
        await new FileTransfer().RunAsync(dstDir, new[] { srcDir }, move: false, null, null);

        Assert.True(File.Exists(Path.Combine(dstDir, "folder", "inside.txt")));
        Assert.False(File.Exists(Path.Combine(dstDir, "folder", "link", "secret.txt")));
        Assert.True(File.Exists(Path.Combine(outside, "secret.txt"))); // target untouched
    }

    [Fact]
    public async Task CancelledMove_RollsBackFastRenames()
    {
        // A same-volume move renames instantly; a later cancel (during the streamed phase) must put
        // those renamed items back so "cancel" means "nothing moved".
        var srcDir = Dir("mvsrc"); var dstDir = Dir("mvdst");
        var fastFile = Path.Combine(srcDir, "instant.txt"); File.WriteAllText(fastFile, "fast");
        // A slow streamed item: same name as an existing dest file forces the streamed-copy path.
        var slowFile = Path.Combine(srcDir, "slow.bin");
        using (var fs = File.Create(slowFile)) fs.SetLength(512L * 1024 * 1024);
        File.WriteAllText(Path.Combine(dstDir, "slow.bin"), "OLD");

        var t = new FileTransfer();
        var res = await t.RunAsync(dstDir, new[] { fastFile, slowFile }, move: true, new CancelAt(t, 4 << 20), Overwrite);

        Assert.True(res.Canceled);
        Assert.True(File.Exists(fastFile), "fast-moved file should be rolled back to its source");
        Assert.False(File.Exists(Path.Combine(dstDir, "instant.txt")));
        Assert.True(File.Exists(slowFile), "streamed source must never be deleted on cancel");
        Assert.Equal("OLD", File.ReadAllText(Path.Combine(dstDir, "slow.bin")));
    }

    [Fact]
    public async Task DestinationAppearingAfterPlanning_IsNotOverwritten()
    {
        var srcDir = Dir("late-src"); var dstDir = Dir("late-dst");
        var src = Path.Combine(srcDir, "late.txt"); File.WriteAllText(src, "NEW");
        var dst = Path.Combine(dstDir, "late.txt");

        var made = false;
        var maker = new ProgressAction(_ => { if (!made) { made = true; File.WriteAllText(dst, "SNEAKY"); } });
        await new FileTransfer().RunAsync(dstDir, new[] { src }, move: false, maker, null);

        Assert.Equal("SNEAKY", File.ReadAllText(dst));       // the late arrival survives
        Assert.Equal(2, Directory.GetFiles(dstDir).Length);  // ours landed under a keep-both name
    }

    private sealed class ProgressAction : IProgress<TransferProgress>
    {
        private readonly Action<TransferProgress> _a;
        public ProgressAction(Action<TransferProgress> a) => _a = a;
        public void Report(TransferProgress p) => _a(p);
    }

    [Fact]
    public async Task SkippedConflicts_AreCounted()
    {
        var srcDir = Dir("skip-src"); var dstDir = Dir("skip-dst");
        var src = Path.Combine(srcDir, "s.txt"); File.WriteAllText(src, "NEW");
        File.WriteAllText(Path.Combine(dstDir, "s.txt"), "OLD");

        var res = await new FileTransfer().RunAsync(dstDir, new[] { src }, move: false, null,
            _ => Task.FromResult(new ConflictChoice { Action = ConflictAction.Skip }));

        Assert.Equal(1, res.Skipped);
        Assert.Equal(0, res.FilesCompleted);
        Assert.Equal("OLD", File.ReadAllText(Path.Combine(dstDir, "s.txt")));
    }
}
