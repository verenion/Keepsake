using System.Buffers.Binary;
using System.Text;

namespace PhotoLibrary.Core.Metadata;

/// <summary>
/// Minimal EXIF reader used when MetadataExtractor returns nothing useful (it chokes on some Pixel DNGs).
/// Reads IFD0 + the Exif sub-IFD from the first 4 MB.
/// </summary>
public sealed class ExifFallback
{
    public string? DateTimeOriginal { get; private set; }
    public string? DateTimeDigitized { get; private set; }
    public string? DateTime { get; private set; }
    public string? Make { get; private set; }
    public string? Model { get; private set; }
    public int? Orientation { get; private set; }
    public bool Found { get; private set; }

    private const int HeadBytes = 4 << 20;

    public static ExifFallback Read(string path)
    {
        var r = new ExifFallback();
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            var buf = new byte[(int)Math.Min(fs.Length, HeadBytes)];
            var n = fs.Read(buf, 0, buf.Length);
            if (n < 16) return r;
            if (buf[0] == 0xFF && buf[1] == 0xD8) r.ParseJpeg(buf, n);
            else if ((buf[0] == 'I' && buf[1] == 'I') || (buf[0] == 'M' && buf[1] == 'M')) r.ParseTiff(buf, n, 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return r;
    }

    private void ParseJpeg(byte[] b, int len)
    {
        var i = 2;
        while (i + 4 <= len && b[i] == 0xFF)
        {
            var marker = b[i + 1];
            if (marker == 0xFF) { i++; continue; }
            if (marker is 0xD8 or 0x01 || (marker >= 0xD0 && marker <= 0xD7)) { i += 2; continue; }
            if (marker == 0xDA) break;
            var segLen = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(i + 2));
            if (marker == 0xE1 && i + 10 <= len && b[i + 4] == 'E' && b[i + 5] == 'x' && b[i + 6] == 'i' && b[i + 7] == 'f')
            {
                ParseTiff(b, len, i + 10);
                return;
            }
            i += 2 + segLen;
        }
    }

    private void ParseTiff(byte[] b, int len, int baseOff)
    {
        if (baseOff + 8 > len) return;
        var little = b[baseOff] == 'I';
        try
        {
            uint U32(int o) => little ? BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(baseOff + o)) : BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(baseOff + o));
            ushort U16(int o) => little ? BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(baseOff + o)) : BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(baseOff + o));

            Dictionary<int, object> Ifd(int off)
            {
                var tags = new Dictionary<int, object>();
                if (baseOff + off + 2 > len) return tags;
                int count = U16(off);
                for (var i = 0; i < count; i++)
                {
                    var p = off + 2 + i * 12;
                    if (baseOff + p + 12 > len) break;
                    int tag = U16(p), type = U16(p + 2);
                    var cnt = U32(p + 4);
                    if (type == 2)
                    {
                        var vo = cnt <= 4 ? p + 8 : (int)U32(p + 8);
                        if (baseOff + vo + (int)cnt > len || cnt > 4096) continue;
                        var s = Encoding.ASCII.GetString(b, baseOff + vo, (int)cnt);
                        var nul = s.IndexOf('\0');
                        tags[tag] = (nul >= 0 ? s[..nul] : s).Trim();
                    }
                    else if (type is 4 or 13) tags[tag] = U32(p + 8);
                    else if (type == 3) tags[tag] = (uint)U16(p + 8);
                }
                return tags;
            }

            var ifd0 = Ifd((int)U32(4));
            if (ifd0.TryGetValue(0x8769, out var exifOff) && exifOff is uint eo)
                foreach (var kv in Ifd((int)eo)) ifd0[kv.Key] = kv.Value;

            Found = ifd0.Count > 0;
            DateTimeOriginal = ifd0.GetValueOrDefault(0x9003) as string;
            DateTimeDigitized = ifd0.GetValueOrDefault(0x9004) as string;
            DateTime = ifd0.GetValueOrDefault(0x0132) as string;
            Make = ifd0.GetValueOrDefault(0x010F) as string;
            Model = ifd0.GetValueOrDefault(0x0110) as string;
            if (ifd0.GetValueOrDefault(0x0112) is uint ori && ori is >= 1 and <= 8) Orientation = (int)ori;
        }
        catch (ArgumentOutOfRangeException)
        {
            // truncated structure: keep whatever was read
        }
    }
}
