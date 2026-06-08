using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace Galileo.Services;

/// <summary>Captures a window's on-screen pixels (including video/WebView content) to a PNG via GDI.</summary>
public static class ScreenCapture
{
    /// <summary>Grabs the window's displayed region and writes it to <paramref name="destPath"/> as PNG.
    /// Returns the actual saved path (a unique name is chosen if the file already exists).</summary>
    public static async Task<string> CaptureWindowAsync(IntPtr hwnd, string destPath)
    {
        // Prefer the DWM "extended frame bounds" (the visible window, minus the invisible resize border).
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var r, Marshal.SizeOf<RECT>()) != 0)
            GetWindowRect(hwnd, out r);

        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        if (w <= 0 || h <= 0) throw new InvalidOperationException("Window has no visible area to capture.");

        var hdcScreen = GetDC(IntPtr.Zero);
        var hdcMem = CreateCompatibleDC(hdcScreen);
        var hBmp = CreateCompatibleBitmap(hdcScreen, w, h);
        var old = SelectObject(hdcMem, hBmp);
        byte[] pixels;
        try
        {
            // Copy from the screen so whatever is actually composited (video, WebView, GPU surfaces) is captured.
            BitBlt(hdcMem, 0, 0, w, h, hdcScreen, r.Left, r.Top, SRCCOPY | CAPTUREBLT);

            var bmi = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h,        // negative = top-down rows
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0,    // BI_RGB
            };
            pixels = new byte[w * h * 4];
            GetDIBits(hdcMem, hBmp, 0, (uint)h, pixels, ref bmi, 0);
        }
        finally
        {
            SelectObject(hdcMem, old);
            DeleteObject(hBmp);
            DeleteDC(hdcMem);
            ReleaseDC(IntPtr.Zero, hdcScreen);
        }

        var folder = await StorageFolder.GetFolderFromPathAsync(System.IO.Path.GetDirectoryName(destPath)!);
        var file = await folder.CreateFileAsync(System.IO.Path.GetFileName(destPath), CreationCollisionOption.GenerateUniqueName);
        using (var stream = await file.OpenAsync(FileAccessMode.ReadWrite))
        {
            var enc = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            enc.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)w, (uint)h, 96, 96, pixels);
            await enc.FlushAsync();
        }
        return file.Path;
    }

    // ---- P/Invoke ----
    private const uint SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

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
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER bmi, uint usage);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
}
