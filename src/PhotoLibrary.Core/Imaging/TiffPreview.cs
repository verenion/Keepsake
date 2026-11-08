using System.Buffers.Binary;

namespace PhotoLibrary.Core.Imaging;

/// <summary>Uncompressed CFA (Bayer) sensor data inside a DNG, enough for <see cref="CfaPreview"/> to render it.</summary>
public sealed record RawFrame(int Width, int Height, int BitsPerSample, bool LittleEndian, long[] StripOffsets, int RowsPerStrip,
    int CfaWidth, int CfaHeight, byte[] CfaPattern, double BlackLevel, double WhiteLevel, double[] AsShotNeutral);

/// <summary>What a TIFF-based RAW file offers: an embedded JPEG preview, the real sensor size and the orientation.</summary>
public sealed record TiffInfo(byte[]? Preview, int Orientation, int Width, int Height, RawFrame? Raw);

/// <summary>
/// Reads the structure of TIFF-based RAW files (CR2, DNG, NEF, ...): the embedded JPEG preview, the full
/// image size and the orientation. Canon CR2 keeps a full-size JPEG in IFD0; Pixel DNGs keep a ~1280px
/// JPEG-compressed preview in IFD0 and the sensor data in a SubIFD. The preview JPEG has no EXIF of its own,
/// so the orientation must come from here, and the image size must come from the sensor IFD or the record
/// would claim the preview's size.
/// </summary>
public static class TiffPreview
{
    private const int TagNewSubfileType = 0x00FE, TagWidth = 0x0100, TagHeight = 0x0101, TagBits = 0x0102, TagCompression = 0x0103,
        TagPhotometric = 0x0106, TagStripOffsets = 0x0111, TagOrientation = 0x0112, TagSamples = 0x0115, TagRowsPerStrip = 0x0116,
        TagStripByteCounts = 0x0117, TagTileOffsets = 0x0144, TagTileByteCounts = 0x0145, TagSubIfds = 0x014A,
        TagJpegIfOffset = 0x0201, TagJpegIfByteCount = 0x0202, TagCfaRepeat = 0x828D, TagCfaPattern = 0x828E,
        TagBlackLevel = 0xC61A, TagWhiteLevel = 0xC61D, TagAsShotNeutral = 0xC628;

    private const int PhotometricCfa = 32803, PhotometricLinearRaw = 34892;

    private sealed record Candidate(long Offset, long Length, int Width, int Height);

    private sealed class Walk
    {
        public List<Candidate> Previews { get; } = [];
        public HashSet<long> Seen { get; } = [];
        public int Orientation { get; set; } = 1;
        public int Width { get; set; }
        public int Height { get; set; }
        public bool SizeFromSensor { get; set; }
        public RawFrame? Raw { get; set; }
    }

    public static byte[]? Extract(string path) => Inspect(path)?.Preview;

    public static byte[]? Extract(Stream s) => Inspect(s)?.Preview;

    /// <param name="readPreview">False to read only the structure (size, orientation), skipping the preview bytes.</param>
    public static TiffInfo? Inspect(string path, bool readPreview = true)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            return Inspect(fs, readPreview);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static TiffInfo? Inspect(Stream s, bool readPreview = true)
    {
        Span<byte> hdr = stackalloc byte[8];
        if (s.Read(hdr) < 8) return null;
        bool little = hdr[0] == 'I' && hdr[1] == 'I';
        if (!little && !(hdr[0] == 'M' && hdr[1] == 'M')) return null;
        var reader = new Reader(s, little);
        var walk = new Walk();
        long chain = reader.U32(4);
        var first = true;
        while (chain != 0 && chain < s.Length && walk.Seen.Add(chain) && walk.Previews.Count < 32)
        {
            chain = WalkIfd(reader, chain, walk, 0, first);
            first = false;
        }

        // The biggest preview wins: Canon CR2 files carry a full-size JPEG next to a 160x120 thumbnail, and JPEG
        // decodes at reduced size for almost nothing, so there is no reason to settle for the thumbnail.
        var best = walk.Previews
            .Where(c => c.Length > 1024 && c.Length <= 48L * 1024 * 1024 && c.Width > 0)
            .OrderByDescending(c => c.Width)
            .ThenByDescending(c => c.Length)
            .FirstOrDefault();
        byte[]? preview = null;
        if (best is not null && readPreview)
        {
            var buf = new byte[best.Length];
            s.Seek(best.Offset, SeekOrigin.Begin);
            var read = 0;
            while (read < buf.Length)
            {
                var n = s.Read(buf, read, buf.Length - read);
                if (n <= 0) break;
                read += n;
            }
            if (read >= 4 && buf[0] == 0xFF && buf[1] == 0xD8) preview = read == buf.Length ? buf : buf[..read];
        }
        return new TiffInfo(preview, walk.Orientation, walk.Width, walk.Height, walk.Raw);
    }

    private static long WalkIfd(Reader r, long off, Walk walk, int depth, bool isIfd0)
    {
        if (depth > 4) return 0;
        int n;
        try { n = r.U16(off); } catch (IOException) { return 0; }
        if (n is 0 or > 500) return 0;
        var tags = new Dictionary<int, Entry>();
        for (var i = 0; i < n; i++)
        {
            var p = off + 2 + i * 12;
            var tag = r.U16(p);
            tags[tag] = new Entry(r.U16(p + 2), r.U32(p + 4), p + 8);
        }

        long Value(int tag) => (long)r.Values(tags[tag], 1)[0];
        long ValueOr(int tag, long fallback) => tags.ContainsKey(tag) ? Value(tag) : fallback;

        var subfile = ValueOr(TagNewSubfileType, isIfd0 ? 0 : 1);
        var compression = ValueOr(TagCompression, 1);
        var photometric = ValueOr(TagPhotometric, 2);
        var width = (int)ValueOr(TagWidth, 0);
        var height = (int)ValueOr(TagHeight, 0);
        var isSensor = photometric is PhotometricCfa or PhotometricLinearRaw;

        if (isIfd0 && tags.ContainsKey(TagOrientation))
        {
            var o = (int)Value(TagOrientation);
            if (o is >= 1 and <= 8) walk.Orientation = o;
        }

        // The file's real size: the main image (NewSubfileType 0) wins; sensor data wins over a reduced preview that
        // happens to be flagged as the main image; otherwise the biggest IFD seen.
        if (width > 0 && height > 0)
        {
            var isMain = (subfile & 1) == 0;
            var better = walk.Width == 0
                || (isSensor && !walk.SizeFromSensor)
                || (isSensor == walk.SizeFromSensor && isMain && (long)width * height > (long)walk.Width * walk.Height);
            if (better)
            {
                walk.Width = width;
                walk.Height = height;
                walk.SizeFromSensor = isSensor;
            }
        }

        // Old-style JPEG thumbnail (IFD1 in EXIF, also used by some RAWs)
        if (tags.ContainsKey(TagJpegIfOffset) && tags.ContainsKey(TagJpegIfByteCount))
            walk.Previews.Add(new Candidate(Value(TagJpegIfOffset), Value(TagJpegIfByteCount), width > 0 ? width : 160, height));

        // JPEG-compressed strips/tiles that aren't the CFA raw data
        if (compression is 6 or 7 && !isSensor)
        {
            foreach (var (offTag, cntTag) in new[] { (TagStripOffsets, TagStripByteCounts), (TagTileOffsets, TagTileByteCounts) })
            {
                if (!tags.TryGetValue(offTag, out var o) || !tags.TryGetValue(cntTag, out var c)) continue;
                if (o.Count == 1 && c.Count == 1)
                    walk.Previews.Add(new Candidate(Value(offTag), Value(cntTag), width, height));
                // Multi-strip previews would need re-assembly; skip them, the other previews usually suffice.
            }
        }

        // Uncompressed Bayer data in strips: no JPEG preview needed, CfaPreview can render it directly.
        if (walk.Raw is null && compression == 1 && photometric == PhotometricCfa && width > 0 && height > 0
            && ValueOr(TagSamples, 1) == 1 && tags.TryGetValue(TagStripOffsets, out var strips) && tags.ContainsKey(TagCfaPattern))
        {
            var bits = (int)ValueOr(TagBits, 16);
            if (bits is 8 or 16)
            {
                var pattern = r.Bytes(tags[TagCfaPattern]);
                var (cw, ch) = tags.TryGetValue(TagCfaRepeat, out var rep) && rep.Count >= 2
                    ? ((int)r.Values(rep, 2)[0], (int)r.Values(rep, 2)[1]) : (2, 2);
                if (cw > 0 && ch > 0 && pattern.Length >= cw * ch)
                {
                    var black = tags.TryGetValue(TagBlackLevel, out var bl) ? r.Values(bl, 16).Average() : 0;
                    var white = tags.TryGetValue(TagWhiteLevel, out var wl) ? r.Values(wl, 1)[0] : (1 << bits) - 1;
                    var neutral = tags.TryGetValue(TagAsShotNeutral, out var ne) ? r.Values(ne, 4) : [];
                    var rows = (int)Math.Clamp(ValueOr(TagRowsPerStrip, height), 1, height);
                    var offsets = r.Values(strips, (int)Math.Min(strips.Count, 1 << 16)).Select(v => (long)v).ToArray();
                    walk.Raw = new RawFrame(width, height, bits, r.LittleEndian, offsets, rows, cw, ch, pattern[..(cw * ch)], black, white, neutral);
                }
            }
        }

        if (tags.TryGetValue(TagSubIfds, out var sub))
        {
            foreach (var so in r.Values(sub, 8))
                if (so != 0 && walk.Seen.Add((long)so)) WalkIfd(r, (long)so, walk, depth + 1, false);
        }

        return r.U32(off + 2 + n * 12L);
    }

    private sealed record Entry(int Type, uint Count, long ValueOff);

    private sealed class Reader(Stream s, bool little)
    {
        private readonly byte[] _buf = new byte[8];

        public bool LittleEndian => little;

        private static int TypeSize(int type) => type switch
        {
            1 or 2 or 6 or 7 => 1, 3 or 8 => 2, 4 or 9 or 11 => 4, 5 or 10 or 12 => 8, _ => 0,
        };

        private void Fill(long off, int n)
        {
            s.Seek(off, SeekOrigin.Begin);
            if (s.Read(_buf, 0, n) < n) throw new IOException("truncated TIFF");
        }

        public ushort U16(long off)
        {
            Fill(off, 2);
            return little ? BinaryPrimitives.ReadUInt16LittleEndian(_buf) : BinaryPrimitives.ReadUInt16BigEndian(_buf);
        }

        public uint U32(long off)
        {
            Fill(off, 4);
            return little ? BinaryPrimitives.ReadUInt32LittleEndian(_buf) : BinaryPrimitives.ReadUInt32BigEndian(_buf);
        }

        /// <summary>Where an entry's data lives: inline in the 4-byte value field when it fits, else at the offset stored there.</summary>
        private long DataOffset(Entry e) => TypeSize(e.Type) * (long)e.Count <= 4 ? e.ValueOff : U32(e.ValueOff);

        /// <summary>Reads up to <paramref name="max"/> numeric values of an entry (SHORT/LONG/RATIONAL/BYTE...).</summary>
        public double[] Values(Entry e, int max)
        {
            var size = TypeSize(e.Type);
            if (size == 0) return [];
            var count = (int)Math.Min(e.Count, (uint)max);
            var data = DataOffset(e);
            var result = new double[count];
            for (var i = 0; i < count; i++)
            {
                var p = data + (long)i * size;
                result[i] = e.Type switch
                {
                    1 or 7 => Byte(p),
                    6 => (sbyte)Byte(p),
                    3 => U16(p),
                    8 => (short)U16(p),
                    4 => U32(p),
                    9 => (int)U32(p),
                    5 => U32(p + 4) is var d && d != 0 ? (double)U32(p) / d : 0,
                    10 => (int)U32(p + 4) is var sd && sd != 0 ? (double)(int)U32(p) / sd : 0,
                    11 => F32(p),
                    12 => F64(p),
                    _ => 0,
                };
            }
            return result;
        }

        public byte[] Bytes(Entry e)
        {
            var count = (int)Math.Min(e.Count, 64);
            var p = DataOffset(e);
            var result = new byte[count];
            for (var i = 0; i < count; i++) result[i] = Byte(p + i);
            return result;
        }

        private byte Byte(long off)
        {
            Fill(off, 1);
            return _buf[0];
        }

        private float F32(long off)
        {
            Fill(off, 4);
            return little ? BinaryPrimitives.ReadSingleLittleEndian(_buf) : BinaryPrimitives.ReadSingleBigEndian(_buf);
        }

        private double F64(long off)
        {
            Fill(off, 8);
            return little ? BinaryPrimitives.ReadDoubleLittleEndian(_buf) : BinaryPrimitives.ReadDoubleBigEndian(_buf);
        }
    }
}
