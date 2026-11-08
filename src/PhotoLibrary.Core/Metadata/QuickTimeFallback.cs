using System.Buffers.Binary;
using System.Text;

namespace PhotoLibrary.Core.Metadata;

/// <summary>
/// Minimal MOV/MP4 reader: mvhd creation time (UTC, 1904 epoch) and
/// Apple's com.apple.quicktime.creationdate (local wall-clock). Used when MetadataExtractor can't parse a file.
/// </summary>
public sealed class QuickTimeFallback
{
    private static readonly DateTime Epoch1904 = new(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public DateTime? MvhdCreatedUtc { get; private set; }
    public string? AppleCreationDate { get; private set; }
    public long? DurationMs { get; private set; }

    public static QuickTimeFallback Read(string path)
    {
        var q = new QuickTimeFallback();
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            foreach (var (type, start, end) in Boxes(fs, 0, fs.Length))
            {
                if (type != "moov") continue;
                foreach (var (t, s, e) in Boxes(fs, start, end))
                {
                    if (t == "mvhd") q.ReadMvhd(fs, s);
                    else if (t == "meta") q.ReadMeta(fs, s, e);
                }
                break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException or EndOfStreamException)
        {
        }
        return q;
    }

    private static IEnumerable<(string Type, long Start, long End)> Boxes(Stream f, long start, long end)
    {
        var hdr = new byte[16];
        var pos = start;
        while (pos + 8 <= end)
        {
            f.Seek(pos, SeekOrigin.Begin);
            var n = f.Read(hdr, 0, 16);
            if (n < 8) yield break;
            long size = BinaryPrimitives.ReadUInt32BigEndian(hdr);
            var type = Encoding.ASCII.GetString(hdr, 4, 4);
            var hl = 8;
            if (size == 1)
            {
                if (n < 16) yield break;
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(hdr.AsSpan(8));
                hl = 16;
            }
            else if (size == 0) size = end - pos;
            if (size < hl) yield break;
            yield return (type, pos + hl, Math.Min(pos + size, end));
            pos += size;
        }
    }

    private void ReadMvhd(Stream f, long s)
    {
        f.Seek(s, SeekOrigin.Begin);
        var d = new byte[32];
        if (f.Read(d, 0, 32) < 20) return;
        long secs;
        long duration, scale;
        if (d[0] == 1)
        {
            secs = (long)BinaryPrimitives.ReadUInt64BigEndian(d.AsSpan(4));
            scale = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(20));
            duration = (long)BinaryPrimitives.ReadUInt64BigEndian(d.AsSpan(24));
        }
        else
        {
            secs = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(4));
            scale = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(12));
            duration = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(16));
        }
        if (secs > 0)
        {
            var utc = Epoch1904.AddSeconds(secs);
            if (utc.Year >= 1990) MvhdCreatedUtc = utc;
        }
        if (scale > 0) DurationMs = duration * 1000 / scale;
    }

    private void ReadMeta(Stream f, long s, long e)
    {
        // QuickTime 'meta' is a plain box; MP4 'meta' is a full box (4 bytes of version/flags first).
        f.Seek(s, SeekOrigin.Begin);
        var probe = new byte[8];
        if (f.Read(probe, 0, 8) < 8) return;
        var first = Encoding.ASCII.GetString(probe, 4, 4);
        var start = first is "hdlr" or "keys" or "ilst" ? s : s + 4;
        var keys = new List<string>();
        var items = new Dictionary<int, string>();
        foreach (var (t, cs, ce) in Boxes(f, start, e))
        {
            if (t == "keys")
            {
                f.Seek(cs, SeekOrigin.Begin);
                var d = new byte[(int)Math.Min(ce - cs, 1 << 16)];
                var n = f.Read(d, 0, d.Length);
                if (n < 8) continue;
                var count = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(4));
                var p = 8;
                for (var i = 0; i < count && p + 8 <= n; i++)
                {
                    var ks = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p));
                    if (ks < 8 || p + ks > n) break;
                    keys.Add(Encoding.UTF8.GetString(d, p + 8, ks - 8));
                    p += ks;
                }
            }
            else if (t == "ilst")
            {
                foreach (var (it, ics, ice) in Boxes(f, cs, ce))
                {
                    var index = BinaryPrimitives.ReadInt32BigEndian(Encoding.Latin1.GetBytes(it));
                    foreach (var (dt, ds, de) in Boxes(f, ics, ice))
                    {
                        if (dt != "data") continue;
                        f.Seek(ds + 8, SeekOrigin.Begin);
                        var d = new byte[(int)Math.Min(de - ds - 8, 256)];
                        var n = f.Read(d, 0, d.Length);
                        items[index] = Encoding.UTF8.GetString(d, 0, Math.Max(0, n));
                    }
                }
            }
        }
        for (var i = 0; i < keys.Count; i++)
            if (keys[i] == "com.apple.quicktime.creationdate" && items.TryGetValue(i + 1, out var v))
                AppleCreationDate = v;
    }
}
