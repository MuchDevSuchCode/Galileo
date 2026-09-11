using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Galileo.Services;

/// <summary>
/// The retoucher's eye fix: mirror the BETTER eye onto the other one, at native resolution.
///
/// This replaced a CodeFormer-based approach that failed on exactly the photos people want fixed:
/// close-ups. CodeFormer works in a fixed 512px aligned frame, so a 200px-wide eye was regenerated
/// at ~90px and stretched back — soft, glassy, and wearing an invented catchlight. Mirroring is
/// deterministic, loses no detail at any zoom, and matches what a human retoucher does for a
/// malformed or asymmetric eye. Gaze is preserved by re-centring the mirrored patch on the target
/// eye's own iris position; the lighting difference between the two sides of the face is handled
/// by per-channel tone matching; the paste is a feathered ellipse so no seam shows.
/// Validated pixel-for-pixel against a reference photo before shipping.
/// </summary>
public static class EyeFix
{
    /// <summary>Mirrors one eye onto the other, in place. <paramref name="eyeL"/> / <paramref name="eyeR"/>
    /// are the two eye-centre positions (image-left first). <paramref name="target"/> chooses which one
    /// gets fixed: <c>Auto</c> = the blurrier eye (for auto-detected use); <c>Left</c>/<c>Right</c> = the
    /// user explicitly said which eye to fix (click-to-target). <paramref name="requireIris"/> runs the
    /// safety gate — keep it on for auto detection, off when the user clicked the spot themselves.
    /// Returns false when the eyes are too small/close or (auto) not confidently irises.</summary>
    public enum TargetEye { Auto, Left, Right }

    public static bool MirrorFix(byte[] bgra, int w, int h, (float X, float Y) eyeL, (float X, float Y) eyeR,
        TargetEye target = TargetEye.Auto, bool requireIris = true)
    {
        // Face-local frame: û along the eye axis, v̂ perpendicular (handles tilted heads).
        float dx = eyeR.X - eyeL.X, dy = eyeR.Y - eyeL.Y;
        var d = MathF.Sqrt(dx * dx + dy * dy);
        if (d < 24) return false;                    // eyes too small to work on
        float ux = dx / d, uy = dy / d;
        float vx = -uy, vy = ux;

        // SAFETY GATE (auto detection only). YuNet's landmarks drift badly on tilted / partially-cropped
        // faces — a wrong pair once put a mirrored mini-eye on the subject's nose bridge. Refuse unless
        // BOTH positions actually sit on an iris (dark disc ringed by brighter sclera/skin). Skipped when
        // the user clicked the eyes themselves and each click was already snapped to its iris.
        if (requireIris)
        {
            var probe = 0.11f * d;
            if (DiscContrast(bgra, w, h, eyeL.X, eyeL.Y, probe) > -8f) return false;
            if (DiscContrast(bgra, w, h, eyeR.X, eyeR.Y, probe) > -8f) return false;
        }

        // Region ellipse (in the local frame), sized from the interocular distance.
        var rx = 0.33f * d;
        var ry = 0.24f * d;

        // Which eye is the TARGET (gets replaced)? Explicit when the user clicked it; otherwise the
        // blurrier eye — the malformed one in damaged/AI photos is consistently the smearier (Laplacian
        // variance), so the sharper one is the source template.
        bool targetIsLeft;
        if (target == TargetEye.Left) targetIsLeft = true;
        else if (target == TargetEye.Right) targetIsLeft = false;
        else
        {
            var sharpL = Sharpness(bgra, w, h, eyeL, ux, uy, vx, vy, rx, ry);
            var sharpR = Sharpness(bgra, w, h, eyeR, ux, uy, vx, vy, rx, ry);
            targetIsLeft = sharpL < sharpR;   // fix the blurrier one
        }
        var dst = targetIsLeft ? eyeL : eyeR;
        var src = targetIsLeft ? eyeR : eyeL;

        // Iris centres (local-frame offsets from each eye landmark): centroid of the darkest pixels.
        var irisS = IrisOffset(bgra, w, h, src, ux, uy, vx, vy, rx, ry);
        var irisT = IrisOffset(bgra, w, h, dst, ux, uy, vx, vy, rx, ry);

        // Sampling map: an output pixel at local (a,b) around the TARGET eye reads the SOURCE at
        // (-a + shiftA, b + shiftB) — a horizontal mirror in the face frame, shifted so the
        // mirrored source iris lands exactly on the target's own iris position (gaze preserved).
        var shiftA = irisS.A + irisT.A;
        var shiftB = irisS.B - irisT.B;

        // Tone statistics of both regions (per channel) for the lighting match — one side of a
        // face is very often darker than the other.
        Span<double> sumS = stackalloc double[3], sumS2 = stackalloc double[3];
        Span<double> sumT = stackalloc double[3], sumT2 = stackalloc double[3];
        double n = 0;
        var bound = (int)MathF.Ceiling(MathF.Max(rx, ry)) + 2;
        var cx = (int)dst.X; var cy = (int)dst.Y;
        for (var py = cy - bound; py <= cy + bound; py++)
        for (var px = cx - bound; px <= cx + bound; px++)
        {
            if (px < 0 || py < 0 || px >= w || py >= h) continue;
            var (a, b) = Local(px + 0.5f, py + 0.5f, dst, ux, uy, vx, vy);
            if (Ellipse(a, b, rx, ry) >= 1f) continue;
            var di = (py * w + px) * 4;
            sumT[0] += bgra[di]; sumT2[0] += bgra[di] * bgra[di];
            sumT[1] += bgra[di + 1]; sumT2[1] += bgra[di + 1] * bgra[di + 1];
            sumT[2] += bgra[di + 2]; sumT2[2] += bgra[di + 2] * bgra[di + 2];
            var sxp = src.X + (-a + shiftA) * ux + (b + shiftB) * vx;
            var syp = src.Y + (-a + shiftA) * uy + (b + shiftB) * vy;
            Sample(bgra, w, h, sxp, syp, out var r, out var g, out var bb);
            sumS[0] += bb; sumS2[0] += bb * bb;
            sumS[1] += g; sumS2[1] += g * g;
            sumS[2] += r; sumS2[2] += r * r;
            n++;
        }
        Span<float> gain = stackalloc float[3], off = stackalloc float[3];
        gain[0] = gain[1] = gain[2] = 1;
        if (n > 64)
            for (var c = 0; c < 3; c++)
            {
                var mS = sumS[c] / n; var mT = sumT[c] / n;
                var sS = Math.Sqrt(Math.Max(1, sumS2[c] / n - mS * mS));
                var sT = Math.Sqrt(Math.Max(1, sumT2[c] / n - mT * mT));
                gain[c] = (float)Math.Clamp(sT / sS, 0.7, 1.4);   // capped: matching must not distort structure
                off[c] = (float)(mT - gain[c] * mS);
            }

        // Feathered, mirrored, iris-aligned, tone-matched paste. The paste reads from the SOURCE
        // region, which the loop never writes (it only writes inside the target ellipse), so
        // sampling in place is safe.
        for (var py = cy - bound; py <= cy + bound; py++)
        for (var px = cx - bound; px <= cx + bound; px++)
        {
            if (px < 0 || py < 0 || px >= w || py >= h) continue;
            var (a, b) = Local(px + 0.5f, py + 0.5f, dst, ux, uy, vx, vy);
            var e = Ellipse(a, b, rx, ry);
            if (e >= 1f) continue;
            var alpha = e <= 0.55f ? 1f : 1f - Smooth((e - 0.55f) / 0.45f);

            var sxp = src.X + (-a + shiftA) * ux + (b + shiftB) * vx;
            var syp = src.Y + (-a + shiftA) * uy + (b + shiftB) * vy;
            Sample(bgra, w, h, sxp, syp, out var r, out var g, out var bb);
            bb = bb * gain[0] + off[0];
            g = g * gain[1] + off[1];
            r = r * gain[2] + off[2];

            var di = (py * w + px) * 4;
            bgra[di] = (byte)Math.Clamp(bgra[di] + (bb - bgra[di]) * alpha, 0, 255);
            bgra[di + 1] = (byte)Math.Clamp(bgra[di + 1] + (g - bgra[di + 1]) * alpha, 0, 255);
            bgra[di + 2] = (byte)Math.Clamp(bgra[di + 2] + (r - bgra[di + 2]) * alpha, 0, 255);
        }
        return true;
    }

    // ---------------- AI restore (GPEN-BFR-1024) — for when NEITHER eye is good ----------------

    private const int GpenN = 1024;
    // FFHQ 5-point template's eye positions, scaled to 1024 (the 512 template ×2).
    private static readonly (float X, float Y) TplEyeL = (385.96276f, 479.89416f);
    private static readonly (float X, float Y) TplEyeR = (637.8055f, 480.38732f);
    private const float GpenERx = 95f, GpenERy = 62f;   // eye-window half-axes in 1024 template space

    /// <summary>Generatively restores BOTH eyes with GPEN-BFR-1024 and composites only the eye
    /// regions back — for photos where neither eye is a good enough template to mirror. The face is
    /// aligned into GPEN's 1024 frame from the two USER-CLICKED eye positions (exact — no detector),
    /// GPEN regenerates at native 1024 (so a close-up eye stays crisp), and each restored eye is
    /// feathered + tone-matched back onto the original. Returns false on degenerate input.</summary>
    public static bool GpenRestore(AiEngine engine, byte[] bgra, int w, int h,
        (float X, float Y) eyeL, (float X, float Y) eyeR)
    {
        float dx = eyeR.X - eyeL.X, dy = eyeR.Y - eyeL.Y;
        if (MathF.Sqrt(dx * dx + dy * dy) < 24) return false;
        if (!SimilarityFit(new[] { eyeL, eyeR }, new[] { TplEyeL, TplEyeR }, out var m)) return false;

        // Pull the aligned 1024 face (GPEN convention: RGB, (v/255-0.5)/0.5 → [-1,1], NCHW).
        var input = new DenseTensor<float>(new[] { 1, 3, GpenN, GpenN });
        var buf = input.Buffer.Span; const int plane = GpenN * GpenN;
        for (var v = 0; v < GpenN; v++)
        for (var u = 0; u < GpenN; u++)
        {
            m.Invert(u + 0.5f, v + 0.5f, out var sx, out var sy);
            SampleBgra(bgra, w, h, sx - 0.5f, sy - 0.5f, out var r, out var g, out var b);
            var o = v * GpenN + u;
            buf[o] = r / 127.5f - 1f; buf[plane + o] = g / 127.5f - 1f; buf[2 * plane + o] = b / 127.5f - 1f;
        }

        var spec = AiEngine.Catalog[AiModel.FaceHiRes];
        var face = engine.Use(AiModel.FaceHiRes, session =>
        {
            using var results = session.Run(new[] { NamedOnnxValue.CreateFromTensor(spec.Input, input) });
            var t = results.First().AsTensor<float>();
            return (t as DenseTensor<float> ?? t.ToDenseTensor()).Buffer.ToArray();
        });

        PasteGpenEye(bgra, w, h, face, m, TplEyeL);
        PasteGpenEye(bgra, w, h, face, m, TplEyeR);
        return true;
    }

    private static float GpenMaskAlpha(float u, float v, (float X, float Y) c)
    {
        var dx = (u - c.X) / GpenERx; var dy = (v - c.Y) / GpenERy;
        var d = MathF.Sqrt(dx * dx + dy * dy);
        if (d <= 0.62f) return 1f;
        if (d >= 1f) return 0f;
        var s = (d - 0.62f) / 0.38f;
        return 1f - s * s * (3f - 2f * s);
    }

    private static void PasteGpenEye(byte[] dest, int w, int h, float[] face, in Sim m, (float X, float Y) tpl)
    {
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var (cu, cv) in new[] { (tpl.X - GpenERx, tpl.Y - GpenERy), (tpl.X + GpenERx, tpl.Y - GpenERy), (tpl.X - GpenERx, tpl.Y + GpenERy), (tpl.X + GpenERx, tpl.Y + GpenERy) })
        { m.Invert(cu, cv, out var x, out var y); minX = MathF.Min(minX, x); maxX = MathF.Max(maxX, x); minY = MathF.Min(minY, y); maxY = MathF.Max(maxY, y); }
        int x0 = Math.Max(0, (int)minX), y0 = Math.Max(0, (int)minY), x1 = Math.Min(w - 1, (int)maxX + 1), y1 = Math.Min(h - 1, (int)maxY + 1);

        // Per-channel tone match to the original eye window (invented highlights / exposure shift).
        Span<double> sO = stackalloc double[3], sO2 = stackalloc double[3], sR = stackalloc double[3], sR2 = stackalloc double[3];
        double n = 0;
        for (var y = y0; y <= y1; y++) for (var x = x0; x <= x1; x++)
        {
            m.Apply(x + 0.5f, y + 0.5f, out var u, out var v);
            if (u < 0 || v < 0 || u >= GpenN - 1 || v >= GpenN - 1 || GpenMaskAlpha(u, v, tpl) < 0.5f) continue;
            GpenBilinear(face, u, v, out var fr, out var fg, out var fb);
            var d = (y * w + x) * 4;
            sO[0] += dest[d]; sO2[0] += dest[d] * dest[d]; sR[0] += fb; sR2[0] += fb * fb;
            sO[1] += dest[d + 1]; sO2[1] += dest[d + 1] * dest[d + 1]; sR[1] += fg; sR2[1] += fg * fg;
            sO[2] += dest[d + 2]; sO2[2] += dest[d + 2] * dest[d + 2]; sR[2] += fr; sR2[2] += fr * fr;
            n++;
        }
        Span<float> gain = stackalloc float[3], off = stackalloc float[3]; gain[0] = gain[1] = gain[2] = 1;
        if (n > 64) for (var c = 0; c < 3; c++)
        {
            var mO = sO[c] / n; var mR = sR[c] / n;
            var dO = Math.Sqrt(Math.Max(1, sO2[c] / n - mO * mO)); var dR = Math.Sqrt(Math.Max(1, sR2[c] / n - mR * mR));
            gain[c] = (float)Math.Clamp(dO / dR, 0.6, 1.5); off[c] = (float)(mO - gain[c] * mR);
        }

        for (var y = y0; y <= y1; y++) for (var x = x0; x <= x1; x++)
        {
            m.Apply(x + 0.5f, y + 0.5f, out var u, out var v);
            if (u < 0 || v < 0 || u >= GpenN - 1 || v >= GpenN - 1) continue;
            var a = GpenMaskAlpha(u, v, tpl); if (a <= 0f) continue;
            GpenBilinear(face, u, v, out var fr, out var fg, out var fb);
            fb = fb * gain[0] + off[0]; fg = fg * gain[1] + off[1]; fr = fr * gain[2] + off[2];
            var d = (y * w + x) * 4;
            dest[d] = (byte)Math.Clamp(dest[d] + (fb - dest[d]) * a, 0, 255);
            dest[d + 1] = (byte)Math.Clamp(dest[d + 1] + (fg - dest[d + 1]) * a, 0, 255);
            dest[d + 2] = (byte)Math.Clamp(dest[d + 2] + (fr - dest[d + 2]) * a, 0, 255);
        }
    }

    private static void GpenBilinear(float[] s, float u, float v, out float r, out float g, out float b)
    {
        const int plane = GpenN * GpenN; int xi = (int)u, yi = (int)v; float fx = u - xi, fy = v - yi;
        float Ch(int c)
        {
            var o = c * plane;
            float p00 = s[o + yi * GpenN + xi], p10 = s[o + yi * GpenN + xi + 1];
            float p01 = s[o + (yi + 1) * GpenN + xi], p11 = s[o + (yi + 1) * GpenN + xi + 1];
            var tp = p00 + (p10 - p00) * fx; var bt = p01 + (p11 - p01) * fx;
            return (tp + (bt - tp) * fy + 1f) * 127.5f;   // [-1,1] → [0,255]
        }
        r = Ch(0); g = Ch(1); b = Ch(2);
    }

    private static void SampleBgra(byte[] bgra, int w, int h, float x, float y, out float r, out float g, out float b)
        => Sample(bgra, w, h, x, y, out r, out g, out b);

    /// <summary>A similarity transform (uniform scale + rotation + translation).</summary>
    public readonly struct Sim
    {
        public readonly float C, S, Tx, Ty;
        public Sim(float c, float s, float tx, float ty) { C = c; S = s; Tx = tx; Ty = ty; }
        public void Apply(float x, float y, out float u, out float v) { u = C * x - S * y + Tx; v = S * x + C * y + Ty; }
        public void Invert(float u, float v, out float x, out float y)
        { var det = C * C + S * S; var du = u - Tx; var dv = v - Ty; x = (C * du + S * dv) / det; y = (-S * du + C * dv) / det; }
    }

    /// <summary>Least-squares similarity fit (exact for 2 point pairs) mapping src→dst.</summary>
    private static bool SimilarityFit((float X, float Y)[] src, (float X, float Y)[] dst, out Sim m)
    {
        m = default; var n = Math.Min(src.Length, dst.Length); if (n < 2) return false;
        float msx = 0, msy = 0, mdx = 0, mdy = 0;
        for (var i = 0; i < n; i++) { msx += src[i].X; msy += src[i].Y; mdx += dst[i].X; mdy += dst[i].Y; }
        msx /= n; msy /= n; mdx /= n; mdy /= n;
        float a = 0, b = 0, den = 0;
        for (var i = 0; i < n; i++)
        { float sx = src[i].X - msx, sy = src[i].Y - msy, dx = dst[i].X - mdx, dy = dst[i].Y - mdy; a += sx * dx + sy * dy; b += sx * dy - sy * dx; den += sx * sx + sy * sy; }
        if (den < 1e-6f) return false;
        var c = a / den; var s = b / den;
        m = new Sim(c, s, mdx - (c * msx - s * msy), mdy - (s * msx + c * msy));
        return true;
    }

    /// <summary>Refines a clicked point onto the nearest iris centre — the darkest disc (relative to
    /// its surround) within <paramref name="searchR"/> px of the click. <paramref name="irisR"/> is the
    /// expected iris radius (roughly eyeWidth/4). Lets a rough click become a precise, gaze-correct
    /// anchor without any face detector in the loop.</summary>
    public static (float X, float Y) SnapToIris(byte[] bgra, int w, int h, float clickX, float clickY,
        float searchR, float irisR)
    {
        var best = (X: clickX, Y: clickY);
        var bestScore = float.MaxValue;
        var step = MathF.Max(1f, irisR / 4f);
        for (var oy = -searchR; oy <= searchR; oy += step)
        for (var ox = -searchR; ox <= searchR; ox += step)
        {
            var cx = clickX + ox; var cy = clickY + oy;
            var s = DiscContrast(bgra, w, h, cx, cy, irisR)
                    + 4f * MathF.Sqrt(ox * ox + oy * oy) / MathF.Max(1f, searchR); // slight pull to the click
            if (s < bestScore) { bestScore = s; best = (cx, cy); }
        }
        // 1px local refine.
        for (var oy = -2f; oy <= 2f; oy += 1f)
        for (var ox = -2f; ox <= 2f; ox += 1f)
        {
            var s = DiscContrast(bgra, w, h, best.X + ox, best.Y + oy, irisR);
            if (s < bestScore) { bestScore = s; best = (best.X + ox, best.Y + oy); }
        }
        return best;
    }

    private static (float A, float B) Local(float x, float y, (float X, float Y) c, float ux, float uy, float vx, float vy)
    {
        var ox = x - c.X; var oy = y - c.Y;
        return (ox * ux + oy * uy, ox * vx + oy * vy);
    }

    private static float Ellipse(float a, float b, float rx, float ry)
    {
        var na = a / rx; var nb = b / ry;
        return MathF.Sqrt(na * na + nb * nb);
    }

    /// <summary>Mean luminance inside a disc of radius <paramref name="r"/> minus the mean of the
    /// surrounding annulus. Strongly negative over a real iris (dark centre, bright surround); near
    /// zero over skin or a brow. The safety gate for whether a claimed eye position is really an eye.</summary>
    private static float DiscContrast(byte[] bgra, int w, int h, float cx, float cy, float r)
    {
        float discSum = 0, annSum = 0; int discN = 0, annN = 0;
        var r2 = r * r; var a1 = 1.35f * r; var a2 = 2.0f * r;
        float a1s = a1 * a1, a2s = a2 * a2;
        var bound = (int)MathF.Ceiling(a2);
        for (var y = (int)cy - bound; y <= (int)cy + bound; y++)
        for (var x = (int)cx - bound; x <= (int)cx + bound; x++)
        {
            if (x < 0 || y < 0 || x >= w || y >= h) continue;
            float ddx = x - cx, ddy = y - cy; var dd = ddx * ddx + ddy * ddy;
            var i = (y * w + x) * 4;
            var l = 0.114f * bgra[i] + 0.587f * bgra[i + 1] + 0.299f * bgra[i + 2];
            if (dd <= r2) { discSum += l; discN++; }
            else if (dd >= a1s && dd <= a2s) { annSum += l; annN++; }
        }
        if (discN < 8 || annN < 8) return 0f;   // not enough pixels to judge → treat as "not an eye"
        return discSum / discN - annSum / annN;
    }

    private static float Smooth(float t) => t <= 0 ? 0 : t >= 1 ? 1 : t * t * (3 - 2 * t);

    /// <summary>Laplacian-variance sharpness of the eye region (higher = crisper structure).</summary>
    private static double Sharpness(byte[] bgra, int w, int h, (float X, float Y) eye,
        float ux, float uy, float vx, float vy, float rx, float ry)
    {
        double sum = 0, sum2 = 0; var n = 0;
        var bound = (int)MathF.Ceiling(MathF.Max(rx, ry));
        var cx = (int)eye.X; var cy = (int)eye.Y;
        for (var py = cy - bound + 1; py < cy + bound - 1; py++)
        for (var px = cx - bound + 1; px < cx + bound - 1; px++)
        {
            if (px < 1 || py < 1 || px >= w - 1 || py >= h - 1) continue;
            var (a, b) = Local(px, py, eye, ux, uy, vx, vy);
            if (Ellipse(a, b, rx, ry) >= 0.9f) continue;
            double L(int xx, int yy) { var i = (yy * w + xx) * 4; return 0.114 * bgra[i] + 0.587 * bgra[i + 1] + 0.299 * bgra[i + 2]; }
            var lap = 4 * L(px, py) - L(px - 1, py) - L(px + 1, py) - L(px, py - 1) - L(px, py + 1);
            sum += lap; sum2 += lap * lap; n++;
        }
        if (n == 0) return 0;
        var mean = sum / n;
        return sum2 / n - mean * mean;
    }

    /// <summary>Iris centre as a local-frame offset from the eye landmark: the centroid of the
    /// darkest third of pixels in the inner eye region (skips brow/shadow rims).</summary>
    private static (float A, float B) IrisOffset(byte[] bgra, int w, int h, (float X, float Y) eye,
        float ux, float uy, float vx, float vy, float rx, float ry)
    {
        var lums = new List<(float A, float B, float L)>();
        var bound = (int)MathF.Ceiling(MathF.Max(rx, ry));
        var cx = (int)eye.X; var cy = (int)eye.Y;
        for (var py = cy - bound; py <= cy + bound; py++)
        for (var px = cx - bound; px <= cx + bound; px++)
        {
            if (px < 0 || py < 0 || px >= w || py >= h) continue;
            var (a, b) = Local(px, py, eye, ux, uy, vx, vy);
            if (Ellipse(a, b, rx * 0.8f, ry * 0.85f) >= 1f) continue;
            var i = (py * w + px) * 4;
            var l = 0.114f * bgra[i] + 0.587f * bgra[i + 1] + 0.299f * bgra[i + 2];
            lums.Add((a, b, l));
        }
        if (lums.Count < 16) return (0, 0);
        lums.Sort((p, q) => p.L.CompareTo(q.L));
        var take = Math.Max(8, lums.Count / 3);
        float sa = 0, sb = 0;
        for (var i = 0; i < take; i++) { sa += lums[i].A; sb += lums[i].B; }
        return (sa / take, sb / take);
    }

    /// <summary>Bilinear BGRA sample, edge-clamped.</summary>
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
            float p00 = bgra[(yi * w + xi) * 4 + off], p10 = bgra[(yi * w + xi2) * 4 + off];
            float p01 = bgra[(yi2 * w + xi) * 4 + off], p11 = bgra[(yi2 * w + xi2) * 4 + off];
            var top = p00 + (p10 - p00) * fx;
            var bot = p01 + (p11 - p01) * fx;
            return top + (bot - top) * fy;
        }
        b = Ch(0); g = Ch(1); r = Ch(2);
    }
}
