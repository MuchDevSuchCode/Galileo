using System;
using System.IO;
using System.Threading.Tasks;
using Galileo.Services;
using Xunit;

namespace Galileo.Tests;

public class SecureWipeTests
{
    private static string TempFile(string content)
    {
        var p = Path.Combine(Path.GetTempPath(), "galileo-wipe-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(p, content);
        return p;
    }

    [Fact]
    public async Task Wipe_RemovesFile_AndReportsOverwritten()
    {
        var f = TempFile("sensitive");
        var summary = await SecureWipe.WipePathsAsync(new[] { f }, WipeMethod.Random, progress: null);

        Assert.False(File.Exists(f));
        Assert.Equal(1, summary.Overwritten);
        Assert.Equal(0, summary.PlainDeleted);
        Assert.Equal(0, summary.Failed);
    }

    [Fact]
    public async Task LockedFile_IsReportedAsFailed_NotSuccess()
    {
        var f = TempFile("locked");
        using (new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.None)) // hold it open exclusively
        {
            var summary = await SecureWipe.WipePathsAsync(new[] { f }, WipeMethod.Random, progress: null);
            Assert.Equal(1, summary.Failed);
            Assert.Equal(0, summary.Overwritten);
        }
        Assert.True(File.Exists(f)); // the wipe honestly failed; nothing pretended otherwise
        File.Delete(f);
    }

    [Fact]
    public async Task ReadOnlyFile_IsStillWiped()
    {
        var f = TempFile("readonly");
        File.SetAttributes(f, FileAttributes.ReadOnly);
        var summary = await SecureWipe.WipePathsAsync(new[] { f }, WipeMethod.Zero, progress: null);
        Assert.False(File.Exists(f));
        Assert.Equal(1, summary.Overwritten);
    }
}
