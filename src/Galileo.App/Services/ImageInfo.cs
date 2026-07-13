using System;
using System.Buffers.Binary;
using System.IO;

namespace Galileo.Services;

/// <summary>
/// Reads an image's pixel dimensions straight from its file header.
///
/// This exists to keep WinRT off the gallery's hot path. The obvious way to get dimensions is
/// StorageFile.GetFileFromPathAsync + Properties.GetImagePropertiesAsync, but that allocates two
/// WinRT objects per photo, and WinRT objects are reclaimed by the finalizer, which must marshal
/// each one back to the STA to release it. A gallery of a few hundred photos therefore pits the
/// finalizer, the GC and the UI thread against each other and the window locks up for seconds.
///
/// Dimensions live in the first few bytes of every common format, so a plain FileStream read gets
/// them with no COM, no finalizer queue and no allocation to speak of.
/// </summary>
public static class ImageInfo
{
    /// <summary>Pixel dimensions of the image, or null if the format isn't recognised or the file is unreadable.</summary>
    public static (int Width, int Height)? GetDimensions(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                bufferSize: 4096, FileOptions.SequentialScan);
            Span<byte> head = stackalloc byte[32];
            var n = fs.Read(head);
            if (n < 16) return null;

            // PNG: 8-byte signature, then IHDR (length+type = 8 bytes) then width/height, big-endian.
            if (head[0] == 0x89 && head[1] == 'P' && head[2] == 'N' && head[3] == 'G')
                return (BinaryPrimitives.ReadInt32BigEndian(head[16..]),
                        BinaryPrimitives.ReadInt32BigEndian(head[20..]));

            // GIF: "GIF87a"/"GIF89a", then logical screen width/height, little-endian.
            if (head[0] == 'G' && head[1] == 'I' && head[2] == 'F')
                return (BinaryPrimitives.ReadUInt16LittleEndian(head[6..]),
                        BinaryPrimitives.ReadUInt16LittleEndian(head[8..]));

            // BMP: "BM", then a BITMAPINFOHEADER whose height may be negative (top-down).
            if (head[0] == 'B' && head[1] == 'M')
                return (BinaryPrimitives.ReadInt32LittleEndian(head[18..]),
                        Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(head[22..])));

            // WEBP: "RIFF"...."WEBP" then a VP8 / VP8L / VP8X chunk, each storing size differently.
            if (head[0] == 'R' && head[1] == 'I' && head[2] == 'F' && head[3] == 'F' &&
                head[8] == 'W' && head[9] == 'E' && head[10] == 'B' && head[11] == 'P')
                return WebP(fs, head);

            // JPEG: no fixed offset — walk the segment chain to the start-of-frame marker.
            if (head[0] == 0xFF && head[1] == 0xD8)
                return Jpeg(fs);

            // TIFF (also the container for many camera RAWs): little- or big-endian IFD.
            if ((head[0] == 'I' && head[1] == 'I' && head[2] == 42 && head[3] == 0) ||
                (head[0] == 'M' && head[1] == 'M' && head[2] == 0 && head[3] == 42))
                return Tiff(fs, bigEndian: head[0] == 'M');

            return null; // HEIC/AVIF/RAW variants etc. — caller falls back
        }
        catch
        {
            return null;
        }
    }

    private static (int, int)? WebP(FileStream fs, Span<byte> head)
    {
        // Lossy "VP8 ": 14-byte frame header, dimensions are 14 bits each at offset 26.
        if (head[12] == 'V' && head[13] == 'P' && head[14] == '8' && head[15] == ' ')
        {
            Span<byte> b = stackalloc byte[10];
            fs.Position = 20;
            if (fs.Read(b) < 10) return null;
            if (b[3] != 0x9D || b[4] != 0x01 || b[5] != 0x2A) return null; // start code
            return (BinaryPrimitives.ReadUInt16LittleEndian(b[6..]) & 0x3FFF,
                    BinaryPrimitives.ReadUInt16LittleEndian(b[8..]) & 0x3FFF);
        }

        // Lossless "VP8L": 1 signature byte, then 14-bit width-1 and height-1 packed across 4 bytes.
        if (head[12] == 'V' && head[13] == 'P' && head[14] == '8' && head[15] == 'L')
        {
            Span<byte> b = stackalloc byte[5];
            fs.Position = 20;
            if (fs.Read(b) < 5) return null;
            if (b[0] != 0x2F) return null;
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(b[1..]);
            return ((int)(bits & 0x3FFF) + 1, (int)((bits >> 14) & 0x3FFF) + 1);
        }

        // Extended "VP8X": canvas size as two 24-bit little-endian (value-1) fields.
        if (head[12] == 'V' && head[13] == 'P' && head[14] == '8' && head[15] == 'X')
        {
            Span<byte> b = stackalloc byte[6];
            fs.Position = 24;
            if (fs.Read(b) < 6) return null;
            var w = b[0] | (b[1] << 8) | (b[2] << 16);
            var h = b[3] | (b[4] << 8) | (b[5] << 16);
            return (w + 1, h + 1);
        }

        return null;
    }

    private static (int, int)? Jpeg(FileStream fs)
    {
        fs.Position = 2;
        Span<byte> b = stackalloc byte[9];

        while (true)
        {
            // Markers are 0xFF followed by a type byte; fill bytes (0xFF) may pad between segments.
            int marker;
            do
            {
                var x = fs.ReadByte();
                if (x < 0) return null;
                if (x != 0xFF) continue;
                do { marker = fs.ReadByte(); } while (marker == 0xFF);
                break;
            } while (true);

            if (marker < 0) return null;

            // Start-of-frame markers carry the dimensions. C4/C8/CC are DHT/JPG/DAC, not frames.
            var isSof = marker is >= 0xC0 and <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;

            if (fs.Read(b[..2]) < 2) return null;
            var len = BinaryPrimitives.ReadUInt16BigEndian(b);
            if (len < 2) return null;

            if (isSof)
            {
                // Segment body: 1 byte precision, then height then width, both big-endian.
                if (fs.Read(b[..5]) < 5) return null;
                return (BinaryPrimitives.ReadUInt16BigEndian(b[3..]),
                        BinaryPrimitives.ReadUInt16BigEndian(b[1..]));
            }

            if (marker == 0xDA) return null; // hit compressed data without a frame header
            fs.Position += len - 2;
        }
    }

    private static (int, int)? Tiff(FileStream fs, bool bigEndian)
    {
        Span<byte> b = stackalloc byte[4];
        fs.Position = 4;
        if (fs.Read(b) < 4) return null;

        var ifd = Read32(b, bigEndian);
        if (ifd <= 0 || ifd >= fs.Length) return null;
        fs.Position = ifd;
        if (fs.Read(b[..2]) < 2) return null;

        var count = Read16(b, bigEndian);
        int w = 0, h = 0;
        Span<byte> e = stackalloc byte[12];

        for (var i = 0; i < count && (w == 0 || h == 0); i++)
        {
            if (fs.Read(e) < 12) return null;
            var tag = Read16(e, bigEndian);
            if (tag != 0x0100 && tag != 0x0101) continue; // ImageWidth / ImageLength

            var type = Read16(e[2..], bigEndian);
            // Value is inlined in the last 4 bytes: SHORT (type 3) or LONG (type 4).
            var v = type == 3 ? Read16(e[8..], bigEndian) : Read32(e[8..], bigEndian);
            if (tag == 0x0100) w = v; else h = v;
        }

        return w > 0 && h > 0 ? (w, h) : null;
    }

    private static int Read16(ReadOnlySpan<byte> b, bool bigEndian) =>
        bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(b) : BinaryPrimitives.ReadUInt16LittleEndian(b);

    private static int Read32(ReadOnlySpan<byte> b, bool bigEndian) =>
        bigEndian ? BinaryPrimitives.ReadInt32BigEndian(b) : BinaryPrimitives.ReadInt32LittleEndian(b);
}
