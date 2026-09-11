using System;
using System.IO;
using Galileo.Services;
using Xunit;

namespace Galileo.Tests;

// These tests share the single state.json under the test data root — one class, sequential.
public class AppStateMergeTests
{
    private static void ResetStateFile()
    {
        var p = Path.Combine(AppPaths.Root, "state.json");
        if (File.Exists(p)) File.Delete(p);
    }

    [Fact]
    public void ConcurrentWriters_BothChangesSurvive()
    {
        ResetStateFile();
        var a = AppState.Load();   // "window A"
        var b = AppState.Load();   // "window B" — same baseline

        a.FavoritePaths.Add(@"C:\from-a.jpg");
        a.Save();

        b.HiddenPaths.Add(@"C:\from-b.jpg");
        b.Save();                  // used to clobber A's favorite with B's stale copy

        var merged = AppState.Load();
        Assert.Contains(@"C:\from-a.jpg", merged.FavoritePaths);
        Assert.Contains(@"C:\from-b.jpg", merged.HiddenPaths);
    }

    [Fact]
    public void StaleWriter_DoesNotRevertScalarSetting()
    {
        ResetStateFile();
        var a = AppState.Load();
        var b = AppState.Load();

        a.Theme = "Dark";
        a.Save();

        b.SlideshowSeconds = 9;    // b changes something unrelated
        b.Save();

        var merged = AppState.Load();
        Assert.Equal("Dark", merged.Theme);          // A's change survives B's stale write
        Assert.Equal(9, merged.SlideshowSeconds);    // B's change survives too
    }

    [Fact]
    public void OwnChange_WinsOverConcurrentChangeToSameField()
    {
        ResetStateFile();
        var a = AppState.Load();
        var b = AppState.Load();

        a.Theme = "Dark";
        a.Save();

        b.Theme = "Light";         // both changed the same field — the later writer's OWN change wins
        b.Save();

        Assert.Equal("Light", AppState.Load().Theme);
    }

    [Fact]
    public void RemovalByOtherProcess_IsAdopted()
    {
        ResetStateFile();
        var seed = AppState.Load();
        seed.FavoritePaths.Add(@"C:\shared.jpg");
        seed.Save();

        var a = AppState.Load();
        var b = AppState.Load();

        a.FavoritePaths.Remove(@"C:\shared.jpg");
        a.Save();

        b.PinnedPaths.Add(@"C:\pinned");   // b didn't touch favorites
        b.Save();

        var merged = AppState.Load();
        Assert.DoesNotContain(@"C:\shared.jpg", merged.FavoritePaths); // a's removal not resurrected
        Assert.Contains(@"C:\pinned", merged.PinnedPaths);
    }
}
