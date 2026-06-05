using System;
using System.Numerics;
using System.Threading.Tasks;
using Galileo.Models;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace Galileo.Services;

/// <summary>
/// Win2D image-editing pipeline: loads a source bitmap, builds a GPU effect graph from an
/// <see cref="EditState"/> (color adjustments + filter + orientation/straighten), and bakes the
/// result to a file at full resolution. Used for both the live preview and the final export.
/// </summary>
public sealed class ImageEditor : IDisposable
{
    // Lazily acquired so simply constructing the editor (in the MainWindow ctor) never initializes
    // Win2D — the device is only created the first time an image is actually edited.
    private CanvasDevice? _deviceCache;
    private CanvasDevice _device => _deviceCache ??= CanvasDevice.GetSharedDevice();

    public CanvasBitmap? Source { get; private set; }
    public CanvasDevice Device => _device;
    public uint PixelWidth => Source?.SizeInPixels.Width ?? 0;
    public uint PixelHeight => Source?.SizeInPixels.Height ?? 0;

    public async Task LoadAsync(string path)
    {
        Source?.Dispose();
        Source = null;
        var file = await StorageFile.GetFileFromPathAsync(path);
        try
        {
            using var stream = await file.OpenReadAsync();
            Source = await CanvasBitmap.LoadAsync(_device, stream);
        }
        catch
        {
            // Some HEIC/RAW formats won't load through Win2D directly — decode via WIC and convert.
            using var stream = await file.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            using var sb = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            Source = CanvasBitmap.CreateFromSoftwareBitmap(_device, sb);
        }
    }

    /// <summary>The full color + orientation effect graph. <paramref name="orientedBounds"/> is the
    /// post-transform image rectangle (origin 0,0); crop coordinates are relative to it.</summary>
    public ICanvasImage BuildOriented(EditState s, out Rect orientedBounds)
    {
        var colored = BuildColorPipeline(Source!, s);
        var m = OrientMatrix(s, out orientedBounds);
        return new Transform2DEffect
        {
            Source = colored,
            TransformMatrix = m,
            InterpolationMode = CanvasImageInterpolation.HighQualityCubic,
        };
    }

    /// <summary>Renders the edit (plus an optional overlay) to a file. <paramref name="bakeOverlay"/>
    /// is given the drawing session and the crop rect (in oriented-image space) to draw markup.</summary>
    public async Task ExportAsync(EditState s, string destPath, float quality, Action<CanvasDrawingSession, Rect>? bakeOverlay)
    {
        var oriented = BuildOriented(s, out var bounds);
        var crop = s.Crop ?? new Rect(0, 0, bounds.Width, bounds.Height);
        var w = Math.Max(1, (int)Math.Round(crop.Width));
        var h = Math.Max(1, (int)Math.Round(crop.Height));

        var fmt = FormatFor(destPath);
        using var rt = new CanvasRenderTarget(_device, w, h, 96);
        using (var ds = rt.CreateDrawingSession())
        {
            ds.Clear(fmt == CanvasBitmapFileFormat.Jpeg ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Transparent);
            ds.DrawImage(oriented, new Vector2((float)-crop.X, (float)-crop.Y));
            bakeOverlay?.Invoke(ds, crop);
        }
        await rt.SaveAsync(destPath, fmt, quality);
    }

    // ---- effect graph ----

    private static ICanvasImage BuildColorPipeline(ICanvasImage src, EditState s)
    {
        var img = src;
        if (s.Exposure != 0) img = new ExposureEffect { Source = img, Exposure = (float)s.Exposure };
        if (s.Temperature != 0 || s.Tint != 0) img = new TemperatureAndTintEffect { Source = img, Temperature = (float)s.Temperature, Tint = (float)s.Tint };
        if (s.Contrast != 0) img = new ContrastEffect { Source = img, Contrast = (float)s.Contrast };
        if (s.Brightness != 0) img = new LinearTransferEffect { Source = img, RedOffset = (float)s.Brightness, GreenOffset = (float)s.Brightness, BlueOffset = (float)s.Brightness };
        if (s.Saturation != 0) img = new ColorMatrixEffect { Source = img, ColorMatrix = SaturationMatrix(1f + (float)s.Saturation) };
        img = ApplyFilter(img, s.Filter);
        if (s.Sharpness > 0) img = new SharpenEffect { Source = img, Amount = (float)(s.Sharpness * 10), Threshold = 0 };
        return img;
    }

    private static ICanvasImage ApplyFilter(ICanvasImage img, ImageFilter filter) => filter switch
    {
        ImageFilter.Auto => new ColorMatrixEffect { Source = new ContrastEffect { Source = img, Contrast = 0.12f }, ColorMatrix = SaturationMatrix(1.12f) },
        ImageFilter.BlackWhite => new GrayscaleEffect { Source = img },
        ImageFilter.Sepia => new SepiaEffect { Source = img, Intensity = 1f },
        ImageFilter.Vivid => new ContrastEffect { Source = new ColorMatrixEffect { Source = img, ColorMatrix = SaturationMatrix(1.45f) }, Contrast = 0.15f },
        ImageFilter.Warm => new TemperatureAndTintEffect { Source = img, Temperature = 0.25f, Tint = 0.05f },
        ImageFilter.Cool => new TemperatureAndTintEffect { Source = img, Temperature = -0.25f, Tint = -0.03f },
        ImageFilter.Invert => new InvertEffect { Source = img },
        _ => img,
    };

    // Luminance-preserving saturation matrix (sat: 0 = grayscale, 1 = original, >1 = boosted).
    private static Matrix5x4 SaturationMatrix(float sat)
    {
        const float lr = 0.2125f, lg = 0.7154f, lb = 0.0721f;
        float ir = (1 - sat) * lr, ig = (1 - sat) * lg, ib = (1 - sat) * lb;
        return new Matrix5x4
        {
            M11 = ir + sat, M12 = ir, M13 = ir, M14 = 0,
            M21 = ig, M22 = ig + sat, M23 = ig, M24 = 0,
            M31 = ib, M32 = ib, M33 = ib + sat, M34 = 0,
            M41 = 0, M42 = 0, M43 = 0, M44 = 1,
            M51 = 0, M52 = 0, M53 = 0, M54 = 0,
        };
    }

    // ---- geometry ----

    private Matrix3x2 OrientMatrix(EditState s, out Rect bounds)
    {
        float w = Source!.SizeInPixels.Width, h = Source.SizeInPixels.Height;
        var center = new Vector2(w / 2f, h / 2f);
        float sx = s.FlipH ? -1f : 1f, sy = s.FlipV ? -1f : 1f;
        var ang = (float)((s.Quarter * 90 + s.StraightenDeg) * Math.PI / 180.0);

        var m = Matrix3x2.CreateScale(sx, sy, center) * Matrix3x2.CreateRotation(ang, center);
        bounds = TransformBounds(w, h, m);
        m *= Matrix3x2.CreateTranslation((float)-bounds.X, (float)-bounds.Y);
        bounds = new Rect(0, 0, bounds.Width, bounds.Height);
        return m;
    }

    private static Rect TransformBounds(float w, float h, Matrix3x2 m)
    {
        Span<Vector2> pts = stackalloc Vector2[4]
        {
            Vector2.Transform(new Vector2(0, 0), m),
            Vector2.Transform(new Vector2(w, 0), m),
            Vector2.Transform(new Vector2(0, h), m),
            Vector2.Transform(new Vector2(w, h), m),
        };
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var p in pts)
        {
            minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y);
            maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y);
        }
        return new Rect(minX, minY, maxX - minX, maxY - minY);
    }

    private static CanvasBitmapFileFormat FormatFor(string path)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".png" => CanvasBitmapFileFormat.Png,
            ".bmp" => CanvasBitmapFileFormat.Bmp,
            ".gif" => CanvasBitmapFileFormat.Gif,
            ".tif" or ".tiff" => CanvasBitmapFileFormat.Tiff,
            _ => CanvasBitmapFileFormat.Jpeg,
        };
    }

    public void Dispose()
    {
        Source?.Dispose();
        Source = null;
    }
}
