using System;
using System.Runtime.InteropServices;

namespace PhotosPlus.Services;

/// <summary>
/// Gets file/folder images the way Explorer does — via IShellItemImageFactory — and extracts the
/// pixels with GetDIBits. GetDIBits works for both DIB sections and device-dependent bitmaps
/// (some shortcut/file icons come back as DDBs), and forces a top-down 32-bit BGRA layout so the
/// result is always upright with correct transparency.
/// </summary>
public static class ShellImaging
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx; public int cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;   // negative = top-down
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColor0; // unused for 32bpp BI_RGB
    }

    [DllImport("shell32.dll")]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr hgdiobj, int cbBuffer, ref BITMAP lpvObject);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint cLines,
        byte[] lpvBits, ref BITMAPINFO lpbi, uint usage);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    private const int SIIGBF_BIGGERSIZEOK = 0x1;
    private const int SIIGBF_ICONONLY = 0x4;
    private const uint BI_RGB = 0;
    private const uint DIB_RGB_COLORS = 0;

    /// <summary>
    /// Returns premultiplied top-down BGRA pixels for the path, or (null,0,0) on failure.
    /// <paramref name="iconOnly"/> forces the plain icon (no content thumbnail / white matte).
    /// </summary>
    public static (byte[]? Pixels, int Width, int Height) GetPixels(string path, int size, bool iconOnly)
    {
        IShellItemImageFactory? factory = null;
        var hbitmap = IntPtr.Zero;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out factory) != 0 || factory is null)
                return (null, 0, 0);

            var flags = SIIGBF_BIGGERSIZEOK | (iconOnly ? SIIGBF_ICONONLY : 0);
            if (factory.GetImage(new SIZE { cx = size, cy = size }, flags, out hbitmap) != 0 || hbitmap == IntPtr.Zero)
                return (null, 0, 0);

            var bmp = new BITMAP();
            if (GetObject(hbitmap, Marshal.SizeOf<BITMAP>(), ref bmp) == 0) return (null, 0, 0);
            int w = bmp.bmWidth, h = Math.Abs(bmp.bmHeight);
            if (w <= 0 || h <= 0 || w > 1024 || h > 1024) return (null, 0, 0);

            var bytes = new byte[w * h * 4];
            var bi = new BITMAPINFO();
            bi.bmiHeader.biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>();
            bi.bmiHeader.biWidth = w;
            bi.bmiHeader.biHeight = -h; // top-down
            bi.bmiHeader.biPlanes = 1;
            bi.bmiHeader.biBitCount = 32;
            bi.bmiHeader.biCompression = BI_RGB;

            var hdc = GetDC(IntPtr.Zero);
            var lines = GetDIBits(hdc, hbitmap, 0, (uint)h, bytes, ref bi, DIB_RGB_COLORS);
            ReleaseDC(IntPtr.Zero, hdc);
            if (lines == 0) return (null, 0, 0);

            return (bytes, w, h);
        }
        catch
        {
            return (null, 0, 0);
        }
        finally
        {
            if (hbitmap != IntPtr.Zero) DeleteObject(hbitmap);
            if (factory is not null) Marshal.ReleaseComObject(factory);
        }
    }
}
