using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Galileo.Services;

/// <summary>
/// Blind face restoration: <b>YuNet</b> finds faces (with 5 landmarks), each face is affine-aligned into the
/// 512x512 FFHQ frame that <b>CodeFormer</b> was trained on, restored, then warped back and feathered into
/// the photo.
///
/// The alignment is the part that matters: CodeFormer expects eyes/nose/mouth at canonical positions, so
/// simply cropping the detector's box and resizing gives visibly worse results. We solve the least-squares
/// similarity transform (rotation + uniform scale + translation) from the 5 detected landmarks onto the
/// template, which is also exactly invertible — so the restored face maps back onto the original geometry.
/// </summary>
public static class FaceRestore
{
    /// <summary>The canonical FFHQ 5-point template at 512px (facexlib) — the layout CodeFormer expects:
    /// right eye, left eye, nose, right mouth corner, left mouth corner (image-left first).</summary>
    private static readonly (float X, float Y)[] Template =
    {
        (192.98138f, 239.94708f),
        (318.90277f, 240.19366f),
        (256.63416f, 314.01935f),
        (201.26117f, 371.41043f),
        (313.08905f, 371.15118f),
    };

    private const int Size = 512;      // CodeFormer's fixed input
    private const int DetSize = 640;   // YuNet's fixed input
    private const float ScoreThreshold = 0.6f;
    private const float NmsIou = 0.3f;

    /// <summary>Faces smaller than this (px) are left alone. Blowing a tiny background face up to 512 gives
    /// the network almost nothing to work with, so it invents one — worse than leaving it untouched.</summary>
    private const float MinFaceSize = 40f;

    public sealed record Face(float Score, float X, float Y, float W, float H, (float X, float Y)[] Landmarks);

    /// <summary>Restores every detected face in-place on a copy of <paramref name="bgra"/>.
    /// <paramref name="fidelity"/> (CodeFormer's <c>w</c>): 0 = maximum quality/invention, 1 = stay closest
    /// to the original face. Returns the new pixels and how many faces were touched.</summary>
    public static byte[] Run(AiEngine engine, byte[] bgra, int w, int h, double fidelity,
        out int facesRestored, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var faces = DetectRestorable(engine, bgra, w, h, ct);
        facesRestored = 0;
        // Clone even on the no-op path: returning the caller's array made the undo snapshot and the new
        // source the same object, so a later in-place write would corrupt undo.
        if (faces.Count == 0) return (byte[])bgra.Clone();

        var outPix = (byte[])bgra.Clone();
        var spec = AiEngine.Catalog[AiModel.Face];

        for (var i = 0; i < faces.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var f = faces[i];

            // Solve image -> template. Skip degenerate detections (collapsed landmarks).
            if (!Similarity(f.Landmarks, Template, out var m)) continue;

            // Pull the aligned 512 face out of the source (inverse map + bilinear).
            var input = new DenseTensor<float>(new[] { 1, 3, Size, Size });
            var buf = input.Buffer.Span;
            const int plane = Size * Size;
            for (var v = 0; v < Size; v++)
            for (var u = 0; u < Size; u++)
            {
                m.Invert(u + 0.5f, v + 0.5f, out var sx, out var sy);
                Sample(bgra, w, h, sx - 0.5f, sy - 0.5f, out var r, out var g, out var b);
                var o = v * Size + u;
                // CodeFormer convention (verified): (value/255 - 0.5) / 0.5  =>  -1..1
                buf[o] = r / 127.5f - 1f;
                buf[plane + o] = g / 127.5f - 1f;
                buf[2 * plane + o] = b / 127.5f - 1f;
            }

            var wTensor = new DenseTensor<double>(new[] { Math.Clamp(fidelity, 0, 1) }, Array.Empty<int>());
            // Under the engine's lock, so the session can't be disposed mid-inference by the UI thread.
            var restored = engine.Use(AiModel.Face, session =>
            {
                using var results = session.Run(new[]
                {
                    NamedOnnxValue.CreateFromTensor(spec.Input, input),
                    NamedOnnxValue.CreateFromTensor("w", wTensor),
                });
                var y = results.FirstOrDefault(r => r.Name == "y") ?? results[0];
                var t = y.AsTensor<float>();
                return (t as DenseTensor<float> ?? t.ToDenseTensor()).Buffer.ToArray();
            });

            // Warp the restored face back and feather it in, so the seam doesn't show.
            PasteBack(outPix, w, h, restored, m);
            facesRestored++;
            progress?.Report((double)(i + 1) / faces.Count);
        }
        return outPix;
    }

    /// <summary>Blends the restored 512 face back into the photo. Iterating over the destination and mapping
    /// forward (image -> template) means every output pixel is filled — a scatter from the 512 grid would
    /// leave holes wherever the face is upscaled.</summary>
    private static void PasteBack(byte[] dest, int w, int h, ReadOnlySpan<float> face, in Sim m)
    {

        // Destination bounds = the 512 square's corners mapped back into image space.
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var (cu, cv) in new[] { (0f, 0f), (Size, 0f), (0f, (float)Size), ((float)Size, (float)Size) })
        {
            m.Invert(cu, cv, out var x, out var y);
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
        }
        var x0 = Math.Max(0, (int)MathF.Floor(minX));
        var y0 = Math.Max(0, (int)MathF.Floor(minY));
        var x1 = Math.Min(w - 1, (int)MathF.Ceiling(maxX));
        var y1 = Math.Min(h - 1, (int)MathF.Ceiling(maxY));

        for (var y = y0; y <= y1; y++)
        for (var x = x0; x <= x1; x++)
        {
            m.Apply(x + 0.5f, y + 0.5f, out var u, out var v);
            if (u < 0 || v < 0 || u >= Size - 1 || v >= Size - 1) continue;

            // Feather toward the edge of the aligned square so it melts into the surrounding photo.
            var edge = MathF.Min(MathF.Min(u, v), MathF.Min(Size - 1 - u, Size - 1 - v));
            var a = Math.Clamp(edge / 48f, 0f, 1f);
            if (a <= 0f) continue;

            BilinearPlanar(face, u, v, out var fr, out var fg, out var fb);
            var d = (y * w + x) * 4;
            dest[d] = (byte)Math.Clamp(dest[d] + (fb - dest[d]) * a, 0, 255);          // B
            dest[d + 1] = (byte)Math.Clamp(dest[d + 1] + (fg - dest[d + 1]) * a, 0, 255);
            dest[d + 2] = (byte)Math.Clamp(dest[d + 2] + (fr - dest[d + 2]) * a, 0, 255);
        }
    }

    /// <summary>Bilinear sample of the planar CHW face tensor, converting -1..1 back to 0..255.</summary>
    private static void BilinearPlanar(ReadOnlySpan<float> src, float u, float v,
        out float r, out float g, out float b)
    {
        const int plane = Size * Size;
        var xi = (int)u;
        var yi = (int)v;
        var fx = u - xi;
        var fy = v - yi;

        static float Ch(ReadOnlySpan<float> s, int c, int xi, int yi, float fx, float fy)
        {
            var o = c * plane;
            var p00 = s[o + yi * Size + xi];
            var p10 = s[o + yi * Size + xi + 1];
            var p01 = s[o + (yi + 1) * Size + xi];
            var p11 = s[o + (yi + 1) * Size + xi + 1];
            var top = p00 + (p10 - p00) * fx;
            var bot = p01 + (p11 - p01) * fx;
            return (top + (bot - top) * fy + 1f) * 127.5f;   // -1..1 -> 0..255
        }

        r = Ch(src, 0, xi, yi, fx, fy);
        g = Ch(src, 1, xi, yi, fx, fy);
        b = Ch(src, 2, xi, yi, fx, fy);
    }

    // ---------------- YuNet detection ----------------

    /// <summary>Faces that are actually worth restoring (see <see cref="MinFaceSize"/>).</summary>
    public static List<Face> DetectRestorable(AiEngine engine, byte[] bgra, int w, int h, CancellationToken ct = default)
        => Detect(engine, bgra, w, h, ct).Where(f => f.W >= MinFaceSize && f.H >= MinFaceSize).ToList();

    public static List<Face> Detect(AiEngine engine, byte[] bgra, int w, int h, CancellationToken ct = default)
    {
        // Letterbox into 640x640 (top-left, uniform scale) so aspect ratio is preserved.
        var scale = Math.Min((float)DetSize / w, (float)DetSize / h);
        var nw = Math.Max(1, (int)(w * scale));
        var nh = Math.Max(1, (int)(h * scale));

        var input = new DenseTensor<float>(new[] { 1, 3, DetSize, DetSize });
        var buf = input.Buffer.Span;
        const int plane = DetSize * DetSize;
        for (var y = 0; y < nh; y++)
        for (var x = 0; x < nw; x++)
        {
            Sample(bgra, w, h, x / scale, y / scale, out var r, out var g, out var b);
            var o = y * DetSize + x;
            buf[o] = b;                 // YuNet takes BGR, 0..255 (OpenCV blobFromImage, no scaling)
            buf[plane + o] = g;
            buf[2 * plane + o] = r;
        }

        ct.ThrowIfCancellationRequested();
        var outputs = engine.Use(AiModel.FaceDetect, session =>
        {
            using var results = session.Run(new[] { NamedOnnxValue.CreateFromTensor("input", input) });
            return results.ToDictionary(r => r.Name, r => r.AsTensor<float>().ToDenseTensor().Buffer.ToArray());
        });

        var faces = new List<Face>();
        foreach (var stride in new[] { 8, 16, 32 })
        {
            // A different export (names or anchor layout) should say so, not throw KeyNotFound/IndexOutOfRange
            // from deep inside the decode loop.
            if (!outputs.TryGetValue($"cls_{stride}", out var cls) ||
                !outputs.TryGetValue($"obj_{stride}", out var obj) ||
                !outputs.TryGetValue($"bbox_{stride}", out var box) ||
                !outputs.TryGetValue($"kps_{stride}", out var kps))
                throw new InvalidOperationException("The face-detection model has an unexpected output layout.");

            var grid = DetSize / stride;
            var anchors = grid * grid;
            if (cls.Length < anchors || obj.Length < anchors || box.Length < anchors * 4 || kps.Length < anchors * 10)
                throw new InvalidOperationException("The face-detection model has an unexpected anchor layout.");

            for (var i = 0; i < anchors; i++)
            {
                // YuNet's score is the geometric mean of the classification and objectness heads.
                var c = Math.Clamp(cls[i], 0f, 1f);
                var ob = Math.Clamp(obj[i], 0f, 1f);
                var score = MathF.Sqrt(c * ob);
                if (score < ScoreThreshold) continue;

                var col = i % grid;
                var row = i / grid;
                var cx = (col + box[i * 4 + 0]) * stride;
                var cy = (row + box[i * 4 + 1]) * stride;
                var bw = MathF.Exp(box[i * 4 + 2]) * stride;
                var bh = MathF.Exp(box[i * 4 + 3]) * stride;

                var lm = new (float X, float Y)[5];
                for (var k = 0; k < 5; k++)
                {
                    lm[k] = (((col + kps[i * 10 + 2 * k]) * stride) / scale,
                             ((row + kps[i * 10 + 2 * k + 1]) * stride) / scale);
                }
                faces.Add(new Face(score, (cx - bw / 2) / scale, (cy - bh / 2) / scale, bw / scale, bh / scale, lm));
            }
        }
        return Nms(faces);
    }

    private static List<Face> Nms(List<Face> faces)
    {
        var kept = new List<Face>();
        foreach (var f in faces.OrderByDescending(f => f.Score))
        {
            var overlaps = kept.Any(k =>
            {
                var ix = Math.Max(0, Math.Min(f.X + f.W, k.X + k.W) - Math.Max(f.X, k.X));
                var iy = Math.Max(0, Math.Min(f.Y + f.H, k.Y + k.H) - Math.Max(f.Y, k.Y));
                var inter = ix * iy;
                var union = f.W * f.H + k.W * k.H - inter;
                return union > 0 && inter / union > NmsIou;
            });
            if (!overlaps) kept.Add(f);
        }
        return kept;
    }

    // ---------------- geometry ----------------

    /// <summary>A similarity transform (uniform scale + rotation + translation): u = c·x − s·y + tx.</summary>
    internal readonly struct Sim
    {
        public readonly float C, S, Tx, Ty;
        public Sim(float c, float s, float tx, float ty) { C = c; S = s; Tx = tx; Ty = ty; }

        public void Apply(float x, float y, out float u, out float v)
        {
            u = C * x - S * y + Tx;
            v = S * x + C * y + Ty;
        }

        public void Invert(float u, float v, out float x, out float y)
        {
            var det = C * C + S * S;
            var du = u - Tx;
            var dv = v - Ty;
            x = (C * du + S * dv) / det;
            y = (-S * du + C * dv) / det;
        }
    }

    /// <summary>Closed-form least-squares similarity fit (the 2-D case of Umeyama): no SVD needed, because
    /// restricting to scale+rotation makes the normal equations solvable directly.</summary>
    private static bool Similarity((float X, float Y)[] src, (float X, float Y)[] dst, out Sim m)
    {
        m = default;
        var n = Math.Min(src.Length, dst.Length);
        if (n < 2) return false;

        float msx = 0, msy = 0, mdx = 0, mdy = 0;
        for (var i = 0; i < n; i++) { msx += src[i].X; msy += src[i].Y; mdx += dst[i].X; mdy += dst[i].Y; }
        msx /= n; msy /= n; mdx /= n; mdy /= n;

        float a = 0, b = 0, den = 0;
        for (var i = 0; i < n; i++)
        {
            float sx = src[i].X - msx, sy = src[i].Y - msy;
            float dx = dst[i].X - mdx, dy = dst[i].Y - mdy;
            a += sx * dx + sy * dy;
            b += sx * dy - sy * dx;
            den += sx * sx + sy * sy;
        }
        if (den < 1e-6f) return false;

        var c = a / den;
        var s = b / den;
        m = new Sim(c, s, mdx - (c * msx - s * msy), mdy - (s * msx + c * msy));
        return true;
    }

    /// <summary>Bilinear sample of the BGRA source, clamped at the edges.</summary>
    private static void Sample(byte[] bgra, int w, int h, float x, float y, out float r, out float g, out float b)
    {
        var xi = Math.Clamp((int)MathF.Floor(x), 0, w - 1);
        var yi = Math.Clamp((int)MathF.Floor(y), 0, h - 1);
        var xi2 = Math.Min(xi + 1, w - 1);
        var yi2 = Math.Min(yi + 1, h - 1);
        var fx = Math.Clamp(x - xi, 0, 1);
        var fy = Math.Clamp(y - yi, 0, 1);

        float Ch(int off)
        {
            var p00 = bgra[(yi * w + xi) * 4 + off];
            var p10 = bgra[(yi * w + xi2) * 4 + off];
            var p01 = bgra[(yi2 * w + xi) * 4 + off];
            var p11 = bgra[(yi2 * w + xi2) * 4 + off];
            var top = p00 + (p10 - p00) * fx;
            var bot = p01 + (p11 - p01) * fx;
            return top + (bot - top) * fy;
        }
        b = Ch(0); g = Ch(1); r = Ch(2);
    }
}
