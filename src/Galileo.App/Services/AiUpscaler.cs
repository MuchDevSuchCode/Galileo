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

/// <summary>
/// AI image enhancement backed by <b>Real-ESRGAN x4plus</b> (ONNX), run on the GPU through DirectML — so it
/// uses whatever DX12 card is present (NVIDIA/AMD/Intel) with no CUDA/cuDNN install. Falls back to CPU.
///
/// The network is fully convolutional but memory grows with tile area (the output is 16x the pixels), so the
/// image is processed in <b>overlapping tiles</b>; the overlap is discarded when stitching, which is what
/// keeps tile seams from showing.
///
/// Two operations, both driven by the same pass:
/// <list type="bullet">
/// <item><b>Enhance</b> — super-resolve each tile, then box-downsample it straight back to its original size.
/// Detail/denoise from the network is retained but the image doesn't grow, and no giant 4x intermediate is
/// ever allocated, so this works on full-resolution photos.</item>
/// <item><b>Upscale</b> — keep the 4x result (input is capped, since the output is 16x the pixels).</item>
/// </list>
/// Pixels are BGRA8 (Win2D's native layout); the model wants planar RGB float 0..1 (NCHW).
/// </summary>
public sealed class AiUpscaler : IDisposable
{
    public const string ModelUrl =
        "https://huggingface.co/fernandotonon/QtMeshEditor-realesrgan-onnx/resolve/main/RealESRGAN_x4plus.onnx";

    /// <summary>Long-edge cap on the input when keeping the 4x result (output is 16x the pixel count).</summary>
    public const int MaxUpscaleInputEdge = 1600;

    private static readonly string ModelDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Galileo", "models");

    public static string ModelPath => Path.Combine(ModelDir, "realesrgan-x4plus.onnx");

    /// <summary>The model is ~64MB; guard against a truncated/aborted download.</summary>
    public static bool ModelReady =>
        File.Exists(ModelPath) && new FileInfo(ModelPath).Length > 50_000_000;

    /// <summary>Fetches the model to a temp file and moves it into place, so an aborted download can't leave
    /// a half-written model behind.</summary>
    public static async Task DownloadModelAsync(IProgress<double>? progress, CancellationToken ct = default)
    {
        Directory.CreateDirectory(ModelDir);
        var tmp = ModelPath + ".part";
        if (File.Exists(tmp)) File.Delete(tmp);

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        using var resp = await http.GetAsync(ModelUrl, HttpCompletionOption.ResponseHeadersRead, ct);
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

        if (File.Exists(ModelPath)) File.Delete(ModelPath);
        File.Move(tmp, ModelPath);
    }

    private InferenceSession? _session;
    private int _fixedTile;   // >0 when the model was exported with a fixed input size

    /// <summary>"DirectML (GPU)" or "CPU" — surfaced in the UI so it's obvious the GPU is actually in use.</summary>
    public string Provider { get; private set; } = "CPU";

    private void EnsureSession()
    {
        if (_session is not null) return;
        if (!ModelReady) throw new InvalidOperationException("The AI model hasn't been downloaded yet.");

        try
        {
            var opts = new SessionOptions();
            opts.AppendExecutionProvider_DML(0);   // adapter 0 — the primary DX12 GPU
            opts.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
            _session = new InferenceSession(ModelPath, opts);
            Provider = "DirectML (GPU)";
        }
        catch (Exception ex)
        {
            App.LogInfo("AI: DirectML unavailable, using CPU: " + ex.Message);
            _session = new InferenceSession(ModelPath);
            Provider = "CPU";
        }

        // A PyTorch export usually has dynamic H/W; if it's fixed we must feed exactly that tile size.
        var dims = _session.InputMetadata.First().Value.Dimensions;
        _fixedTile = dims.Length == 4 && dims[2] > 0 ? dims[2] : 0;
    }

    /// <param name="keepUpscale">true = return the 4x image; false = return the original size (enhance).</param>
    public byte[] Run(byte[] bgra, int width, int height, bool keepUpscale,
        out int outWidth, out int outHeight, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        EnsureSession();

        // Big tiles keep the GPU busy; back off if the card can't allocate one.
        foreach (var tile in TileCandidates())
        {
            try
            {
                return RunTiled(bgra, width, height, keepUpscale, tile, out outWidth, out outHeight, progress, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (tile > 128)
            {
                App.LogInfo($"AI: tile {tile} failed ({ex.GetType().Name}), retrying smaller");
            }
        }
        throw new InvalidOperationException("AI enhancement failed — the model couldn't run on this image.");
    }

    private IEnumerable<int> TileCandidates()
    {
        if (_fixedTile > 0) { yield return _fixedTile; yield break; }
        yield return 512;
        yield return 256;
        yield return 128;
    }

    private byte[] RunTiled(byte[] bgra, int w, int h, bool keepUpscale, int tile,
        out int outW, out int outH, IProgress<double>? progress, CancellationToken ct)
    {
        const int scale = 4;                       // Real-ESRGAN x4
        var pad = Math.Max(8, tile / 16);          // overlap trimmed off each tile to hide seams
        var step = tile - 2 * pad;
        if (step <= 0) throw new InvalidOperationException("tile too small");

        outW = keepUpscale ? w * scale : w;
        outH = keepUpscale ? h * scale : h;
        var dest = new byte[outW * outH * 4];

        var inputName = _session!.InputMetadata.First().Key;
        var input = new DenseTensor<float>(new[] { 1, 3, tile, tile });
        var inBuf = input.Buffer.Span;
        var plane = tile * tile;

        var cols = (w + step - 1) / step;
        var rows = (h + step - 1) / step;
        var totalTiles = cols * rows;
        var done = 0;

        for (var ry = 0; ry < rows; ry++)
        {
            for (var rx = 0; rx < cols; rx++)
            {
                ct.ThrowIfCancellationRequested();
                var tx = rx * step;
                var ty = ry * step;

                // Fill the padded tile (planar RGB, 0..1), clamping at the image edges.
                for (var y = 0; y < tile; y++)
                {
                    var sy = Math.Clamp(ty - pad + y, 0, h - 1);
                    for (var x = 0; x < tile; x++)
                    {
                        var sx = Math.Clamp(tx - pad + x, 0, w - 1);
                        var p = (sy * w + sx) * 4;              // BGRA
                        var o = y * tile + x;
                        inBuf[o] = bgra[p + 2] / 255f;                 // R
                        inBuf[plane + o] = bgra[p + 1] / 255f;         // G
                        inBuf[2 * plane + o] = bgra[p] / 255f;         // B
                    }
                }

                using var results = _session.Run(new[] { NamedOnnxValue.CreateFromTensor(inputName, input) });
                var outT = results[0].AsTensor<float>();
                var dense = outT as DenseTensor<float> ?? outT.ToDenseTensor();
                var ob = dense.Buffer.Span;

                var os = tile * scale;             // SR tile side
                var oPlane = os * os;
                var cropX = pad * scale;
                var cropY = pad * scale;
                var validW = Math.Min(step, w - tx);
                var validH = Math.Min(step, h - ty);

                if (keepUpscale)
                {
                    for (var y = 0; y < validH * scale; y++)
                    {
                        var srcRow = (cropY + y) * os;
                        var dstRow = (ty * scale + y) * outW;
                        for (var x = 0; x < validW * scale; x++)
                        {
                            var si = srcRow + cropX + x;
                            var d = (dstRow + tx * scale + x) * 4;
                            dest[d] = ToByte(ob[2 * oPlane + si]);      // B
                            dest[d + 1] = ToByte(ob[oPlane + si]);      // G
                            dest[d + 2] = ToByte(ob[si]);               // R
                            dest[d + 3] = 255;
                        }
                    }
                }
                else
                {
                    // Box-downsample each 4x4 block of the SR tile straight back to one output pixel.
                    for (var y = 0; y < validH; y++)
                    {
                        for (var x = 0; x < validW; x++)
                        {
                            float r = 0, g = 0, b = 0;
                            for (var by = 0; by < scale; by++)
                            {
                                var srcRow = (cropY + y * scale + by) * os + cropX + x * scale;
                                for (var bx = 0; bx < scale; bx++)
                                {
                                    var si = srcRow + bx;
                                    r += ob[si];
                                    g += ob[oPlane + si];
                                    b += ob[2 * oPlane + si];
                                }
                            }
                            const float inv = 1f / (scale * scale);
                            var d = ((ty + y) * outW + tx + x) * 4;
                            dest[d] = ToByte(b * inv);
                            dest[d + 1] = ToByte(g * inv);
                            dest[d + 2] = ToByte(r * inv);
                            dest[d + 3] = 255;
                        }
                    }
                }

                done++;
                progress?.Report((double)done / totalTiles);
            }
        }
        return dest;
    }

    private static byte ToByte(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);

    public void Dispose()
    {
        _session?.Dispose();
        _session = null;
    }
}
