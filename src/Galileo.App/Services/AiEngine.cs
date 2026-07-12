using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Galileo.Services;

public enum AiModel
{
    /// <summary>Real-ESRGAN x4plus — the high-quality super-resolution net (slow, best detail).</summary>
    Upscale,
    /// <summary>Real-ESRGAN general-x4v3 — small net trained on real-world degradation (noise, blur,
    /// compression). Used for denoise/sharpen because it's ~45x faster than x4plus.</summary>
    General,
    /// <summary>CodeFormer — blind face restoration, 512x512, with a fidelity weight.</summary>
    Face,
    /// <summary>YuNet — face detection with 5 landmarks (needed to align faces for CodeFormer).</summary>
    FaceDetect,
}

public sealed record ModelSpec(AiModel Model, string File, string Url, string Input, int Scale, long MinBytes, string Label);

/// <summary>
/// On-device AI image restoration, run on the GPU through DirectML (any DX12 card — no CUDA install).
///
/// Every model here was verified against this pipeline; two well-known ones were rejected because they
/// don't actually work: SCUNet crashes DirectML outright, and the OpenCV NAFNet ONNX is a broken export
/// (garbage output on GPU, crash on CPU). The Real-ESRGAN *general* model covers denoise/sharpen instead —
/// it's trained on exactly that real-world degradation.
///
/// Models download once, on demand, to %LocalAppData%\Galileo\models and then run entirely offline.
/// Pixels are BGRA8 (Win2D's layout); the nets want planar RGB float (NCHW).
/// </summary>
public sealed class AiEngine : IDisposable
{
    public static readonly IReadOnlyDictionary<AiModel, ModelSpec> Catalog = new Dictionary<AiModel, ModelSpec>
    {
        [AiModel.Upscale] = new(AiModel.Upscale, "realesrgan-x4plus.onnx",
            "https://huggingface.co/fernandotonon/QtMeshEditor-realesrgan-onnx/resolve/main/RealESRGAN_x4plus.onnx",
            "input", 4, 50_000_000, "Real-ESRGAN x4plus (64 MB)"),

        [AiModel.General] = new(AiModel.General, "realesr-general-x4v3.onnx",
            "https://huggingface.co/Samo629/real-esrgan-onnx/resolve/main/realesr-general-x4v3.onnx",
            "input", 4, 3_000_000, "Real-ESRGAN general (5 MB)"),

        [AiModel.Face] = new(AiModel.Face, "codeformer.onnx",
            "https://huggingface.co/bluefoxcreation/Codeformer-ONNX/resolve/main/codeformer.onnx",
            "x", 1, 300_000_000, "CodeFormer face restoration (360 MB)"),

        [AiModel.FaceDetect] = new(AiModel.FaceDetect, "face_yunet.onnx",
            "https://huggingface.co/opencv/face_detection_yunet/resolve/main/face_detection_yunet_2023mar.onnx",
            "input", 1, 100_000, "YuNet face detection (0.2 MB)"),
    };

    /// <summary>Long-edge cap on the input when keeping a 4x result (the output has 16x the pixels).</summary>
    public const int MaxUpscaleInputEdge = 1600;

    public static string ModelDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Galileo", "models");

    public static string PathFor(AiModel m) => Path.Combine(ModelDir, Catalog[m].File);

    public static bool IsReady(AiModel m)
    {
        var p = PathFor(m);
        return File.Exists(p) && new FileInfo(p).Length >= Catalog[m].MinBytes;
    }

    /// <summary>Downloads to a .part file and moves it into place, so an aborted download can't leave a
    /// truncated model that would then fail cryptically at load time.</summary>
    public static async Task DownloadAsync(AiModel m, IProgress<double>? progress, CancellationToken ct = default)
    {
        var spec = Catalog[m];
        Directory.CreateDirectory(ModelDir);
        var dest = PathFor(m);
        var tmp = dest + ".part";
        if (File.Exists(tmp)) File.Delete(tmp);

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        using var resp = await http.GetAsync(spec.Url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? 0L;

        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(tmp))
        {
            var buf = new byte[81920];
            long read = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                read += n;
                if (total > 0) progress?.Report((double)read / total);
            }
        }
        if (File.Exists(dest)) File.Delete(dest);
        File.Move(tmp, dest);
    }

    private readonly Dictionary<AiModel, InferenceSession> _sessions = new();
    public string Provider { get; private set; } = "CPU";

    internal InferenceSession Session(AiModel m)
    {
        if (_sessions.TryGetValue(m, out var s)) return s;
        if (!IsReady(m)) throw new InvalidOperationException($"The {Catalog[m].Label} model hasn't been downloaded yet.");

        InferenceSession created;
        try
        {
            var opts = new SessionOptions { LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_FATAL };
            opts.AppendExecutionProvider_DML(0);            // adapter 0 = primary DX12 GPU
            opts.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
            created = new InferenceSession(PathFor(m), opts);
            Provider = "DirectML (GPU)";
        }
        catch (Exception ex)
        {
            App.LogInfo("AI: DirectML unavailable, falling back to CPU: " + ex.Message);
            created = new InferenceSession(PathFor(m));
            Provider = "CPU";
        }
        _sessions[m] = created;
        return created;
    }

    // ---------------- operations ----------------

    /// <summary>Super-resolve with Real-ESRGAN x4plus. <paramref name="keepUpscale"/> false = resample back to
    /// the original size (detail recovery without growing the image).</summary>
    public byte[] Enhance(byte[] bgra, int w, int h, bool keepUpscale, out int outW, out int outH,
        IProgress<double>? progress = null, CancellationToken ct = default)
        => RunSr(AiModel.Upscale, bgra, w, h, keepUpscale, out outW, out outH, progress, ct);

    /// <summary>Denoise / sharpen using the Real-ESRGAN general model (trained on real-world noise, blur and
    /// compression). Runs at 4x then straight back down, and blends with the original by
    /// <paramref name="strength"/> (0..1) so the effect is dialable.</summary>
    public byte[] Denoise(byte[] bgra, int w, int h, double strength,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var processed = RunSr(AiModel.General, bgra, w, h, false, out _, out _, progress, ct);
        return Blend(bgra, processed, strength);
    }

    private byte[] RunSr(AiModel model, byte[] bgra, int w, int h, bool keepUpscale, out int outW, out int outH,
        IProgress<double>? progress, CancellationToken ct)
    {
        var spec = Catalog[model];
        var session = Session(model);
        foreach (var tile in new[] { 512, 256, 128 })
        {
            try
            {
                return RunTiled(session, spec.Input, bgra, w, h, spec.Scale, keepUpscale, tile,
                    out outW, out outH, progress, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (tile > 128)
            {
                App.LogInfo($"AI: tile {tile} failed ({ex.GetType().Name}), retrying smaller");
            }
        }
        throw new InvalidOperationException("AI processing failed on this image.");
    }

    /// <summary>Overlap-tiled inference. The overlap is discarded when stitching, which is what stops tile
    /// seams from showing. With <paramref name="keepUpscale"/> false each tile is box-downsampled straight
    /// back to its input size, so no giant scale^2 intermediate is ever allocated.</summary>
    private byte[] RunTiled(InferenceSession session, string inputName, byte[] bgra, int w, int h,
        int scale, bool keepUpscale, int tile, out int outW, out int outH,
        IProgress<double>? progress, CancellationToken ct)
    {
        var pad = Math.Max(8, tile / 16);
        var step = tile - 2 * pad;
        if (step <= 0) throw new InvalidOperationException("tile too small");

        outW = keepUpscale ? w * scale : w;
        outH = keepUpscale ? h * scale : h;
        var dest = new byte[outW * outH * 4];

        var input = new DenseTensor<float>(new[] { 1, 3, tile, tile });
        var inBuf = input.Buffer.Span;
        var plane = tile * tile;

        var cols = (w + step - 1) / step;
        var rows = (h + step - 1) / step;
        var total = cols * rows;
        var done = 0;

        for (var ry = 0; ry < rows; ry++)
        for (var rx = 0; rx < cols; rx++)
        {
            ct.ThrowIfCancellationRequested();
            var tx = rx * step;
            var ty = ry * step;

            for (var y = 0; y < tile; y++)
            {
                var sy = Math.Clamp(ty - pad + y, 0, h - 1);
                for (var x = 0; x < tile; x++)
                {
                    var sx = Math.Clamp(tx - pad + x, 0, w - 1);
                    var p = (sy * w + sx) * 4;
                    var o = y * tile + x;
                    inBuf[o] = bgra[p + 2] / 255f;             // R
                    inBuf[plane + o] = bgra[p + 1] / 255f;     // G
                    inBuf[2 * plane + o] = bgra[p] / 255f;     // B
                }
            }

            using var results = session.Run(new[] { NamedOnnxValue.CreateFromTensor(inputName, input) });
            var t = results[0].AsTensor<float>();
            var dense = t as DenseTensor<float> ?? t.ToDenseTensor();
            var ob = dense.Buffer.Span;

            var os = tile * scale;
            var oPlane = os * os;
            var cx = pad * scale;
            var cy = pad * scale;
            var validW = Math.Min(step, w - tx);
            var validH = Math.Min(step, h - ty);

            if (keepUpscale)
            {
                for (var y = 0; y < validH * scale; y++)
                {
                    var srcRow = (cy + y) * os;
                    var dstRow = (ty * scale + y) * outW;
                    for (var x = 0; x < validW * scale; x++)
                    {
                        var si = srcRow + cx + x;
                        var d = (dstRow + tx * scale + x) * 4;
                        dest[d] = ToByte(ob[2 * oPlane + si]);
                        dest[d + 1] = ToByte(ob[oPlane + si]);
                        dest[d + 2] = ToByte(ob[si]);
                        dest[d + 3] = 255;
                    }
                }
            }
            else
            {
                const float inv = 1f;
                for (var y = 0; y < validH; y++)
                for (var x = 0; x < validW; x++)
                {
                    float r = 0, g = 0, b = 0;
                    for (var by = 0; by < scale; by++)
                    {
                        var srcRow = (cy + y * scale + by) * os + cx + x * scale;
                        for (var bx = 0; bx < scale; bx++)
                        {
                            var si = srcRow + bx;
                            r += ob[si];
                            g += ob[oPlane + si];
                            b += ob[2 * oPlane + si];
                        }
                    }
                    var n = inv / (scale * scale);
                    var d = ((ty + y) * outW + tx + x) * 4;
                    dest[d] = ToByte(b * n);
                    dest[d + 1] = ToByte(g * n);
                    dest[d + 2] = ToByte(r * n);
                    dest[d + 3] = 255;
                }
            }

            done++;
            progress?.Report((double)done / total);
        }
        return dest;
    }

    /// <summary>Linear blend, so an effect can be dialed back instead of being all-or-nothing.</summary>
    public static byte[] Blend(byte[] original, byte[] processed, double strength)
    {
        var t = (float)Math.Clamp(strength, 0, 1);
        if (t >= 0.999f) return processed;
        var outp = new byte[original.Length];
        for (var i = 0; i < original.Length; i += 4)
        {
            outp[i] = (byte)(original[i] + (processed[i] - original[i]) * t);
            outp[i + 1] = (byte)(original[i + 1] + (processed[i + 1] - original[i + 1]) * t);
            outp[i + 2] = (byte)(original[i + 2] + (processed[i + 2] - original[i + 2]) * t);
            outp[i + 3] = 255;
        }
        return outp;
    }

    // ---------------- analysis (drives Autopilot) ----------------

    /// <summary>Cheap image statistics used to decide what an image actually needs.
    /// <c>Blur</c> is the variance of the Laplacian (low = soft/out-of-focus); <c>Noise</c> is the mean
    /// deviation from a 3x3 box blur, which is dominated by grain in flat areas.</summary>
    public static (double Blur, double Noise) Analyze(byte[] bgra, int w, int h)
    {
        // Work on luma, subsampled so this stays cheap on huge photos.
        var stepPx = Math.Max(1, Math.Max(w, h) / 1024);
        var sw = w / stepPx;
        var sh = h / stepPx;
        if (sw < 8 || sh < 8) return (1000, 0);

        var lum = new float[sw * sh];
        for (var y = 0; y < sh; y++)
        for (var x = 0; x < sw; x++)
        {
            var p = ((y * stepPx) * w + x * stepPx) * 4;
            lum[y * sw + x] = 0.114f * bgra[p] + 0.587f * bgra[p + 1] + 0.299f * bgra[p + 2];
        }

        double lapSum = 0, lapSq = 0, noiseSum = 0;
        var n = 0;
        for (var y = 1; y < sh - 1; y++)
        for (var x = 1; x < sw - 1; x++)
        {
            var i = y * sw + x;
            var lap = 4 * lum[i] - lum[i - 1] - lum[i + 1] - lum[i - sw] - lum[i + sw];
            lapSum += lap;
            lapSq += lap * lap;

            var mean = (lum[i] + lum[i - 1] + lum[i + 1] + lum[i - sw] + lum[i + sw]
                        + lum[i - sw - 1] + lum[i - sw + 1] + lum[i + sw - 1] + lum[i + sw + 1]) / 9.0;
            noiseSum += Math.Abs(lum[i] - mean);
            n++;
        }
        if (n == 0) return (1000, 0);
        var mean2 = lapSum / n;
        var blur = lapSq / n - mean2 * mean2;   // variance of Laplacian
        return (blur, noiseSum / n);
    }

    private static byte ToByte(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);

    public void Dispose()
    {
        foreach (var s in _sessions.Values) s.Dispose();
        _sessions.Clear();
    }
}
