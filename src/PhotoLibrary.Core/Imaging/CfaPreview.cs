using SkiaSharp;

namespace PhotoLibrary.Core.Imaging;

/// <summary>
/// Renders a quick preview straight from uncompressed Bayer sensor data, for DNG files that carry no JPEG
/// preview (astrophotography and other capture apps write these). Each 2x2 Bayer cell becomes one pixel:
/// black and white levels and the as-shot white balance are applied, then a display gamma. No demosaicing,
/// no colour matrix: it is a thumbnail, not a conversion.
/// </summary>
public static class CfaPreview
{
    public static SKBitmap? Decode(string path, RawFrame raw, int maxEdge)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            return Decode(fs, raw, maxEdge);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static SKBitmap? Decode(Stream s, RawFrame raw, int maxEdge)
    {
        if (raw.BitsPerSample is not (8 or 16) || raw.CfaWidth != 2 || raw.CfaHeight != 2) return null;
        if (raw.StripOffsets.Length == 0 || raw.Width < 2 || raw.Height < 2) return null;

        // Which of the four cell positions holds which colour (0=R, 1=G, 2=B).
        var pattern = raw.CfaPattern;
        if (pattern.Any(c => c > 2)) return null;

        var cellsX = raw.Width / 2;
        var cellsY = raw.Height / 2;
        var step = Math.Max(1, (int)Math.Ceiling(Math.Max(cellsX, cellsY) / (double)Math.Max(64, maxEdge)));
        var outW = (cellsX + step - 1) / step;
        var outH = (cellsY + step - 1) / step;
        var bytesPerSample = raw.BitsPerSample / 8;
        var rowBytes = (long)raw.Width * bytesPerSample;
        var row0 = new byte[rowBytes];
        var row1 = new byte[rowBytes];

        var range = Math.Max(1, raw.WhiteLevel - raw.BlackLevel);
        var gain = new double[3];
        for (var c = 0; c < 3; c++)
        {
            var n = raw.AsShotNeutral.Length > c ? raw.AsShotNeutral[c] : 1;
            gain[c] = n > 0 ? Math.Clamp(1 / n, 0.25, 8) : 1;
        }

        var pixels = new SKColor[outW * outH];
        var little = raw.LittleEndian; // sample bytes follow the TIFF header's byte order
        for (var oy = 0; oy < outH; oy++)
        {
            var y = oy * step * 2;
            if (!ReadRow(s, raw, y, rowBytes, row0) || !ReadRow(s, raw, y + 1, rowBytes, row1)) return null;
            for (var ox = 0; ox < outW; ox++)
            {
                var x = ox * step * 2;
                double r = 0, g = 0, b = 0;
                int gc = 0;
                for (var i = 0; i < 4; i++)
                {
                    var row = i < 2 ? row0 : row1;
                    var px = x + (i & 1);
                    var v = bytesPerSample == 1 ? row[px] : little ? row[px * 2] | row[px * 2 + 1] << 8 : row[px * 2] << 8 | row[px * 2 + 1];
                    var lin = Math.Clamp((v - raw.BlackLevel) / range, 0, 1);
                    switch (pattern[i])
                    {
                        case 0: r = lin; break;
                        case 1: g += lin; gc++; break;
                        default: b = lin; break;
                    }
                }
                if (gc > 1) g /= gc;
                pixels[oy * outW + ox] = new SKColor(ToByte(r * gain[0]), ToByte(g * gain[1]), ToByte(b * gain[2]));
            }
        }
        var bmp = new SKBitmap(new SKImageInfo(outW, outH, SKColorType.Bgra8888, SKAlphaType.Premul));
        bmp.Pixels = pixels;
        return ImageDecoder.FitWithin(bmp, maxEdge);
    }

    private static byte ToByte(double linear) => (byte)Math.Round(Math.Pow(Math.Clamp(linear, 0, 1), 1 / 2.2) * 255);

    private static bool ReadRow(Stream s, RawFrame raw, int y, long rowBytes, byte[] buf)
    {
        if (y >= raw.Height) return false;
        var strip = y / raw.RowsPerStrip;
        if (strip >= raw.StripOffsets.Length) return false;
        var off = raw.StripOffsets[strip] + (long)(y % raw.RowsPerStrip) * rowBytes;
        if (off + rowBytes > s.Length) return false;
        s.Seek(off, SeekOrigin.Begin);
        var read = 0;
        while (read < buf.Length)
        {
            var n = s.Read(buf, read, buf.Length - read);
            if (n <= 0) return false;
            read += n;
        }
        return true;
    }
}
