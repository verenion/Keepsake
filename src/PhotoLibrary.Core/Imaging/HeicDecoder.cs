using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SkiaSharp;

namespace PhotoLibrary.Core.Imaging;

/// <summary>
/// HEIC/HEIF via the Windows Imaging Component, which decodes them when the "HEIF Image Extensions" and
/// "HEVC Video Extensions" Store packages are installed (they are on most Windows 10/11 machines). WIC applies
/// the file's rotation itself, so the result is upright. When WIC can't (other OS, codec missing), the caller
/// falls back to ffmpeg, and failing that to the small JPEG some writers embed in the EXIF block.
/// </summary>
public static class HeicDecoder
{
    private static readonly Lazy<bool> CanDecode = new(ProbeWic);

    /// <summary>Why the last native decode failed, for diagnostics.</summary>
    public static string? LastError { get; private set; }

    /// <summary>True when Windows has a HEIF decoder registered.</summary>
    public static bool Available => OperatingSystem.IsWindows() && CanDecode.Value;

    public static SKBitmap? Decode(string path, int maxEdge) => DecodeNative(path, maxEdge) ?? EmbeddedThumbnail(path, maxEdge);

    /// <summary>Windows' own decoder; null when it is missing or fails on this file.</summary>
    public static SKBitmap? DecodeNative(string path, int maxEdge)
    {
        if (!Available) return null;
        var bmp = WicImage.DecodeFile(path, maxEdge);
        if (bmp is null) LastError = WicImage.LastError;
        return bmp;
    }

    private static bool ProbeWic()
    {
        if (!OperatingSystem.IsWindows()) return false;
        return WicImage.HasDecoder(WicImage.ContainerHeif);
    }

    /// <summary>The same scan over a file already in memory.</summary>
    public static SKBitmap? EmbeddedThumbnail(byte[] data, int maxEdge)
    {
        var len = Math.Min(data.Length, 2 << 20);
        var start = IndexOf(data, len, [0xFF, 0xD8, 0xFF]);
        if (start < 0) return null;
        var end = IndexOf(data, len, [0xFF, 0xD9], start);
        return end < 0 ? null : ImageDecoder.DecodeBytes(data[start..(end + 2)], maxEdge);
    }

    /// <summary>Best effort: some HEIC writers embed a JPEG thumbnail inside the EXIF (IFD1). Scan the first 2 MB.</summary>
    public static SKBitmap? EmbeddedThumbnail(string path, int maxEdge)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var len = (int)Math.Min(fs.Length, 2 << 20);
            var buf = new byte[len];
            var read = fs.Read(buf, 0, len);
            var start = IndexOf(buf, read, [0xFF, 0xD8, 0xFF]);
            if (start < 0) return null;
            var end = IndexOf(buf, read, [0xFF, 0xD9], start);
            if (end < 0) return null;
            return ImageDecoder.DecodeBytes(buf[start..(end + 2)], maxEdge);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static int IndexOf(byte[] hay, int len, byte[] needle, int from = 0)
    {
        for (var i = from; i <= len - needle.Length; i++)
        {
            var ok = true;
            for (var j = 0; j < needle.Length; j++)
                if (hay[i + j] != needle[j]) { ok = false; break; }
            if (ok) return i;
        }
        return -1;
    }
}
