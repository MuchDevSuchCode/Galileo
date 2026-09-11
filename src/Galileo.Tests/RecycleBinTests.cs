using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Galileo.Services;
using Xunit;

namespace Galileo.Tests;

// All recycle-bin tests share one on-disk bin (its root is static under the test data root), so they
// live in a single class — xunit runs tests within a class sequentially.
public class RecycleBinTests
{
    private readonly RecycleBin _bin = new();
    private readonly string _work;

    public RecycleBinTests()
    {
        _work = Path.Combine(Path.GetTempPath(), "galileo-bin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_work);
    }

    [Fact]
    public void BinAndRestore_RoundTrips()
    {
        var f = Path.Combine(_work, "doc.txt");
        File.WriteAllText(f, "hello");

        Assert.True(_bin.MoveToBin(f));
        Assert.False(File.Exists(f));

        var entry = _bin.Load().First(e => e.OriginalPath == f);
        Assert.True(_bin.Restore(_bin.StorePathOf(entry), out var restoredTo));
        Assert.Equal(f, restoredTo);
        Assert.Equal("hello", File.ReadAllText(f));
    }

    [Fact]
    public void Restore_TypeCollision_PicksSafeName()
    {
        // Bin a FILE named "report", then create a FOLDER at that exact path: restore must not fail —
        // it should land at a renamed path (the old kind-specific check missed the folder entirely).
        var f = Path.Combine(_work, "report");
        File.WriteAllText(f, "file content");
        Assert.True(_bin.MoveToBin(f));
        Directory.CreateDirectory(f); // a folder now squats on the original path

        var entry = _bin.Load().First(e => e.OriginalPath == f);
        Assert.True(_bin.Restore(_bin.StorePathOf(entry), out var restoredTo));
        Assert.NotEqual(f, restoredTo);
        Assert.True(File.Exists(restoredTo));
        Assert.Equal("file content", File.ReadAllText(restoredTo));
    }

    [Fact]
    public async Task Empty_LeavesUnindexedStoreFilesAlone()
    {
        var f = Path.Combine(_work, "victim.txt");
        File.WriteAllText(f, "bye");
        Assert.True(_bin.MoveToBin(f));

        // Simulate another process mid-add: a store file that has no index record yet.
        var storeDir = Path.GetDirectoryName(_bin.StorePathOf(_bin.Load()[0]))!;
        var stray = Path.Combine(storeDir, Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(stray, "concurrent add in flight");

        await _bin.EmptyAsync(WipeMethod.None);

        Assert.DoesNotContain(_bin.Load(), e => e.OriginalPath == f); // snapshot entries removed
        Assert.True(File.Exists(stray), "a store file with no index record must survive Empty");
        File.Delete(stray);
    }
}
