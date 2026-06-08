using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Galileo.Services;

/// <summary>Captures on-screen pixels (including video/WebView content) via GDI — to a PNG file
/// (window screenshot) or an in-memory PNG stream (e.g. the current video frame for the clipboard).</summary>
public static class ScreenCapture
{
    /// <summary>Grabs the window's displayed region and writes it to <paramref name="destPath"/> as PNG.
    /// Returns the actual saved path (a unique name is chosen if the file already exists).</summary>
    public static async Task<string> CaptureWindowAsync(IntPtr hwnd, string destPath)
    {
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var r, Marshal.SizeOf<RECT>()) != 0)
            GetWindowRect(hwnd, out r);
        var (pixels, w, h) = Grab(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);

        var folder = await StorageFolder.GetFolderFromPathAsync(System.IO.Path.GetDirectoryName(destPath)!);
        var file = await folder.CreateFileAsync(System.IO.Path.GetFileName(destPath), CreationCollisionOption.GenerateUniqueName);
        using (var stream = await file.OpenAsync(FileAccessMode.ReadWrite))
            await EncodePngAsync(pixels, w, h, stream);
        return file.Path;
    }

    /// <summary>Captures a client-relative rectangle (in DIPs, scaled by <paramref name="scale"/>) of the
    /// window and returns it as an in-memory PNG stream — used to copy the current video frame.</summary>
    public static async Task<InMemoryRandomAccessStream> CaptureClientRectToPngStreamAsync(
        IntPtr hwnd, double dipX, double dipY, double dipW, double dipH, double scale)
    {
        var origin = new POINT { X = 0, Y = 0 };
        ClientToScreen(hwnd, ref origin);
        int left = origin.X + (int)Math.Round(dipX * scale);
        int top = origin.Y + (int)Math.Round(dipY * scale);
        int w = (int)Math.Round(dipW * scale), h = (int)Math.Round(dipH * scale);

        var (pixels, gw, gh) = Grab(left, top, w, h);
        var stream = new InMemoryRandomAccessStream();
        await EncodePngAsync(pixels, gw, gh, stream);
        stream.Seek(0);
        return stream;
    }

    // ---- core grab + encode ----

    private static (byte[] pixels, int w, int h) Grab(int left, int top, int w, int h)
    {
        if (w <= 0 || h <= 0) throw new InvalidOperationException("Nothing to capture.");
        var hdcScreen = GetDC(IntPtr.Zero);
        var hdcMem = CreateCompatibleDC(hdcScreen);
        var hBmp = CreateCompatibleBitmap(hdcScreen, w, h);
        var old = SelectObject(hdcMem, hBmp);
        try
        {
            BitBlt(hdcMem, 0, 0, w, h, hdcScreen, left, top, SRCCOPY | CAPTUREBLT);
            var bmi = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32, biCompression = 0,
            };
            var pixels = new byte[w * h * 4];
            GetDIBits(hdcMem, hBmp, 0, (uint)h, pixels, ref bmi, 0);
            return (pixels, w, h);
        }
        finally
        {
            SelectObject(hdcMem, old);
            DeleteObject(hBmp);
            DeleteDC(hdcMem);
            ReleaseDC(IntPtr.Zero, hdcScreen);
        }
    }

    private static async Task EncodePngAsync(byte[] pixels, int w, int h, IRandomAccessStream stream)
    {
        var enc = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        enc.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)w, (uint)h, 96, 96, pixels);
        await enc.FlushAsync();
    }

    // ---- P/Invoke ----
    private const uint SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref POINT pt);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER bmi, uint usage);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
}
