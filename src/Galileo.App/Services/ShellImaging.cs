using System;
using System.Buffers;
using System.Runtime.InteropServices;

namespace Galileo.Services;

/// <summary>
/// A shell thumbnail's pixels, borrowed from a pool. <see cref="Pixels"/> may be LARGER than the image —
/// always bound reads by <see cref="ByteCount"/> (or Width/Height), never by <c>Pixels.Length</c>.
///
/// Why pooled: the shell returns the cached thumbnail, which is typically 256x256 = 256 KB. Anything over
/// 85 KB goes straight to the Large Object Heap, so a folder of photos fired hundreds of LOH allocations and
/// drove a gen2 collection storm — multi-second UI stalls with a heap only tens of MB in size. Renting the
/// buffer keeps this churn out of the GC entirely.
/// </summary>
public readonly struct ShellImage : IDisposable
{
    public readonly byte[]? Pixels;
    public readonly int Width;
    public readonly int Height;
    public readonly int ByteCount;

    internal ShellImage(byte[]? pixels, int width, int height)
    {
        Pixels = pixels;
        Width = width;
        Height = height;
        ByteCount = width * height * 4;
    }

    public bool IsValid => Pixels is not null && Width > 0 && Height > 0;

    public void Dispose()
    {
        if (Pixels is not null) ArrayPool<byte>.Shared.Return(Pixels);
    }
}

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
    /// <summary>Pool-backed variant for the thumbnail hot path — the caller MUST dispose the result.
    /// See <see cref="ShellImage"/> for why this matters.</summary>
    public static ShellImage GetImage(string path, int size, bool iconOnly)
    {
        var (pixels, w, h) = GetPixels(path, size, iconOnly, pooled: true);
        return new ShellImage(pixels, w, h);
    }

    public static (byte[]? Pixels, int Width, int Height) GetPixels(string path, int size, bool iconOnly)
        => GetPixels(path, size, iconOnly, pooled: false);

    private static (byte[]? Pixels, int Width, int Height) GetPixels(string path, int size, bool iconOnly, bool pooled)
    {
        IShellItemImageFactory? factory = null;
        var hbitmap = IntPtr.Zero;
        var hdc = IntPtr.Zero;
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

            var need = w * h * 4;
            // A rented buffer can be larger than `need`; GetDIBits only writes the rows it's told to, and
            // every consumer bounds itself by w/h, so the slack is harmless.
            var bytes = pooled ? ArrayPool<byte>.Shared.Rent(need) : new byte[need];
            var bi = new BITMAPINFO();
            bi.bmiHeader.biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>();
            bi.bmiHeader.biWidth = w;
            bi.bmiHeader.biHeight = -h; // top-down
            bi.bmiHeader.biPlanes = 1;
            bi.bmiHeader.biBitCount = 32;
            bi.bmiHeader.biCompression = BI_RGB;

            hdc = GetDC(IntPtr.Zero);
            var lines = GetDIBits(hdc, hbitmap, 0, (uint)h, bytes, ref bi, DIB_RGB_COLORS);
            if (lines == 0)
            {
                if (pooled) ArrayPool<byte>.Shared.Return(bytes);   // don't lose it back to the pool
                return (null, 0, 0);
            }

            // Device-dependent bitmaps (some shortcut/file icons) have no alpha channel, so
            // GetDIBits leaves every alpha byte at 0 — which would render the icon fully
            // transparent (invisible). If the whole image came back transparent, treat it as
            // opaque. Genuinely-transparent icons always have at least one opaque pixel, so
            // this never flattens a real alpha channel.
            // Bound by `need`, not bytes.Length — a rented buffer has slack past the image.
            var anyOpaque = false;
            for (var i = 3; i < need; i += 4)
                if (bytes[i] != 0) { anyOpaque = true; break; }
            if (!anyOpaque)
                for (var i = 3; i < need; i += 4) bytes[i] = 255;

            return (bytes, w, h);
        }
        catch
        {
            return (null, 0, 0);
        }
        finally
        {
            if (hdc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, hdc);
            if (hbitmap != IntPtr.Zero) DeleteObject(hbitmap);
            if (factory is not null) Marshal.ReleaseComObject(factory);
        }
    }
}
