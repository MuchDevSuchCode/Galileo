using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Galileo.Services;

/// <summary>
/// Finds text in a photo (watermarks, captions, timestamps) and returns it as a selection mask, ready for
/// <see cref="Inpaint"/>. Uses the PP-OCRv3 <b>DB</b> detector, whose output is a per-pixel probability map —
/// so the detector's output <em>is</em> a mask, no box-fitting needed.
///
/// The one catch, and the reason this isn't just "threshold the map": DB is trained to predict a
/// <b>shrunk core</b> of each text line, not the glyphs themselves. Filling that raw would leave the tops and
/// bottoms of every letter behind. So each detected region is grown back out (proportionally to its own
/// height, so it works for any text size) to cover the full glyphs plus a margin.
/// </summary>
public static class TextDetect
{
    /// <summary>Detection runs at this long edge (rounded to a multiple of 32, as the net requires). Big
    /// enough for small watermarks, small enough to stay fast on a 50MP photo.</summary>
    private const int MaxSide = 1280;

    private const float Threshold = 0.3f;   // DB's standard probability cut
    private const int MinArea = 20;         // ignore speckle

    /// <summary>Returns a mask (255 = text) the same size as the image, or null if no text was found.</summary>
    public static byte[]? DetectMask(AiEngine engine, byte[] bgra, int w, int h,
        out int regions, CancellationToken ct = default)
    {
        regions = 0;

        // Work at a bounded resolution, rounded to the multiple of 32 the network requires.
        var scale = Math.Min(1.0, (double)MaxSide / Math.Max(w, h));
        var dw = Math.Max(32, (int)Math.Round(w * scale / 32) * 32);
        var dh = Math.Max(32, (int)Math.Round(h * scale / 32) * 32);

        var input = new DenseTensor<float>(new[] { 1, 3, dh, dw });
        var buf = input.Buffer.Span;
        var plane = dh * dw;
        // ImageNet normalisation, RGB — what PP-OCR was trained with.
        float[] mean = { 0.485f, 0.456f, 0.406f };
        float[] std = { 0.229f, 0.224f, 0.225f };

        for (var y = 0; y < dh; y++)
        {
            var sy = Math.Min(h - 1, y * h / dh);
            for (var x = 0; x < dw; x++)
            {
                var sx = Math.Min(w - 1, x * w / dw);
                var p = (sy * w + sx) * 4;
                var o = y * dw + x;
                buf[o] = (bgra[p + 2] / 255f - mean[0]) / std[0];              // R
                buf[plane + o] = (bgra[p + 1] / 255f - mean[1]) / std[1];      // G
                buf[2 * plane + o] = (bgra[p] / 255f - mean[2]) / std[2];      // B
            }
        }

        ct.ThrowIfCancellationRequested();
        var session = engine.Session(AiModel.TextDetect);
        var inputName = session.InputMetadata.Keys.First();
        using var results = session.Run(new[] { NamedOnnxValue.CreateFromTensor(inputName, input) });
        var t = results[0].AsTensor<float>();
        var dims = t.Dimensions.ToArray();
        var prob = (t as DenseTensor<float> ?? t.ToDenseTensor()).Buffer.ToArray();
        var mw = dims[^1];
        var mh = dims[^2];

        // Threshold, then grow each text region back to full glyph height (see class remarks).
        var bin = new bool[mw * mh];
        for (var i = 0; i < bin.Length && i < prob.Length; i++) bin[i] = prob[i] > Threshold;

        var det = new byte[mw * mh];
        var seen = new bool[mw * mh];
        var queue = new Queue<int>();

        for (var i = 0; i < bin.Length; i++)
        {
            if (!bin[i] || seen[i]) continue;
            int x0 = i % mw, x1 = x0, y0 = i / mw, y1 = y0, count = 0;
            queue.Clear();
            queue.Enqueue(i);
            seen[i] = true;
            while (queue.Count > 0)
            {
                var p = queue.Dequeue();
                count++;
                int px = p % mw, py = p / mw;
                if (px < x0) x0 = px;
                if (px > x1) x1 = px;
                if (py < y0) y0 = py;
                if (py > y1) y1 = py;
                if (px > 0 && bin[p - 1] && !seen[p - 1]) { seen[p - 1] = true; queue.Enqueue(p - 1); }
                if (px < mw - 1 && bin[p + 1] && !seen[p + 1]) { seen[p + 1] = true; queue.Enqueue(p + 1); }
                if (py > 0 && bin[p - mw] && !seen[p - mw]) { seen[p - mw] = true; queue.Enqueue(p - mw); }
                if (py < mh - 1 && bin[p + mw] && !seen[p + mw]) { seen[p + mw] = true; queue.Enqueue(p + mw); }
            }
            if (count < MinArea) continue;

            // Grow relative to the band's own height, so this scales with the text size.
            var bh = y1 - y0 + 1;
            var padY = (int)(bh * 1.2);
            var padX = (int)(bh * 0.6);
            var ex0 = Math.Max(0, x0 - padX);
            var ex1 = Math.Min(mw - 1, x1 + padX);
            var ey0 = Math.Max(0, y0 - padY);
            var ey1 = Math.Min(mh - 1, y1 + padY);
            for (var y = ey0; y <= ey1; y++)
            for (var x = ex0; x <= ex1; x++)
                det[y * mw + x] = 255;
            regions++;
        }

        if (regions == 0) return null;

        // Back up to full resolution.
        var mask = new byte[w * h];
        for (var y = 0; y < h; y++)
        {
            var my = Math.Min(mh - 1, y * mh / h);
            for (var x = 0; x < w; x++)
            {
                var mx = Math.Min(mw - 1, x * mw / w);
                mask[y * w + x] = det[my * mw + mx];
            }
        }
        return mask;
    }
}
