using PhotoLibrary.Core.Imaging;
using SkiaSharp;
using Xunit;

namespace PhotoLibrary.Tests;

/// <summary>Minimal PSDs built in memory: the composite is decoded, and a blank one falls back to the thumbnail.</summary>
public class PsdTests
{
    private static void U16(List<byte> b, int v) { b.Add((byte)(v >> 8)); b.Add((byte)v); }
    private static void U32(List<byte> b, long v) { b.Add((byte)(v >> 24)); b.Add((byte)(v >> 16)); b.Add((byte)(v >> 8)); b.Add((byte)v); }

    /// <summary>A PSD with the given planes (one byte per pixel each), optionally RLE-compressed, plus optional resources.</summary>
    private static MemoryStream Psd(int w, int h, int mode, byte[][] planes, bool rle, byte[]? resources = null)
    {
        var b = new List<byte>();
        U32(b, 0x38425053); U16(b, 1); b.AddRange(new byte[6]);
        U16(b, planes.Length); U32(b, h); U32(b, w); U16(b, 8); U16(b, mode);
        U32(b, 0);                                   // colour mode data
        U32(b, resources?.Length ?? 0); if (resources is not null) b.AddRange(resources);
        U32(b, 0);                                   // layers
        U16(b, rle ? 1 : 0);
        if (!rle) { foreach (var p in planes) b.AddRange(p); }
        else
        {
            // Each row as one run when uniform, else as literals.
            var rows = new List<byte[]>();
            foreach (var p in planes)
                for (var y = 0; y < h; y++)
                {
                    var row = p.AsSpan(y * w, w).ToArray();
                    rows.Add(row.All(v => v == row[0]) && w > 1
                        ? [(byte)(sbyte)(1 - w), row[0]]
                        : [(byte)(w - 1), .. row]);
                }
            foreach (var r in rows) U16(b, r.Length);
            foreach (var r in rows) b.AddRange(r);
        }
        return new MemoryStream(b.ToArray());
    }

    [Fact]
    public void RawRgbCompositeIsDecoded()
    {
        // 2x1: red, blue.
        using var ms = Psd(2, 1, 3, [[255, 0], [0, 0], [0, 255]], rle: false);
        using var bmp = PsdDecoder.Decode(ms, 100);
        Assert.NotNull(bmp);
        Assert.Equal((2, 1), (bmp!.Width, bmp.Height));
        Assert.Equal(new SKColor(255, 0, 0), bmp.GetPixel(0, 0));
        Assert.Equal(new SKColor(0, 0, 255), bmp.GetPixel(1, 0));
    }

    [Fact]
    public void RleGreyscaleIsDecodedAndDownsampled()
    {
        // 8x8 greyscale: top half black, bottom half white, PackBits rows.
        var plane = Enumerable.Range(0, 64).Select(i => (byte)(i < 32 ? 0 : 255)).ToArray();
        using var ms = Psd(8, 8, 1, [plane], rle: true);
        using var bmp = PsdDecoder.Decode(ms, 2);
        Assert.NotNull(bmp);
        Assert.Equal(2, Math.Max(bmp!.Width, bmp.Height));
        Assert.True(bmp.GetPixel(0, 0).Red < 40);
        Assert.True(bmp.GetPixel(0, bmp.Height - 1).Red > 215);
    }

    [Fact]
    public void BlankCompositeFallsBackToTheEmbeddedThumbnail()
    {
        // A white composite, as written without "Maximize compatibility", plus a green JPEG thumbnail resource.
        using var thumb = new SKBitmap(4, 4);
        thumb.Erase(new SKColor(0, 200, 0));
        var jpeg = SKImage.FromBitmap(thumb).Encode(SKEncodedImageFormat.Jpeg, 95).ToArray();
        var res = new List<byte>();
        U32(res, 0x3842494D); U16(res, 1036); res.Add(0); res.Add(0); // empty, padded name
        U32(res, 28 + jpeg.Length);
        U32(res, 1); res.AddRange(new byte[24]); res.AddRange(jpeg);
        if (res.Count % 2 == 1) res.Add(0);

        var white = Enumerable.Repeat((byte)255, 16).ToArray();
        using var ms = Psd(4, 4, 3, [white, white, white], rle: false, resources: res.ToArray());
        using var bmp = PsdDecoder.Decode(ms, 100);
        Assert.NotNull(bmp);
        var c = bmp!.GetPixel(1, 1);
        Assert.True(c.Green > 150 && c.Red < 60, $"expected the green thumbnail, got {c}");
    }

    [Fact]
    public void PackBitsRoundTrip()
    {
        // 3 literals, then 'z' repeated 4 times.
        byte[] packed = [2, (byte)'a', (byte)'b', (byte)'c', unchecked((byte)-3), (byte)'z'];
        var dst = new byte[7];
        PsdDecoder.UnpackBits(packed, packed.Length, dst);
        Assert.Equal("abczzzz", System.Text.Encoding.ASCII.GetString(dst));
    }
}
