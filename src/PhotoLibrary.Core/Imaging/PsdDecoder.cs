using SkiaSharp;

namespace PhotoLibrary.Core.Imaging;

/// <summary>
/// Photoshop PSD/PSB. After the layers, every file carries a flattened composite of the whole image (raw or
/// PackBits-compressed, one plane per channel), which is what is decoded here, reading only the rows it needs.
/// Files saved without "Maximize compatibility", or in modes this doesn't handle (Lab, bitmap, 32-bit), fall
/// back to the small JPEG thumbnail Photoshop stores in the image resources.
/// </summary>
public static class PsdDecoder
{
    public sealed record Header(bool IsPsb, int Channels, int Height, int Width, int Depth, int ColorMode);

    private const int ModeBitmap = 0, ModeGray = 1, ModeIndexed = 2, ModeRgb = 3, ModeCmyk = 4, ModeMultichannel = 7, ModeDuotone = 8;
    private const int ResourceThumbnail = 1036, ResourceThumbnailOld = 1033;

    /// <summary>Pixel size from the header, without decoding anything.</summary>
    public static (int Width, int Height)? ReadSize(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return ReadHeader(new Reader(fs)) is { } h ? (h.Width, h.Height) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException) { return null; }
    }

    public static SKBitmap? Decode(string path, int maxEdge)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            return Decode(fs, maxEdge);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public static SKBitmap? Decode(Stream s, int maxEdge)
    {
        try
        {
            var r = new Reader(s);
            if (ReadHeader(r) is not { } h) return null;

            var colorData = r.Bytes((int)r.U32());               // palette for indexed mode
            var resourcesLength = r.U32();
            var resourcesStart = s.Position;
            s.Position = resourcesStart + resourcesLength;
            var layersLength = h.IsPsb ? (long)r.U64() : r.U32();
            var layersStart = s.Position;
            // A negative layer count means the first extra channel is the composite's transparency. Otherwise an
            // extra channel is a saved selection or spot colour, and the composite is opaque.
            var mergedAlpha = false;
            if (layersLength >= (h.IsPsb ? 10 : 6))
            {
                var layerInfoLength = h.IsPsb ? (long)r.U64() : r.U32();
                if (layerInfoLength >= 2) mergedAlpha = (short)r.U16() < 0;
            }
            s.Position = layersStart + layersLength;
            var imageStart = s.Position;

            SKBitmap? composite = null;
            try { composite = DecodeComposite(s, r, h, colorData, imageStart, maxEdge, mergedAlpha); }
            catch (Exception ex) when (ex is EndOfStreamException or IOException or ArgumentException or InvalidDataException) { }

            if (composite is not null && !IsBlank(composite)) return composite;
            var thumb = Thumbnail(s, r, resourcesStart, resourcesLength, maxEdge);
            if (thumb is not null) { composite?.Dispose(); return thumb; }
            return composite;
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException or ArgumentException) { return null; }
    }

    private static Header? ReadHeader(Reader r)
    {
        if (r.U32() != 0x38425053) return null; // "8BPS"
        var version = r.U16();
        if (version is not (1 or 2)) return null;
        r.Skip(6);
        var h = new Header(version == 2, r.U16(), (int)r.U32(), (int)r.U32(), r.U16(), r.U16());
        return h.Width > 0 && h.Height > 0 && h.Channels > 0 ? h : null;
    }

    private static SKBitmap? DecodeComposite(Stream s, Reader r, Header h, byte[] palette, long imageStart, int maxEdge, bool mergedAlpha)
    {
        var supported = h.ColorMode switch
        {
            ModeRgb => h.Channels >= 3,
            ModeCmyk => h.Channels >= 4,
            ModeGray or ModeDuotone or ModeMultichannel => true,
            ModeIndexed => palette.Length >= 768,
            _ => false,
        };
        if (!supported || h.Depth is not (8 or 16)) return null;

        s.Position = imageStart;
        var compression = r.U16();
        if (compression is not (0 or 1)) return null; // ZIP only appears inside layers in practice
        var bpc = h.Depth / 8;
        var rowBytes = h.Width * bpc;
        var planes = h.ColorMode switch { ModeRgb => 3, ModeCmyk => 4, _ => 1 };
        var hasAlpha = mergedAlpha && (h.ColorMode == ModeRgb && h.Channels >= 4 || h.ColorMode == ModeGray && h.Channels >= 2);
        var used = planes + (hasAlpha ? 1 : 0);

        // Where each channel's row starts. RLE: a table of compressed row lengths comes first.
        var rowOffset = new long[used * h.Height];
        var rowLength = new int[used * h.Height];
        if (compression == 1)
        {
            var total = h.Channels * h.Height;
            var lengths = new int[total];
            for (var i = 0; i < total; i++) lengths[i] = h.IsPsb ? (int)r.U32() : r.U16();
            var pos = s.Position;
            for (var i = 0; i < total; i++)
            {
                if (i < rowOffset.Length) { rowOffset[i] = pos; rowLength[i] = lengths[i]; }
                pos += lengths[i];
            }
        }
        else
        {
            var data = s.Position;
            for (var c = 0; c < used; c++)
                for (var y = 0; y < h.Height; y++)
                {
                    rowOffset[c * h.Height + y] = data + ((long)c * h.Height + y) * rowBytes;
                    rowLength[c * h.Height + y] = rowBytes;
                }
        }

        // Read every step-th row and column: thumbnails never need the full 50-megapixel plane.
        var step = Math.Max(1, Math.Max(h.Width, h.Height) / Math.Max(1, maxEdge * 2));
        var ow = (h.Width + step - 1) / step;
        var oh = (h.Height + step - 1) / step;
        var bmp = new SKBitmap(new SKImageInfo(ow, oh, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        var pixels = new byte[ow * oh * 4];
        var raw = new byte[rowBytes];
        var packed = Array.Empty<byte>();
        var plane = new byte[used][];
        for (var c = 0; c < used; c++) plane[c] = new byte[ow];

        for (var oy = 0; oy < oh; oy++)
        {
            var y = oy * step;
            for (var c = 0; c < used; c++)
            {
                var i = c * h.Height + y;
                s.Position = rowOffset[i];
                if (compression == 1)
                {
                    if (packed.Length < rowLength[i]) packed = new byte[rowLength[i]];
                    r.Fill(packed, rowLength[i]);
                    UnpackBits(packed, rowLength[i], raw);
                }
                else r.Fill(raw, rowBytes);
                var dst = plane[c];
                for (var ox = 0; ox < ow; ox++) dst[ox] = raw[ox * step * bpc]; // 16-bit is big-endian: high byte first
            }
            for (var ox = 0; ox < ow; ox++)
            {
                byte red, green, blue, alpha = 255;
                switch (h.ColorMode)
                {
                    case ModeRgb:
                        (red, green, blue) = (plane[0][ox], plane[1][ox], plane[2][ox]);
                        if (hasAlpha) alpha = plane[3][ox];
                        break;
                    case ModeCmyk:
                        // Stored inverted (255 = no ink); a plain multiply is close enough for a thumbnail.
                        var k = plane[3][ox] / 255f;
                        (red, green, blue) = ((byte)(plane[0][ox] * k), (byte)(plane[1][ox] * k), (byte)(plane[2][ox] * k));
                        break;
                    case ModeIndexed:
                        var p = plane[0][ox];
                        (red, green, blue) = (palette[p], palette[256 + p], palette[512 + p]);
                        break;
                    default:
                        red = green = blue = plane[0][ox];
                        if (hasAlpha) alpha = plane[1][ox];
                        break;
                }
                var o = (oy * ow + ox) * 4;
                pixels[o] = blue; pixels[o + 1] = green; pixels[o + 2] = red; pixels[o + 3] = alpha;
            }
        }
        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bmp.GetPixels(), pixels.Length);
        return ImageDecoder.FitWithin(bmp, maxEdge);
    }

    /// <summary>PackBits: a header byte n, then n+1 literal bytes (n ≥ 0) or one byte repeated 1−n times (n &lt; 0).</summary>
    public static void UnpackBits(byte[] src, int length, byte[] dst)
    {
        int i = 0, o = 0;
        while (i < length && o < dst.Length)
        {
            var n = (sbyte)src[i++];
            if (n >= 0)
            {
                var count = Math.Min(n + 1, Math.Min(dst.Length - o, length - i));
                Buffer.BlockCopy(src, i, dst, o, count);
                i += n + 1;
                o += count;
            }
            else if (n != -128)
            {
                var count = Math.Min(1 - n, dst.Length - o);
                if (i >= length) break;
                dst.AsSpan(o, count).Fill(src[i++]);
                o += count;
            }
        }
    }

    /// <summary>Without "Maximize compatibility" Photoshop writes a plain white composite.</summary>
    private static bool IsBlank(SKBitmap bmp)
    {
        var first = bmp.GetPixel(0, 0);
        var stepX = Math.Max(1, bmp.Width / 16);
        var stepY = Math.Max(1, bmp.Height / 16);
        for (var y = 0; y < bmp.Height; y += stepY)
            for (var x = 0; x < bmp.Width; x += stepX)
                if (bmp.GetPixel(x, y) != first) return false;
        return true;
    }

    /// <summary>Photoshop's own small JPEG preview, or null. Public for diagnostics.</summary>
    public static SKBitmap? EmbeddedThumbnail(Stream s, int maxEdge)
    {
        var r = new Reader(s);
        s.Position = 0;
        if (ReadHeader(r) is null) return null;
        var colorDataLength = r.U32(); // read first: "s.Position += r.U32()" would add to the position before the read
        s.Position += colorDataLength;
        var length = r.U32();
        return Thumbnail(s, r, s.Position, length, maxEdge);
    }

    private static SKBitmap? Thumbnail(Stream s, Reader r, long start, long length, int maxEdge)
    {
        s.Position = start;
        var end = start + length;
        while (s.Position + 12 <= end)
        {
            if (r.U32() != 0x3842494D) return null; // "8BIM"
            var id = r.U16();
            var nameLength = r.U8();
            r.Skip(nameLength + ((nameLength + 1) % 2 == 0 ? 0 : 1)); // Pascal string padded to even
            var size = (int)r.U32();
            var next = s.Position + size + (size % 2);
            if (id is ResourceThumbnail or ResourceThumbnailOld && size > 28)
            {
                var format = r.U32();
                r.Skip(24);
                if (format == 1) // kJpegRGB
                {
                    var jpeg = r.Bytes(size - 28);
                    var bmp = ImageDecoder.DecodeBytes(jpeg, maxEdge);
                    if (bmp is not null && id == ResourceThumbnailOld) SwapRedBlue(bmp); // Photoshop 4 stored BGR
                    return bmp;
                }
            }
            s.Position = next;
        }
        return null;
    }

    private static void SwapRedBlue(SKBitmap bmp)
    {
        for (var y = 0; y < bmp.Height; y++)
            for (var x = 0; x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                bmp.SetPixel(x, y, new SKColor(c.Blue, c.Green, c.Red, c.Alpha));
            }
    }

    private sealed class Reader(Stream s)
    {
        private readonly byte[] _buf = new byte[8];

        public void Fill(byte[] dst, int count)
        {
            var got = 0;
            while (got < count)
            {
                var n = s.Read(dst, got, count - got);
                if (n == 0) throw new EndOfStreamException();
                got += n;
            }
        }

        public byte[] Bytes(int count)
        {
            if (count < 0 || count > 64 << 20) throw new InvalidDataException("implausible section length");
            var b = new byte[count];
            Fill(b, count);
            return b;
        }

        public void Skip(long n) => s.Position += n;
        public byte U8() { Fill(_buf, 1); return _buf[0]; }
        public ushort U16() { Fill(_buf, 2); return (ushort)(_buf[0] << 8 | _buf[1]); }
        public uint U32() { Fill(_buf, 4); return (uint)(_buf[0] << 24 | _buf[1] << 16 | _buf[2] << 8 | _buf[3]); }
        public ulong U64() => (ulong)U32() << 32 | U32();
    }
}
