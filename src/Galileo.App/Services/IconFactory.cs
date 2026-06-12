using System;
using System.IO;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;
using Windows.UI;

namespace Galileo.Services;

/// <summary>Known media-folder kinds that get a themed icon instead of the plain folder.</summary>
public enum FolderKind { Normal, Pictures, Music, Videos }

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

    // Per-kind two-tone folder colors (back tab/body, lighter front face). Media folders get their
    // own hue so Pictures/Music/Videos read differently from an ordinary blue folder at a glance.
    private static (Color back, Color front) FolderColors(FolderKind kind) => kind switch
    {
        FolderKind.Pictures => (Color.FromArgb(255, 0x2E, 0x8F, 0x84), Color.FromArgb(255, 0x3F, 0xB6, 0xA8)), // teal
        FolderKind.Music => (Color.FromArgb(255, 0x8A, 0x5C, 0xD6), Color.FromArgb(255, 0xB5, 0x8B, 0xFF)),    // purple
        FolderKind.Videos => (Color.FromArgb(255, 0xCE, 0x6E, 0x33), Color.FromArgb(255, 0xF0, 0x93, 0x4E)),  // amber
        _ => (Blue, BlueLight),
    };

    /// <summary>Maps a folder path to a known media-folder kind (by known-folder path or name).</summary>
    public static FolderKind FolderKindFor(string path)
    {
        if (string.IsNullOrEmpty(path)) return FolderKind.Normal;
        if (Same(path, Environment.SpecialFolder.MyPictures)) return FolderKind.Pictures;
        if (Same(path, Environment.SpecialFolder.MyMusic)) return FolderKind.Music;
        if (Same(path, Environment.SpecialFolder.MyVideos)) return FolderKind.Videos;

        var name = Path.GetFileName(path.TrimEnd('\\', '/')).ToLowerInvariant();
        return name switch
        {
            "pictures" or "photos" or "camera roll" or "screenshots" or "saved pictures" => FolderKind.Pictures,
            "music" or "musik" => FolderKind.Music,
            "videos" or "movies" => FolderKind.Videos,
            _ => FolderKind.Normal,
        };

        static bool Same(string p, Environment.SpecialFolder sf)
        {
            var k = Environment.GetFolderPath(sf);
            return !string.IsNullOrEmpty(k) &&
                   string.Equals(p.TrimEnd('\\', '/'), k.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }
    }

    public static byte[] RenderFolder(int px, FolderKind kind = FolderKind.Normal) => Render(px, (ds, s) =>
    {
        var (back, front) = FolderColors(kind);
        // Back tab + body, with a lighter front face for a flat two-tone look.
        ds.FillRoundedRectangle(new Rect(0.14 * s, 0.26 * s, 0.34 * s, 0.16 * s), 0.045f * s, 0.045f * s, back);
        ds.FillRoundedRectangle(new Rect(0.10 * s, 0.32 * s, 0.80 * s, 0.46 * s), 0.06f * s, 0.06f * s, back);
        ds.FillRoundedRectangle(new Rect(0.10 * s, 0.44 * s, 0.80 * s, 0.34 * s), 0.06f * s, 0.06f * s, front);

        if (kind != FolderKind.Normal) DrawFolderGlyph(ds, s, kind);
    });

    // White media glyph centered on the folder's front face (face spans ~y 0.44–0.78).
    private static void DrawFolderGlyph(CanvasDrawingSession ds, float s, FolderKind kind)
    {
        var white = Microsoft.UI.Colors.White;
        float cx = 0.50f * s, cy = 0.61f * s;
        switch (kind)
        {
            case FolderKind.Pictures:
            {
                // Sun + mountain (a little photo scene).
                ds.FillCircle(cx - 0.085f * s, cy - 0.045f * s, 0.030f * s, white);
                using var pb = new CanvasPathBuilder(ds);
                pb.BeginFigure(cx - 0.13f * s, cy + 0.085f * s);
                pb.AddLine(cx - 0.02f * s, cy - 0.045f * s);
                pb.AddLine(cx + 0.045f * s, cy + 0.02f * s);
                pb.AddLine(cx + 0.085f * s, cy - 0.02f * s);
                pb.AddLine(cx + 0.14f * s, cy + 0.085f * s);
                pb.EndFigure(CanvasFigureLoop.Closed);
                using var mtn = CanvasGeometry.CreatePath(pb);
                ds.FillGeometry(mtn, white);
                break;
            }
            case FolderKind.Music:
            {
                // Beamed eighth notes: two heads + stems + a top beam.
                ds.FillEllipse(cx - 0.075f * s, cy + 0.075f * s, 0.052f * s, 0.038f * s, white);
                ds.FillEllipse(cx + 0.085f * s, cy + 0.045f * s, 0.052f * s, 0.038f * s, white);
                ds.FillRectangle(new Rect(cx - 0.030f * s, cy - 0.11f * s, 0.020f * s, 0.19f * s), white);
                ds.FillRectangle(new Rect(cx + 0.130f * s, cy - 0.14f * s, 0.020f * s, 0.19f * s), white);
                using var beam = new CanvasPathBuilder(ds);
                beam.BeginFigure(cx - 0.030f * s, cy - 0.11f * s);
                beam.AddLine(cx + 0.150f * s, cy - 0.14f * s);
                beam.AddLine(cx + 0.150f * s, cy - 0.085f * s);
                beam.AddLine(cx - 0.030f * s, cy - 0.055f * s);
                beam.EndFigure(CanvasFigureLoop.Closed);
                using var beamGeo = CanvasGeometry.CreatePath(beam);
                ds.FillGeometry(beamGeo, white);
                break;
            }
            case FolderKind.Videos:
            {
                // Play triangle.
                using var pb = new CanvasPathBuilder(ds);
                pb.BeginFigure(cx - 0.065f * s, cy - 0.105f * s);
                pb.AddLine(cx + 0.105f * s, cy);
                pb.AddLine(cx - 0.065f * s, cy + 0.105f * s);
                pb.EndFigure(CanvasFigureLoop.Closed);
                using var tri = CanvasGeometry.CreatePath(pb);
                ds.FillGeometry(tri, white);
                break;
            }
        }
    }

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

    // Icons render on background threads (DecodeThrottle). Win2D drawing sessions on one device are NOT
    // concurrency-safe — two at once raise "the last drawing session must be disposed…" and can hard-crash
    // the render thread (0xc000027b). Use a dedicated device (isolated from the editor / live preview / the
    // shared device) and serialize every draw with a lock.
    private static readonly object _renderLock = new();
    private static CanvasDevice? _device;

    private static byte[] Render(int px, Action<CanvasDrawingSession, float> draw)
    {
        lock (_renderLock)
        {
            _device ??= new CanvasDevice();
            using var rt = new CanvasRenderTarget(_device, px, px, 96);
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
}
