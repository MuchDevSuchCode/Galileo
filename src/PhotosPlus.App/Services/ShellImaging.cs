using System;
using System.Runtime.InteropServices;

namespace PhotosPlus.Services;

/// <summary>
/// Gets file/folder images the way Explorer does — via IShellItemImageFactory, which returns a
/// 32-bit premultiplied BGRA bitmap with correct transparency. (GetThumbnailAsync sometimes
/// returns a JPEG with no alpha, which renders transparent areas as black.)
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
        public int biHeight; // sign indicates orientation: negative = top-down, positive = bottom-up
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
    private struct DIBSECTION
    {
        public BITMAP dsBm;
        public BITMAPINFOHEADER dsBmih;
        public uint dsBitfields0;
        public uint dsBitfields1;
        public uint dsBitfields2;
        public IntPtr dshSection;
        public uint dsOffset;
    }

    [DllImport("shell32.dll")]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr hgdiobj, int cbBuffer, ref BITMAP lpvObject);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr hgdiobj, int cbBuffer, ref DIBSECTION lpvObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    private const int SIIGBF_BIGGERSIZEOK = 0x1;
    private const int SIIGBF_ICONONLY = 0x4;

    /// <summary>
    /// Returns premultiplied top-down BGRA pixels for the path, or (null,0,0) on failure.
    /// <paramref name="iconOnly"/> forces the plain icon (no content thumbnail / white matte) —
    /// used for folders and drives.
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

            var dib = new DIBSECTION();
            if (GetObject(hbitmap, Marshal.SizeOf<DIBSECTION>(), ref dib) == 0 ||
                dib.dsBm.bmBits == IntPtr.Zero || dib.dsBm.bmBitsPixel != 32)
                return (null, 0, 0);

            var w = dib.dsBm.bmWidth;
            var h = Math.Abs(dib.dsBm.bmHeight);
            var stride = w * 4;
            var bytes = new byte[h * stride];
            Marshal.Copy(dib.dsBm.bmBits, bytes, 0, bytes.Length);

            // biHeight > 0 means a bottom-up DIB → flip rows so it's top-down for WriteableBitmap.
            if (dib.dsBmih.biHeight > 0)
            {
                var flipped = new byte[bytes.Length];
                for (var y = 0; y < h; y++)
                    Array.Copy(bytes, (h - 1 - y) * stride, flipped, y * stride, stride);
                bytes = flipped;
            }
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
