using System.Buffers.Binary;
using PhotoLibrary.Core;
using PhotoLibrary.Core.Imaging;
using PhotoLibrary.Core.Metadata;
using SkiaSharp;

namespace PhotoLibrary.Tests;

/// <summary>
/// Builds tiny DNG-shaped TIFF files in memory: a Pixel-style one (JPEG preview in IFD0, sensor data in a
/// SubIFD, orientation tag) and a sensor-only one with no preview at all.
/// </summary>
public class RawPreviewTests
{
    [Fact]
    public void PixelStyleDngReportsSensorSizeAndOrientation()
    {
        var bytes = PixelStyleDng(previewW: 160, previewH: 120, sensorW: 4000, sensorH: 3000, orientation: 6);
        using var ms = new MemoryStream(bytes);
        var info = TiffPreview.Inspect(ms)!;
        Assert.NotNull(info.Preview);
        Assert.Equal(0xFF, info.Preview![0]);
        Assert.Equal(6, info.Orientation);
        Assert.Equal((4000, 3000), (info.Width, info.Height));
    }

    [Fact]
    public void RawPreviewIsRotatedByTheTiffOrientation()
    {
        // 160x120 preview rotated 90° clockwise comes out 120x160.
        var path = Path.Combine(Path.GetTempPath(), $"pl-{Guid.NewGuid():N}.dng");
        File.WriteAllBytes(path, PixelStyleDng(160, 120, 4000, 3000, orientation: 6));
        try
        {
            using var bmp = ImageDecoder.Decode(path, 320, MediaKind.Image);
            Assert.NotNull(bmp);
            Assert.Equal((120, 160), (bmp!.Width, bmp.Height));

            var meta = MetadataReader.Read(path);
            Assert.Equal((4000, 3000), (meta.Width, meta.Height));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SensorOnlyDngGetsAPreviewFromTheBayerData()
    {
        // 8x8 RGGB sensor, 16-bit: left half red, right half blue, with black level 1000 and white 5000.
        const int w = 8, h = 8;
        var samples = new ushort[w * h];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var isRed = (y % 2 == 0) && (x % 2 == 0);
            var isBlue = (y % 2 == 1) && (x % 2 == 1);
            var left = x < w / 2;
            samples[y * w + x] = (ushort)(isRed && left ? 5000 : isBlue && !left ? 5000 : 1000);
        }
        var path = Path.Combine(Path.GetTempPath(), $"pl-{Guid.NewGuid():N}.dng");
        File.WriteAllBytes(path, SensorOnlyDng(w, h, samples, orientation: 1));
        try
        {
            using var bmp = ImageDecoder.Decode(path, 320, MediaKind.Image);
            Assert.NotNull(bmp);
            Assert.Equal((4, 4), (bmp!.Width, bmp.Height));
            var l = bmp.GetPixel(0, 0);
            var r = bmp.GetPixel(3, 3);
            Assert.True(l.Red > 200 && l.Blue < 10 && l.Green < 10, $"left pixel {l}");
            Assert.True(r.Blue > 200 && r.Red < 10 && r.Green < 10, $"right pixel {r}");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void PixelMotionPhotoClipsAreVideos()
    {
        Assert.Equal(MediaKind.Video, MediaKinds.KindOf(@"C:\x\PXL_20251227_174124526.RAW-01.MP.COVER"));
        Assert.Equal(MediaKind.Image, MediaKinds.KindOf(@"C:\x\PXL_20251227_174124526.RAW-01.MP.COVER.jpg"));
        Assert.Equal(MediaKind.Video, MediaKinds.KindOf(@"C:\x\PXL_20240903_130720281.MP"));
    }

    // ---- builders (little-endian TIFF)

    private sealed class Ifd
    {
        public List<(int Tag, int Type, uint Count, byte[] Data)> Entries { get; } = [];

        public void Short(int tag, params ushort[] v) => Add(tag, 3, v.Length, v.SelectMany(BitConverter.GetBytes).ToArray());
        public void Long(int tag, params uint[] v) => Add(tag, 4, v.Length, v.SelectMany(BitConverter.GetBytes).ToArray());
        public void Bytes(int tag, params byte[] v) => Add(tag, 1, v.Length, v);
        public void Rational(int tag, params (uint N, uint D)[] v) => Add(tag, 5, v.Length, v.SelectMany(r => BitConverter.GetBytes(r.N).Concat(BitConverter.GetBytes(r.D))).ToArray());
        private void Add(int tag, int type, int count, byte[] data) => Entries.Add((tag, type, (uint)count, data));
    }

    /// <summary>Lays out IFDs and blobs: header, then each IFD with its out-of-line data, then the blobs.</summary>
    private static byte[] Build(List<(Ifd Ifd, List<Ifd> Subs)> chain, Dictionary<string, byte[]> blobs, Func<Dictionary<string, uint>, Dictionary<Ifd, uint>, bool> fix)
    {
        // Two passes: first to measure, second with resolved offsets.
        var blobOffsets = new Dictionary<string, uint>();
        var ifdOffsets = new Dictionary<Ifd, uint>();
        byte[] result = [];
        for (var pass = 0; pass < 2; pass++)
        {
            fix(blobOffsets, ifdOffsets);
            var ms = new MemoryStream();
            ms.Write("II"u8);
            ms.Write(BitConverter.GetBytes((ushort)42));
            ms.Write(BitConverter.GetBytes((uint)8));
            var all = new List<Ifd>();
            foreach (var (ifd, subs) in chain) { all.Add(ifd); all.AddRange(subs); }
            // Pre-compute IFD offsets for this pass so SubIFD/next pointers can be written.
            var pos = 8u;
            foreach (var ifd in all)
            {
                ifdOffsets[ifd] = pos;
                pos += 2 + 12 * (uint)ifd.Entries.Count + 4;
                foreach (var e in ifd.Entries) if (e.Data.Length > 4) pos += (uint)(e.Data.Length + e.Data.Length % 2);
            }
            foreach (var (name, data) in blobs) { blobOffsets[name] = pos; pos += (uint)(data.Length + data.Length % 2); }
            fix(blobOffsets, ifdOffsets);

            for (var ci = 0; ci < chain.Count; ci++)
            {
                var (ifd, subs) = chain[ci];
                var next = ci + 1 < chain.Count ? ifdOffsets[chain[ci + 1].Ifd] : 0;
                WriteIfd(ms, ifd, ifdOffsets[ifd], next);
                foreach (var sub in subs) WriteIfd(ms, sub, ifdOffsets[sub], 0);
            }
            foreach (var (_, data) in blobs) { ms.Write(data); if (data.Length % 2 == 1) ms.WriteByte(0); }
            result = ms.ToArray();
        }
        return result;
    }

    private static void WriteIfd(MemoryStream ms, Ifd ifd, uint at, uint next)
    {
        Assert.Equal(at, (uint)ms.Position);
        var sorted = ifd.Entries.OrderBy(e => e.Tag).ToList();
        ms.Write(BitConverter.GetBytes((ushort)sorted.Count));
        var dataPos = at + 2 + 12 * (uint)sorted.Count + 4;
        var outOfLine = new List<byte[]>();
        foreach (var (tag, type, count, data) in sorted)
        {
            ms.Write(BitConverter.GetBytes((ushort)tag));
            ms.Write(BitConverter.GetBytes((ushort)type));
            ms.Write(BitConverter.GetBytes(count));
            if (data.Length <= 4)
            {
                var v = new byte[4];
                data.CopyTo(v, 0);
                ms.Write(v);
            }
            else
            {
                ms.Write(BitConverter.GetBytes(dataPos));
                dataPos += (uint)(data.Length + data.Length % 2);
                outOfLine.Add(data);
            }
        }
        ms.Write(BitConverter.GetBytes(next));
        foreach (var d in outOfLine) { ms.Write(d); if (d.Length % 2 == 1) ms.WriteByte(0); }
    }

    private static byte[] Jpeg(int w, int h)
    {
        // Noise, so the JPEG is comfortably bigger than the 1 KB "not a stub" floor the extractor applies.
        using var bmp = new SKBitmap(w, h);
        var rng = new Random(7);
        for (var y = 0; y < h; y++) for (var x = 0; x < w; x++) bmp.SetPixel(x, y, new SKColor((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256)));
        using var data = bmp.Encode(SKEncodedImageFormat.Jpeg, 80);
        return data.ToArray();
    }

    private static byte[] PixelStyleDng(int previewW, int previewH, int sensorW, int sensorH, ushort orientation)
    {
        var jpeg = Jpeg(previewW, previewH);
        var ifd0 = new Ifd();
        var sensor = new Ifd();
        var blobs = new Dictionary<string, byte[]> { ["jpeg"] = jpeg, ["cfa"] = new byte[16] };
        return Build([(ifd0, [sensor])], blobs, (bo, io) =>
        {
            ifd0.Entries.Clear();
            ifd0.Long(0x00FE, 1);
            ifd0.Short(0x0100, (ushort)previewW);
            ifd0.Short(0x0101, (ushort)previewH);
            ifd0.Short(0x0103, 7);
            ifd0.Short(0x0106, 6);
            ifd0.Short(0x0112, orientation);
            ifd0.Long(0x0111, bo.GetValueOrDefault("jpeg"));
            ifd0.Long(0x0117, (uint)jpeg.Length);
            ifd0.Long(0x014A, io.GetValueOrDefault(sensor));
            sensor.Entries.Clear();
            sensor.Long(0x00FE, 0);
            sensor.Long(0x0100, (uint)sensorW);
            sensor.Long(0x0101, (uint)sensorH);
            sensor.Short(0x0102, 16);
            sensor.Short(0x0103, 7);
            sensor.Short(0x0106, 32803);
            sensor.Long(0x0144, bo.GetValueOrDefault("cfa"));
            sensor.Long(0x0145, 16);
            return true;
        });
    }

    private static byte[] SensorOnlyDng(int w, int h, ushort[] samples, ushort orientation)
    {
        var data = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(i * 2), samples[i]);
        var ifd0 = new Ifd();
        var blobs = new Dictionary<string, byte[]> { ["cfa"] = data };
        return Build([(ifd0, [])], blobs, (bo, _) =>
        {
            ifd0.Entries.Clear();
            ifd0.Long(0x00FE, 0);
            ifd0.Long(0x0100, (uint)w);
            ifd0.Long(0x0101, (uint)h);
            ifd0.Short(0x0102, 16);
            ifd0.Short(0x0103, 1);
            ifd0.Short(0x0106, 32803);
            ifd0.Short(0x0112, orientation);
            ifd0.Short(0x0115, 1);
            ifd0.Long(0x0116, (uint)h);
            ifd0.Long(0x0111, bo.GetValueOrDefault("cfa"));
            ifd0.Long(0x0117, (uint)data.Length);
            ifd0.Short(0x828D, 2, 2);
            ifd0.Bytes(0x828E, 0, 1, 1, 2);
            ifd0.Long(0xC61A, 1000);
            ifd0.Long(0xC61D, 5000);
            ifd0.Rational(0xC628, (1, 1), (1, 1), (1, 1));
            return true;
        });
    }
}
