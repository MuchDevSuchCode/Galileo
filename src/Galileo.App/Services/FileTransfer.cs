using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Galileo.Services;

/// <summary>A snapshot of a running transfer, reported to the UI.</summary>
public sealed class TransferProgress
{
    public string CurrentFile { get; init; } = "";
    public long BytesDone { get; init; }
    public long BytesTotal { get; init; }
    public int FilesDone { get; init; }
    public int FilesTotal { get; init; }
    public double BytesPerSecond { get; init; }
    public bool Paused { get; init; }

    public double Fraction => BytesTotal > 0 ? (double)BytesDone / BytesTotal : (FilesTotal > 0 ? (double)FilesDone / FilesTotal : 0);
}

public sealed class TransferResult
{
    public int FilesCompleted { get; init; }
    public bool Canceled { get; init; }
    public int Errors { get; init; }
}

/// <summary>
/// Copies or moves files/folders into a destination directory with granular progress, and supports
/// <see cref="Pause"/> / <see cref="Resume"/> / <see cref="Cancel"/> while running. Same-volume moves
/// use an instant rename; everything else streams in chunks (so multi-GB copies report progress and
/// stay responsive to pause/cancel mid-file). Name collisions are resolved like Explorer ("name (2)").
/// </summary>
public sealed class FileTransfer
{
    private const int Chunk = 1 << 20; // 1 MiB

    private readonly ManualResetEventSlim _gate = new(initialState: true); // set = running, reset = paused
    private readonly CancellationTokenSource _cts = new();

    public bool IsPaused { get; private set; }
    public bool IsCanceled => _cts.IsCancellationRequested;

    public void Pause() { if (IsCanceled) return; IsPaused = true; _gate.Reset(); }
    public void Resume() { if (IsCanceled) return; IsPaused = false; _gate.Set(); }
    public void TogglePause() { if (IsPaused) Resume(); else Pause(); }
    public void Cancel() { _cts.Cancel(); _gate.Set(); } // release the gate so a paused copy can observe the cancel

    private sealed record CopyOp(string Src, string Dest, long Size);

    public Task<TransferResult> RunAsync(string destDir, IReadOnlyList<string> paths, bool move, IProgress<TransferProgress>? progress)
        => Task.Run(() => Run(destDir, paths, move, progress));

    private TransferResult Run(string destDir, IReadOnlyList<string> paths, bool move, IProgress<TransferProgress>? progress)
    {
        var copies = new List<CopyOp>();      // files to stream-copy
        var dirsToCreate = new List<string>(); // destination dirs (preserves empty subdirs)
        var fastMoves = new List<(string src, string dest, bool isDir)>(); // instant same-volume renames
        var moveSourcesToDelete = new List<(string path, bool isDir)>();    // copied move-sources removed on success
        long bytesTotal = 0;

        // ---- Plan ----
        foreach (var src in paths)
        {
            try
            {
                var name = Path.GetFileName(src.TrimEnd('\\', '/'));
                if (string.IsNullOrEmpty(name)) continue;

                if (Directory.Exists(src))
                {
                    var srcParent = Path.GetDirectoryName(src.TrimEnd('\\', '/'));
                    if (move && string.Equals(srcParent, destDir, StringComparison.OrdinalIgnoreCase)) continue; // no-op
                    var dest = UniquePath(Path.Combine(destDir, name), isDir: true);
                    if (IsSubPath(src, dest)) continue; // can't move a folder into itself

                    if (move && SameVolume(src, dest))
                    {
                        fastMoves.Add((src, dest, true));
                    }
                    else
                    {
                        PlanDirectory(src, dest, copies, dirsToCreate, ref bytesTotal);
                        if (move) moveSourcesToDelete.Add((src, true));
                    }
                }
                else if (File.Exists(src))
                {
                    var srcParent = Path.GetDirectoryName(src);
                    if (move && string.Equals(srcParent, destDir, StringComparison.OrdinalIgnoreCase)) continue; // no-op
                    var dest = UniquePath(Path.Combine(destDir, name), isDir: false);

                    if (move && SameVolume(src, dest))
                    {
                        fastMoves.Add((src, dest, false));
                    }
                    else
                    {
                        long size = 0; try { size = new FileInfo(src).Length; } catch { }
                        copies.Add(new CopyOp(src, dest, size));
                        bytesTotal += size;
                        if (move) moveSourcesToDelete.Add((src, false));
                    }
                }
            }
            catch { /* skip the offending item, keep planning the rest */ }
        }

        var filesTotal = copies.Count + fastMoves.Count;
        var clock = Stopwatch.StartNew();
        long bytesDone = 0;
        var filesDone = 0;
        var errors = 0;
        var canceled = false;

        // rate (exponential moving average) + throttled reporting
        long lastReportMs = -1000, rateBytesMark = 0, rateMsMark = 0;
        double ema = 0;

        void Report(string file, bool force)
        {
            var ms = clock.ElapsedMilliseconds;
            if (!force && ms - lastReportMs < 33) return; // ~30 fps
            var dt = (ms - rateMsMark) / 1000.0;
            if (dt >= 0.25)
            {
                var inst = (bytesDone - rateBytesMark) / dt;
                ema = ema <= 0 ? inst : ema * 0.75 + inst * 0.25;
                rateBytesMark = bytesDone; rateMsMark = ms;
            }
            lastReportMs = ms;
            progress?.Report(new TransferProgress
            {
                CurrentFile = file,
                BytesDone = bytesDone,
                BytesTotal = bytesTotal,
                FilesDone = filesDone,
                FilesTotal = filesTotal,
                BytesPerSecond = ema,
                Paused = IsPaused,
            });
        }

        Report("", force: true);

        // ---- Instant same-volume renames first ----
        foreach (var (src, dest, isDir) in fastMoves)
        {
            _gate.Wait();
            if (IsCanceled) { canceled = true; break; }
            try
            {
                if (isDir) Directory.Move(src, dest); else File.Move(src, dest);
                filesDone++;
                Report(Path.GetFileName(dest), force: true);
            }
            catch { errors++; }
        }

        // ---- Streamed copies ----
        if (!canceled)
        {
            foreach (var dir in dirsToCreate)
            {
                try { Directory.CreateDirectory(dir); } catch { }
            }

            foreach (var op in copies)
            {
                _gate.Wait(); // blocks while paused
                if (IsCanceled) { canceled = true; break; }
                try
                {
                    CopyFile(op, ref bytesDone, Report);
                    filesDone++;
                    Report(Path.GetFileName(op.Dest), force: true);
                }
                catch (OperationCanceledException) { canceled = true; break; }
                catch { errors++; }
            }
        }

        // ---- Finish a move: delete sources we fully copied (only on a clean run) ----
        if (move && !canceled)
        {
            foreach (var (path, isDir) in moveSourcesToDelete)
            {
                try { if (isDir) Directory.Delete(path, recursive: true); else File.Delete(path); }
                catch { errors++; }
            }
        }

        return new TransferResult { FilesCompleted = filesDone, Canceled = canceled, Errors = errors };
    }

    private void CopyFile(CopyOp op, ref long bytesDone, Action<string, bool> report)
    {
        var name = Path.GetFileName(op.Dest);
        Directory.CreateDirectory(Path.GetDirectoryName(op.Dest)!);
        var buffer = new byte[Chunk];
        var partial = false;
        try
        {
            using var src = new FileStream(op.Src, FileMode.Open, FileAccess.Read, FileShare.Read, Chunk, FileOptions.SequentialScan);
            using var dst = new FileStream(op.Dest, FileMode.Create, FileAccess.Write, FileShare.None, Chunk, FileOptions.SequentialScan);
            partial = true;
            int read;
            while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
            {
                _gate.Wait(); // pause mid-file
                if (IsCanceled) throw new OperationCanceledException();
                dst.Write(buffer, 0, read);
                bytesDone += read;
                report(name, false);
            }
            partial = false;
        }
        catch (OperationCanceledException)
        {
            TryDeletePartial(op.Dest);
            throw;
        }
        catch
        {
            if (partial) TryDeletePartial(op.Dest);
            throw;
        }
        try { File.SetLastWriteTimeUtc(op.Dest, File.GetLastWriteTimeUtc(op.Src)); } catch { }
    }

    private static void TryDeletePartial(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void PlanDirectory(string srcDir, string destDir, List<CopyOp> copies, List<string> dirsToCreate, ref long bytesTotal)
    {
        dirsToCreate.Add(destDir);
        try
        {
            foreach (var file in Directory.EnumerateFiles(srcDir))
            {
                long size = 0; try { size = new FileInfo(file).Length; } catch { }
                copies.Add(new CopyOp(file, Path.Combine(destDir, Path.GetFileName(file)), size));
                bytesTotal += size;
            }
            foreach (var sub in Directory.EnumerateDirectories(srcDir))
                PlanDirectory(sub, Path.Combine(destDir, Path.GetFileName(sub)), copies, dirsToCreate, ref bytesTotal);
        }
        catch { /* access denied etc. — copy what we can */ }
    }

    private static bool SameVolume(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetPathRoot(Path.GetFullPath(a)),
                                 Path.GetPathRoot(Path.GetFullPath(b)),
                                 StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static string UniquePath(string path, bool isDir)
    {
        if (isDir ? !Directory.Exists(path) : !File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = isDir ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path);
        var ext = isDir ? "" : Path.GetExtension(path);
        for (var i = 2; i < 10000; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (isDir ? !Directory.Exists(candidate) : !File.Exists(candidate)) return candidate;
        }
        return path;
    }

    private static bool IsSubPath(string parent, string child)
    {
        var p = Path.GetFullPath(parent).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var c = Path.GetFullPath(child).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        return c.StartsWith(p, StringComparison.OrdinalIgnoreCase);
    }
}
