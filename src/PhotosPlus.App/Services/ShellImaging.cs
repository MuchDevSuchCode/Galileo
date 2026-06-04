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

    [DllImport("shell32.dll")]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr hgdiobj, int cbBuffer, ref BITMAP lpvObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    private const int SIIGBF_BIGGERSIZEOK = 0x1;

    /// <summary>Returns premultiplied top-down BGRA pixels for the path, or (null,0,0) on failure.</summary>
    public static (byte[]? Pixels, int Width, int Height) GetPixels(string path, int size)
    {
        IShellItemImageFactory? factory = null;
        var hbitmap = IntPtr.Zero;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out factory) != 0 || factory is null)
                return (null, 0, 0);

            if (factory.GetImage(new SIZE { cx = size, cy = size }, SIIGBF_BIGGERSIZEOK, out hbitmap) != 0 || hbitmap == IntPtr.Zero)
                return (null, 0, 0);

            var bmp = new BITMAP();
            if (GetObject(hbitmap, Marshal.SizeOf<BITMAP>(), ref bmp) == 0 || bmp.bmBits == IntPtr.Zero || bmp.bmBitsPixel != 32)
                return (null, 0, 0);

            var w = bmp.bmWidth;
            var h = Math.Abs(bmp.bmHeight);
            var bytes = new byte[w * h * 4];
            Marshal.Copy(bmp.bmBits, bytes, 0, bytes.Length);
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
