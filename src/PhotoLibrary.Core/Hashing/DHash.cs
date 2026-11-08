using System.Numerics;
using PhotoLibrary.Core.Imaging;
using SkiaSharp;

namespace PhotoLibrary.Core.Hashing;

/// <summary>
/// 64-bit difference hash: the image is reduced to 9x8 grey pixels and each bit records whether a pixel is
/// brighter than its right-hand neighbour. Survives re-encoding, resizing and mild edits; two photos within a
/// Hamming distance of ~4 are almost certainly the same picture. Dark/plain images all look alike to it, so
/// callers also require the capture dates to agree.
/// </summary>
public static class DHash
{
    public static ulong? Compute(string path)
    {
        using var bmp = ImageDecoder.Decode(path, 256, MediaKind.Image);
        return bmp is null ? null : Compute(bmp);
    }

    public static ulong Compute(SKBitmap bmp)
    {
        using var small = bmp.Resize(new SKImageInfo(9, 8, SKColorType.Bgra8888, SKAlphaType.Premul),
            new SKSamplingOptions(SKCubicResampler.Mitchell));
        if (small is null) return 0;
        var grey = new float[8, 9];
        for (var y = 0; y < 8; y++)
        for (var x = 0; x < 9; x++)
        {
            var c = small.GetPixel(x, y);
            grey[y, x] = 0.299f * c.Red + 0.587f * c.Green + 0.114f * c.Blue;
        }
        ulong bits = 0;
        for (var y = 0; y < 8; y++)
        for (var x = 0; x < 8; x++)
            bits = (bits << 1) | (grey[y, x] > grey[y, x + 1] ? 1UL : 0UL);
        return bits;
    }

    public static int Distance(ulong a, ulong b) => BitOperations.PopCount(a ^ b);
}
