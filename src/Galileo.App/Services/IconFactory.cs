using System;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;
using Windows.UI;

namespace Galileo.Services;

/// <summary>
/// Draws Galileo's own folder / drive / generic-file icons with Win2D — original, flat, accent-tinted
/// shapes that read like the familiar Explorer ones without being them (no Segoe Fluent / shell icons).
/// Returns premultiplied top-down BGRA pixels (px×px) ready for a WriteableBitmap.
/// </summary>
public static class IconFactory
{
    // Galileo accent (the blue→purple used across the app).
    private static readonly Color Blue = Color.FromArgb(255, 0x5A, 0x8A, 0xE0);
    private static readonly Color BlueLight = Color.FromArgb(255, 0x6E, 0xA8, 0xFF);
    private static readonly Color Purple = Color.FromArgb(255, 0xB5, 0x8B, 0xFF);
    private static readonly Color Slate = Color.FromArgb(255, 0x55, 0x5E, 0x72);
    private static readonly Color SlateDark = Color.FromArgb(255, 0x3B, 0x42, 0x52);
    private static readonly Color Page = Color.FromArgb(255, 0xFB, 0xFC, 0xFE);
    private static readonly Color PageEdge = Color.FromArgb(255, 0xBD, 0xC6, 0xD6);

    public static byte[] RenderFolder(int px) => Render(px, (ds, s) =>
    {
        // Back tab + body, with a lighter front face for a flat two-tone look.
        ds.FillRoundedRectangle(new Rect(0.14 * s, 0.26 * s, 0.34 * s, 0.16 * s), 0.045f * s, 0.045f * s, Blue);
        ds.FillRoundedRectangle(new Rect(0.10 * s, 0.32 * s, 0.80 * s, 0.46 * s), 0.06f * s, 0.06f * s, Blue);
        ds.FillRoundedRectangle(new Rect(0.10 * s, 0.44 * s, 0.80 * s, 0.34 * s), 0.06f * s, 0.06f * s, BlueLight);
    });

    public static byte[] RenderDrive(int px) => Render(px, (ds, s) =>
    {
        ds.FillRoundedRectangle(new Rect(0.12 * s, 0.30 * s, 0.76 * s, 0.40 * s), 0.07f * s, 0.07f * s, Slate);
        // Accent face strip + a small "activity" dot.
        ds.FillRoundedRectangle(new Rect(0.12 * s, 0.54 * s, 0.76 * s, 0.16 * s), 0.05f * s, 0.05f * s, SlateDark);
        ds.FillRoundedRectangle(new Rect(0.18 * s, 0.585 * s, 0.40 * s, 0.05 * s), 0.025f * s, 0.025f * s, BlueLight);
        ds.FillCircle(0.74f * s, 0.61f * s, 0.028f * s, Purple);
    });

    public static byte[] RenderFile(int px, string ext) => Render(px, (ds, s) =>
    {
        float L = 0.22f * s, R = 0.78f * s, T = 0.14f * s, B = 0.86f * s, fold = 0.18f * s;

        using var pb = new CanvasPathBuilder(ds);
        pb.BeginFigure(L, T);
        pb.AddLine(R - fold, T);
        pb.AddLine(R, T + fold);
        pb.AddLine(R, B);
        pb.AddLine(L, B);
        pb.EndFigure(CanvasFigureLoop.Closed);
        using var page = CanvasGeometry.CreatePath(pb);
        ds.FillGeometry(page, Page);
        ds.DrawGeometry(page, PageEdge, Math.Max(1f, 0.014f * s));

        // Folded corner.
        using var cb = new CanvasPathBuilder(ds);
        cb.BeginFigure(R - fold, T);
        cb.AddLine(R - fold, T + fold);
        cb.AddLine(R, T + fold);
        cb.EndFigure(CanvasFigureLoop.Closed);
        using var corner = CanvasGeometry.CreatePath(cb);
        ds.FillGeometry(corner, PageEdge);

        // Accent extension badge.
        var label = (ext ?? "").TrimStart('.').ToUpperInvariant();
        if (label.Length > 4) label = label[..4];
        if (label.Length > 0)
        {
            var badge = new Rect(L, 0.58 * s, R - L, 0.17 * s);
            ds.FillRoundedRectangle(badge, 0.035f * s, 0.035f * s, Blue);
            using var fmt = new CanvasTextFormat
            {
                FontSize = label.Length >= 4 ? 0.12f * s : 0.14f * s,
                HorizontalAlignment = CanvasHorizontalAlignment.Center,
                VerticalAlignment = CanvasVerticalAlignment.Center,
                WordWrapping = CanvasWordWrapping.NoWrap,
                FontWeight = new Windows.UI.Text.FontWeight { Weight = 700 },
            };
            ds.DrawText(label, badge, Microsoft.UI.Colors.White, fmt);
        }
    });

    private static byte[] Render(int px, Action<CanvasDrawingSession, float> draw)
    {
        var device = CanvasDevice.GetSharedDevice();
        using var rt = new CanvasRenderTarget(device, px, px, 96);
        using (var ds = rt.CreateDrawingSession())
        {
            ds.Clear(Microsoft.UI.Colors.Transparent);
            ds.Antialiasing = CanvasAntialiasing.Antialiased;
            ds.TextAntialiasing = CanvasTextAntialiasing.Grayscale;
            draw(ds, px);
        }
        return rt.GetPixelBytes();
    }
}
