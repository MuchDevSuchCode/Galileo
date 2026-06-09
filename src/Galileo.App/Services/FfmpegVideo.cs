using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Galileo.Services;

/// <summary>Probed media facts (subset of ffprobe output) used by the editor.</summary>
public sealed class VideoInfo
{
    public double Duration { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public double Fps { get; init; }
    public string VideoCodec { get; init; } = "";
    public bool HasAudio { get; init; }
}

/// <summary>The editor's parameter set — turned into an FFmpeg command at export time
/// (ported from mp4mix). Editing is non-destructive: the original is run through a filter graph.</summary>
public sealed class VideoEditSettings
{
    public string InputPath = "";
    public double SourceDuration;

    // Trim. Segments (when non-empty) take precedence over Start/End for a multi-segment stitch.
    public double TrimStart;
    public double? TrimEnd;                       // null = to the end
    public List<(double Start, double End)> Segments = new();

    // Transform
    public int CropL, CropT, CropR, CropB;        // pixel margins
    public int ResizeW, ResizeH;                  // 0,0 = keep
    public int Rotate;                            // 0 | 90 | 180 | 270
    public bool FlipH, FlipV;

    // Filters
    public bool Deinterlace, Denoise, Sharpen, Stabilize;
    public bool ColorAdjust;                      // apply eq with the values below
    public double Brightness;                     // -1..1  (eq default 0)
    public double Contrast = 1;                   // 0..2   (eq default 1)
    public double Saturation = 1;                 // 0..3   (eq default 1)
    public double Fps;                            // 0 = keep
    public double Speed = 1;                      // 0.25..4

    // Audio: keep | aac | mp3 | none
    public string AudioMode = "keep";

    // Output
    public string Container = "mp4";              // mp4 | mkv | ts | gif
    public string VideoCodec = "h264";            // h264 | h265 | copy | *_nvenc | *_qsv | *_amf
    public int Crf = 21;
    public string Preset = "medium";
}

/// <summary>
/// Bundled-FFmpeg video engine ported from mp4mix: probe, encoder detection, an editor filter graph,
/// and export (single range / multi-segment stitch / animated GIF) with progress + cancellation.
/// </summary>
public static class FfmpegVideo
{
    private static string Dir => Path.Combine(AppContext.BaseDirectory, "Assets", "ffmpeg");
    public static string FfmpegPath => Path.Combine(Dir, "ffmpeg.exe");
    public static string FfprobePath => Path.Combine(Dir, "ffprobe.exe");
    public static bool Available => File.Exists(FfmpegPath) && File.Exists(FfprobePath);

    private static IReadOnlyList<string>? _encoders;

    // ---- Probe ----

    public static async Task<VideoInfo?> ProbeAsync(string path)
    {
        try
        {
            var json = await RunCaptureAsync(FfprobePath,
                new[] { "-v", "quiet", "-print_format", "json", "-show_format", "-show_streams", path },
                CancellationToken.None);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var streams = root.TryGetProperty("streams", out var s) ? s.EnumerateArray().ToList() : new();
            JsonElement? video = streams.FirstOrDefault(x => Codec(x) == "video");
            var hasVideo = streams.Any(x => Codec(x) == "video");
            if (!hasVideo) video = null;
            var hasAudio = streams.Any(x => Codec(x) == "audio");

            double duration = 0;
            if (root.TryGetProperty("format", out var fmt) && fmt.TryGetProperty("duration", out var d)
                && double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var dv))
                duration = dv;

            int w = 0, h = 0; double fps = 0; var vc = "";
            if (video is { } v)
            {
                if (v.TryGetProperty("width", out var wp)) w = wp.GetInt32();
                if (v.TryGetProperty("height", out var hp)) h = hp.GetInt32();
                if (v.TryGetProperty("codec_name", out var cn)) vc = cn.GetString() ?? "";
                fps = ParseFrameRate(Str(v, "avg_frame_rate"));
                if (fps <= 0) fps = ParseFrameRate(Str(v, "r_frame_rate"));
            }
            return new VideoInfo { Duration = duration, Width = w, Height = h, Fps = fps, VideoCodec = vc, HasAudio = hasAudio };
        }
        catch { return null; }

        static string Codec(JsonElement e) => e.TryGetProperty("codec_type", out var c) ? c.GetString() ?? "" : "";
        static string Str(JsonElement e, string n) => e.TryGetProperty(n, out var p) ? p.GetString() ?? "" : "";
    }

    private static double ParseFrameRate(string r)
    {
        if (string.IsNullOrEmpty(r)) return 0;
        var parts = r.Split('/');
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var den) && den != 0)
            return n / den;
        return double.TryParse(r, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : 0;
    }

    /// <summary>Hardware encoders this FFmpeg build exposes (NVENC / Quick Sync / AMD), cached.</summary>
    public static async Task<IReadOnlyList<string>> DetectEncodersAsync()
    {
        if (_encoders is not null) return _encoders;
        try
        {
            var outp = await RunCaptureAsync(FfmpegPath, new[] { "-hide_banner", "-encoders" }, CancellationToken.None, captureStdErr: true);
            string[] candidates = { "h264_nvenc", "hevc_nvenc", "h264_qsv", "hevc_qsv", "h264_amf", "hevc_amf" };
            _encoders = candidates.Where(c => outp.Contains(c)).ToList();
        }
        catch { _encoders = Array.Empty<string>(); }
        return _encoders;
    }

    // ---- Filter graph (ported from buildVideoFilters) ----

    public static List<string> BuildVideoFilters(VideoEditSettings f)
    {
        var vf = new List<string>();
        if (f.Deinterlace) vf.Add("yadif");
        if (f.Denoise) vf.Add("hqdn3d");
        if (f.CropL != 0 || f.CropT != 0 || f.CropR != 0 || f.CropB != 0)
            vf.Add($"crop=in_w-{f.CropL + f.CropR}:in_h-{f.CropT + f.CropB}:{f.CropL}:{f.CropT}");
        if (f.ResizeW > 0 && f.ResizeH > 0)
            vf.Add($"scale={f.ResizeW}:{f.ResizeH}:flags=lanczos");
        if (f.Rotate == 90) vf.Add("transpose=1");
        else if (f.Rotate == 270) vf.Add("transpose=2");
        else if (f.Rotate == 180) vf.Add("transpose=2,transpose=2");
        if (f.FlipH) vf.Add("hflip");
        if (f.FlipV) vf.Add("vflip");
        if (f.ColorAdjust)
            vf.Add($"eq=brightness={N(f.Brightness)}:contrast={N(f.Contrast)}:saturation={N(f.Saturation)}");
        if (f.Sharpen) vf.Add("unsharp=5:5:1.0:5:5:0.0");
        if (f.Fps > 0) vf.Add($"fps={N(f.Fps)}");
        if (f.Speed != 1) vf.Add($"setpts={N(1.0 / f.Speed)}*PTS");
        return vf;
    }

    private static string AtempoChain(double speed)
    {
        var parts = new List<string>();
        var r = speed;
        while (r > 2.0) { parts.Add("atempo=2.0"); r /= 2.0; }
        while (r < 0.5) { parts.Add("atempo=0.5"); r /= 0.5; }
        parts.Add("atempo=" + N(r));
        return string.Join(",", parts);
    }

    // ---- Export ----

    public static async Task ExportAsync(VideoEditSettings s, string outPath, IProgress<double>? progress, CancellationToken ct)
    {
        if (s.Container == "gif") { await ExportGifAsync(s, outPath, progress, ct); return; }
        if (s.Segments.Count > 0) { await ExportSegmentsAsync(s, outPath, progress, ct); return; }

        var start = s.TrimStart > 0 ? s.TrimStart : 0;
        var duration = s.TrimEnd is { } end ? Math.Max(0.01, end - s.TrimStart) : (double?)null;
        await ExportRangeAsync(s, start, duration, outPath, progress, ct);
    }

    private static async Task ExportRangeAsync(VideoEditSettings s, double start, double? duration, string outPath,
        IProgress<double>? progress, CancellationToken ct)
    {
        string? stabDir = null;
        string? stabFilter = null;
        try
        {
            if (s.Stabilize)
            {
                stabDir = Directory.CreateTempSubdirectory("galileo-stab-").FullName;
                var dArgs = new List<string> { "-y", "-hide_banner" };
                if (start > 0) { dArgs.Add("-ss"); dArgs.Add(N(start)); }
                dArgs.Add("-i"); dArgs.Add(s.InputPath);
                if (duration is { } dd) { dArgs.Add("-t"); dArgs.Add(N(dd)); }
                dArgs.AddRange(new[] { "-vf", "vidstabdetect=result=transforms.trf:shakiness=6", "-f", "null", "-" });
                await RunCaptureAsync(FfmpegPath, dArgs.ToArray(), ct, captureStdErr: true, cwd: stabDir);
                stabFilter = "vidstabtransform=input=transforms.trf:smoothing=18,unsharp=5:5:0.6:3:3:0.0";
            }

            var args = new List<string> { "-y", "-hide_banner" };
            if (start > 0) { args.Add("-ss"); args.Add(N(start)); }
            args.Add("-i"); args.Add(s.InputPath);
            if (duration is { } d) { args.Add("-t"); args.Add(N(d)); }

            var vf = BuildVideoFilters(s);
            if (stabFilter is not null) vf.Insert(0, stabFilter);
            var q = s.Crf.ToString(CultureInfo.InvariantCulture);
            var vcodec = s.VideoCodec;

            if (vcodec == "copy")
            {
                args.Add("-c:v"); args.Add("copy");
            }
            else
            {
                if (vf.Count > 0) { args.Add("-vf"); args.Add(string.Join(",", vf)); }
                AddVideoCodec(args, vcodec, q, s.Preset);
                args.Add("-pix_fmt"); args.Add("yuv420p");
            }

            var speed = s.Speed;
            switch (s.AudioMode)
            {
                case "none": args.Add("-an"); break;
                case "mp3": args.Add("-c:a"); args.Add("libmp3lame"); args.Add("-q:a"); args.Add("2"); break;
                case "keep" when speed == 1: args.Add("-c:a"); args.Add("copy"); break;
                default: args.Add("-c:a"); args.Add("aac"); args.Add("-b:a"); args.Add("192k"); break;
            }
            if (speed != 1 && s.AudioMode != "none") { args.Add("-af"); args.Add(AtempoChain(speed)); }

            if (outPath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) { args.Add("-movflags"); args.Add("+faststart"); }
            args.Add("-progress"); args.Add("pipe:1"); args.Add("-nostats"); args.Add(outPath);

            var sp = speed != 1 ? speed : 1;
            var outSeconds = (duration ?? s.SourceDuration) / sp;
            await RunProgressAsync(args.ToArray(), outSeconds, progress, ct, cwd: stabDir);
        }
        finally { if (stabDir is not null) TryDeleteDir(stabDir); }
    }

    private static async Task ExportGifAsync(VideoEditSettings s, string outPath, IProgress<double>? progress, CancellationToken ct)
    {
        var start = s.TrimStart > 0 ? s.TrimStart : 0;
        var duration = s.TrimEnd is { } end ? Math.Max(0.01, end - s.TrimStart) : (double?)null;
        var args = new List<string> { "-y", "-hide_banner" };
        if (start > 0) { args.Add("-ss"); args.Add(N(start)); }
        args.Add("-i"); args.Add(s.InputPath);
        if (duration is { } d) { args.Add("-t"); args.Add(N(d)); }
        var chain = BuildVideoFilters(s);
        chain.Add("fps=15");
        chain.Add("scale='min(640,iw)':-2:flags=lanczos");
        args.Add("-filter_complex");
        args.Add($"[0:v]{string.Join(",", chain)},split[gs0][gs1];[gs0]palettegen=stats_mode=diff[gp];[gs1][gp]paletteuse=dither=bayer");
        args.Add("-an"); args.Add("-loop"); args.Add("0");
        args.Add("-progress"); args.Add("pipe:1"); args.Add("-nostats"); args.Add(outPath);
        await RunProgressAsync(args.ToArray(), duration ?? s.SourceDuration, progress, ct);
    }

    private static async Task ExportSegmentsAsync(VideoEditSettings s, string outPath, IProgress<double>? progress, CancellationToken ct)
    {
        var speed = s.Speed != 0 ? s.Speed : 1;
        var totalOut = s.Segments.Sum(seg => Math.Max(0.01, seg.End - seg.Start)) / speed;
        double doneOut = 0;
        var partDir = Directory.CreateTempSubdirectory("galileo-seg-").FullName;
        var partExt = "." + s.Container;
        try
        {
            var parts = new List<string>();
            for (var i = 0; i < s.Segments.Count; i++)
            {
                var seg = s.Segments[i];
                var segDur = Math.Max(0.01, seg.End - seg.Start);
                var segOut = segDur / speed;
                var partPath = Path.Combine(partDir, $"part_{i}{partExt}");
                var outerDone = doneOut;
                var segProgress = new Progress<double>(pct =>
                {
                    if (totalOut > 0) progress?.Report(Math.Min(99, (outerDone + segOut * pct / 100) / totalOut * 100));
                });
                await ExportRangeAsync(s, seg.Start, segDur, partPath, segProgress, ct);
                doneOut += segOut;
                parts.Add(partPath);
            }
            var listPath = Path.Combine(partDir, "concat.txt");
            await File.WriteAllTextAsync(listPath,
                string.Join("\n", parts.Select(p => $"file '{p.Replace('\\', '/')}'")), ct);
            await RunCaptureAsync(FfmpegPath,
                new[] { "-y", "-hide_banner", "-f", "concat", "-safe", "0", "-i", listPath, "-c", "copy", outPath },
                ct, captureStdErr: true);
            progress?.Report(100);
        }
        finally { TryDeleteDir(partDir); }
    }

    /// <summary>Generates an evenly-spaced strip of JPEG thumbnails into a temp folder; returns their paths.</summary>
    public static async Task<List<string>> GenerateThumbnailsAsync(string input, int count, double duration, CancellationToken ct = default)
    {
        var dir = Directory.CreateTempSubdirectory("galileo-thumbs-").FullName;
        count = Math.Max(1, count);
        var rate = count / Math.Max(0.001, duration);
        await RunCaptureAsync(FfmpegPath,
            new[] { "-y", "-i", input, "-vf", $"fps={N(rate)},scale=160:-2", "-frames:v", count.ToString(), Path.Combine(dir, "t_%04d.jpg") },
            ct, captureStdErr: true);
        var files = Directory.GetFiles(dir, "t_*.jpg");
        Array.Sort(files, StringComparer.Ordinal);
        return files.ToList();
    }

    /// <summary>Save a single frame to a PNG.</summary>
    public static async Task SnapshotAsync(string input, double time, string outPath, CancellationToken ct = default)
        => await RunCaptureAsync(FfmpegPath,
            new[] { "-y", "-ss", N(time), "-i", input, "-frames:v", "1", outPath }, ct, captureStdErr: true);

    private static void AddVideoCodec(List<string> args, string vcodec, string q, string preset)
    {
        if (vcodec == "h264") { args.AddRange(new[] { "-c:v", "libx264", "-preset", preset, "-crf", q }); }
        else if (vcodec == "h265") { args.AddRange(new[] { "-c:v", "libx265", "-preset", preset, "-crf", q }); }
        else if (vcodec.EndsWith("_nvenc")) { args.AddRange(new[] { "-c:v", vcodec, "-rc", "vbr", "-cq", q, "-preset", "p5" }); }
        else if (vcodec.EndsWith("_qsv")) { args.AddRange(new[] { "-c:v", vcodec, "-global_quality", q }); }
        else if (vcodec.EndsWith("_amf")) { args.AddRange(new[] { "-c:v", vcodec, "-rc", "cqp", "-qp_i", q, "-qp_p", q }); }
        else { args.AddRange(new[] { "-c:v", "libx264", "-preset", preset, "-crf", q }); }
    }

    // ---- Process helpers ----

    private static async Task RunProgressAsync(string[] args, double totalSeconds, IProgress<double>? progress, CancellationToken ct, string? cwd = null)
    {
        var psi = NewPsi(FfmpegPath, args, cwd);
        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var err = new StringBuilder();
        var totalUs = totalSeconds * 1e6;
        var lastPct = -1.0;

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            if (e.Data.StartsWith("out_time_us=", StringComparison.Ordinal) && totalUs > 0
                && long.TryParse(e.Data.AsSpan("out_time_us=".Length), out var us))
            {
                var pct = Math.Min(99, us / totalUs * 100);
                if (Math.Abs(pct - lastPct) >= 0.5) { lastPct = pct; progress?.Report(pct); }
            }
        };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) { err.AppendLine(e.Data); if (err.Length > 8000) err.Remove(0, 4000); } };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        await WaitOrKillAsync(proc, ct);
        if (proc.ExitCode != 0) throw new InvalidOperationException(TailLines(err.ToString(), 5));
        progress?.Report(100);
    }

    private static async Task<string> RunCaptureAsync(string exe, string[] args, CancellationToken ct, bool captureStdErr = false, string? cwd = null)
    {
        var psi = NewPsi(exe, args, cwd);
        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.Start();
        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();
        await WaitOrKillAsync(proc, ct);
        var outText = await stdout;
        var errText = await stderr;
        if (proc.ExitCode != 0 && !captureStdErr) throw new InvalidOperationException(TailLines(errText, 5));
        return captureStdErr ? outText + errText : outText;
    }

    private static ProcessStartInfo NewPsi(string exe, string[] args, string? cwd)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = cwd ?? "",
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    private static async Task WaitOrKillAsync(Process proc, CancellationToken ct)
    {
        try { await proc.WaitForExitAsync(ct); }
        catch (OperationCanceledException) { try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { } throw; }
    }

    private static string TailLines(string s, int n)
    {
        var lines = s.Replace("\r", "").Split('\n').Where(l => l.Length > 0).ToArray();
        var msg = string.Join("\n", lines.TakeLast(n));
        return string.IsNullOrWhiteSpace(msg) ? "FFmpeg failed." : msg;
    }

    private static void TryDeleteDir(string dir) { try { Directory.Delete(dir, recursive: true); } catch { } }

    // Invariant-culture number formatting (FFmpeg expects '.' decimals).
    private static string N(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
}
