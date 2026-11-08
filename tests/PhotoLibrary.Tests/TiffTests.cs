using PhotoLibrary.Core;
using PhotoLibrary.Core.Imaging;
using SkiaSharp;
using Xunit;

namespace PhotoLibrary.Tests;

/// <summary>TIFF goes through Windows' own decoder (Skia has none), from a file or from memory, turned upright.</summary>
public class TiffTests
{
    /// <summary>An uncompressed 8-bit RGB TIFF: left half red, right half blue.</summary>
    private static byte[] Tiff(int w, int h, int orientation)
    {
        var pixels = new byte[w * h * 3];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 3;
                if (x < w / 2) pixels[i] = 255; else pixels[i + 2] = 255;
            }
        var entries = new List<(ushort Tag, ushort Type, uint Count, uint Value)>
        {
            (256, 4, 1, (uint)w), (257, 4, 1, (uint)h), (258, 3, 3, 0 /* patched: offset of 8,8,8 */), (259, 3, 1, 1),
            (262, 3, 1, 2), (273, 4, 1, 0 /* patched: strip offset */), (274, 3, 1, (uint)orientation), (277, 3, 1, 3),
            (278, 4, 1, (uint)h), (279, 4, 1, (uint)pixels.Length),
        };
        var ifdOffset = 8;
        var ifdSize = 2 + entries.Count * 12 + 4;
        var bitsOffset = (uint)(ifdOffset + ifdSize);
        var dataOffset = bitsOffset + 6;
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write("II"u8); bw.Write((ushort)42); bw.Write((uint)ifdOffset);
        bw.Write((ushort)entries.Count);
        foreach (var (tag, type, count, value) in entries)
        {
            bw.Write(tag); bw.Write(type); bw.Write(count);
            var v = tag == 258 ? bitsOffset : tag == 273 ? dataOffset : value;
            if (type == 3 && count == 1) { bw.Write((ushort)v); bw.Write((ushort)0); } else bw.Write(v);
        }
        bw.Write(0u);
        bw.Write((ushort)8); bw.Write((ushort)8); bw.Write((ushort)8);
        bw.Write(pixels);
        return ms.ToArray();
    }

    [Fact]
    public void TiffDecodesFromFileAndMemoryAndIsTurnedUpright()
    {
        if (!OperatingSystem.IsWindows()) return; // Windows' decoder; other platforms fall back to ffmpeg
        var data = Tiff(40, 20, orientation: 6); // stored landscape, displayed rotated 90° clockwise
        var path = Path.Combine(Path.GetTempPath(), "photolib-tiff-" + Guid.NewGuid().ToString("N") + ".tif");
        File.WriteAllBytes(path, data);
        try
        {
            using var fromFile = ImageDecoder.Decode(path, 100, MediaKind.Image);
            using var fromMemory = ImageDecoder.DecodeInMemory(data, "scan.tif", MediaKind.Image, 100);
            foreach (var bmp in new[] { fromFile, fromMemory })
            {
                Assert.NotNull(bmp);
                Assert.Equal((20, 40), (bmp!.Width, bmp.Height));
                // After a clockwise turn the stored left (red) half is on top, the right (blue) half below.
                Assert.True(bmp.GetPixel(10, 5).Red > 200, $"top should be red, got {bmp.GetPixel(10, 5)}");
                Assert.True(bmp.GetPixel(10, 35).Blue > 200, $"bottom should be blue, got {bmp.GetPixel(10, 35)}");
            }
        }
        finally { File.Delete(path); }
    }
}
