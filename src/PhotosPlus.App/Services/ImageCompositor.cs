using System;

namespace PhotosPlus.Services;

/// <summary>Paints a small framed "photo" onto a folder icon (Explorer-style content preview).</summary>
public static class ImageCompositor
{
    /// <summary>
    /// Overlays <paramref name="img"/> (premultiplied BGRA) onto <paramref name="folder"/>
    /// (premultiplied BGRA, modified in place) as a white-framed photo on the folder's face.
    /// </summary>
    public static void OverlayPhoto(byte[] folder, int fw, int fh, byte[] img, int iw, int ih)
    {
        if (fw <= 0 || fh <= 0 || iw <= 0 || ih <= 0) return;

        var maxW = fw * 0.50;
        var maxH = fh * 0.42;
        var scale = Math.Min(maxW / iw, maxH / ih);
        if (scale <= 0) return;

        var pw = Math.Max(1, (int)(iw * scale));
        var ph = Math.Max(1, (int)(ih * scale));
        var border = Math.Max(2, fw / 48);
        var boxW = pw + 2 * border;
        var boxH = ph + 2 * border;

        var x0 = fw / 2 - boxW / 2;
        var y0 = (int)(fh * 0.60) - boxH / 2;

        for (var dy = 0; dy < boxH; dy++)
        {
            var y = y0 + dy;
            if (y < 0 || y >= fh) continue;
            for (var dx = 0; dx < boxW; dx++)
            {
                var x = x0 + dx;
                if (x < 0 || x >= fw) continue;

                byte b, g, r, a;
                var inPhoto = dx >= border && dx < border + pw && dy >= border && dy < border + ph;
                if (inPhoto)
                {
                    var sx = Math.Min(iw - 1, (int)((dx - border) / scale));
                    var sy = Math.Min(ih - 1, (int)((dy - border) / scale));
                    var si = (sy * iw + sx) * 4;
                    b = img[si]; g = img[si + 1]; r = img[si + 2]; a = img[si + 3];
                }
                else
                {
                    b = 255; g = 255; r = 255; a = 255; // white frame
                }

                var di = (y * fw + x) * 4;
                if (a == 255)
                {
                    folder[di] = b; folder[di + 1] = g; folder[di + 2] = r; folder[di + 3] = 255;
                }
                else
                {
                    var inv = 1 - a / 255.0;
                    folder[di] = (byte)(b + folder[di] * inv);
                    folder[di + 1] = (byte)(g + folder[di + 1] * inv);
                    folder[di + 2] = (byte)(r + folder[di + 2] * inv);
                    folder[di + 3] = (byte)(a + folder[di + 3] * inv);
                }
            }
        }
    }
}
