using System;
using System.Threading;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Galileo.Services;

/// <summary>
/// Content-aware fill (inpainting) with <b>LaMa</b>.
///
/// The network takes a fixed 512x512 image + mask, so we don't feed it the whole photo: we crop a square
/// region around the selection — deliberately larger than the selection, because the surrounding pixels are
/// the only context the model has to invent from — resize that to 512, inpaint, scale back, and composite
/// only the selected pixels in. Everything outside the selection is left bit-for-bit untouched, and the mask
/// is feathered at its edge so the fill doesn't show a hard seam.
///
/// Runs on the CPU: DirectML loads this graph but fails at execution inside its Fourier unit (see
/// <see cref="ModelSpec.Cpu"/>). ~2s for a fill, which is fine for a deliberate one-shot operation.
/// </summary>
public static class Inpaint
{
    private const int S = 512;               // LaMa's fixed input
    private const float ContextFactor = 2.0f; // crop this many times the selection's size, for context
    private const int Feather = 3;            // px of soft edge on the composite
    private const int Dilate = 4;             // px the selection is grown before filling

    /// <summary>Fills the masked region of <paramref name="bgra"/> (255 = fill, 0 = keep) and returns new
    /// pixels. Returns the input unchanged if the mask is empty.</summary>
    public static byte[] Fill(AiEngine engine, byte[] bgra, int w, int h, byte[] mask,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        // 0. Grow the selection slightly. Things people erase — watermarks, text, wires — have a soft,
        // anti-aliased fringe a pixel or two outside their visible edge; leaving it behind is what makes a
        // removal look "nearly clean". Painting over a bit of good pixel costs nothing (the model just
        // regenerates it), so err outward.
        mask = Dilated(mask, w, h, Dilate);

        // 1. Bounds of the selection.
        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            if (mask[y * w + x] == 0) continue;
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }
        if (maxX < 0) return bgra;   // nothing selected
        progress?.Report(0.1);

        // 2. A square crop around it, enlarged for context and clamped to the image.
        // Never crop SMALLER than the model's own 512 window: a small watermark on a big photo then maps
        // 1:1 into the network (no resampling at all), which is what keeps the fill sharp instead of
        // blurring a downscaled patch back up. Only a selection bigger than 512 forces a downscale.
        var selW = maxX - minX + 1;
        var selH = maxY - minY + 1;
        var side = (int)Math.Ceiling(Math.Max(selW, selH) * ContextFactor);
        side = Math.Max(side, S);
        side = Math.Clamp(side, 64, Math.Min(w, h));
        var cx = (minX + maxX) / 2;
        var cy = (minY + maxY) / 2;
        var rx = Math.Clamp(cx - side / 2, 0, Math.Max(0, w - side));
        var ry = Math.Clamp(cy - side / 2, 0, Math.Max(0, h - side));
        var rw = Math.Min(side, w - rx);
        var rh = Math.Min(side, h - ry);

        // 3. Resize the crop (and its mask) to the model's 512x512.
        var img = new DenseTensor<float>(new[] { 1, 3, S, S });
        var msk = new DenseTensor<float>(new[] { 1, 1, S, S });
        var ib = img.Buffer.Span;
        var mb = msk.Buffer.Span;
        const int plane = S * S;
        for (var y = 0; y < S; y++)
        {
            var sy = Math.Min(h - 1, ry + (int)((y + 0.5f) * rh / S));
            for (var x = 0; x < S; x++)
            {
                var sx = Math.Min(w - 1, rx + (int)((x + 0.5f) * rw / S));
                var p = (sy * w + sx) * 4;
                var o = y * S + x;
                ib[o] = bgra[p + 2] / 255f;             // R
                ib[plane + o] = bgra[p + 1] / 255f;     // G
                ib[2 * plane + o] = bgra[p] / 255f;     // B
                mb[o] = mask[sy * w + sx] != 0 ? 1f : 0f;   // 1 = hole
            }
        }
        ct.ThrowIfCancellationRequested();
        progress?.Report(0.3);

        // 4. Inpaint.
        var session = engine.Session(AiModel.Inpaint);
        using var results = session.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor("image", img),
            NamedOnnxValue.CreateFromTensor("mask", msk),
        });
        var t = results[0].AsTensor<float>();
        var dense = t as DenseTensor<float> ?? t.ToDenseTensor();
        var ob = dense.Buffer.Span;

        // This export emits 0..255 (verified); tolerate a 0..1 export too.
        var max = 0f;
        foreach (var v in ob) if (v > max) max = v;
        var scale = max > 1.5f ? 1f : 255f;
        progress?.Report(0.8);

        // 5. Feather the mask so the fill blends instead of showing a hard cut.
        var soft = Feathered(mask, w, h, rx, ry, rw, rh);

        // 6. Composite: only masked pixels change; everything else is byte-identical to the original.
        var outPix = (byte[])bgra.Clone();
        for (var y = 0; y < rh; y++)
        {
            var iy = y * S / rh;
            for (var x = 0; x < rw; x++)
            {
                var a = soft[y * rw + x];
                if (a <= 0f) continue;
                var ix = x * S / rw;
                var o = iy * S + ix;
                var r = Math.Clamp(ob[o] * scale, 0, 255);
                var g = Math.Clamp(ob[plane + o] * scale, 0, 255);
                var b = Math.Clamp(ob[2 * plane + o] * scale, 0, 255);

                var d = ((ry + y) * w + rx + x) * 4;
                outPix[d] = (byte)(outPix[d] + (b - outPix[d]) * a);
                outPix[d + 1] = (byte)(outPix[d + 1] + (g - outPix[d + 1]) * a);
                outPix[d + 2] = (byte)(outPix[d + 2] + (r - outPix[d + 2]) * a);
            }
        }
        progress?.Report(1.0);
        return outPix;
    }

    /// <summary>Grows the mask by <paramref name="r"/> px (separable max filter — a square dilation).</summary>
    private static byte[] Dilated(byte[] mask, int w, int h, int r)
    {
        if (r <= 0) return mask;
        var tmp = new byte[w * h];
        var outp = new byte[w * h];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            byte m = 0;
            for (var d = -r; d <= r && m == 0; d++)
            {
                var xx = x + d;
                if (xx >= 0 && xx < w && mask[y * w + xx] != 0) m = 255;
            }
            tmp[y * w + x] = m;
        }
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            byte m = 0;
            for (var d = -r; d <= r && m == 0; d++)
            {
                var yy = y + d;
                if (yy >= 0 && yy < h && tmp[yy * w + x] != 0) m = 255;
            }
            outp[y * w + x] = m;
        }
        return outp;
    }

    /// <summary>The region's mask as 0..1 alpha with a soft edge (box-blurred), so the composite has no seam.</summary>
    private static float[] Feathered(byte[] mask, int w, int h, int rx, int ry, int rw, int rh)
    {
        var a = new float[rw * rh];
        for (var y = 0; y < rh; y++)
        for (var x = 0; x < rw; x++)
            a[y * rw + x] = mask[(ry + y) * w + rx + x] != 0 ? 1f : 0f;

        var tmp = new float[rw * rh];
        for (var pass = 0; pass < 2; pass++)   // two box passes ≈ a smooth falloff
        {
            for (var y = 0; y < rh; y++)
            for (var x = 0; x < rw; x++)
            {
                float sum = 0;
                var n = 0;
                for (var dy = -Feather; dy <= Feather; dy++)
                {
                    var yy = y + dy;
                    if (yy < 0 || yy >= rh) continue;
                    for (var dx = -Feather; dx <= Feather; dx++)
                    {
                        var xx = x + dx;
                        if (xx < 0 || xx >= rw) continue;
                        sum += a[yy * rw + xx];
                        n++;
                    }
                }
                tmp[y * rw + x] = n > 0 ? sum / n : 0f;
            }
            Array.Copy(tmp, a, a.Length);
        }
        return a;
    }
}
