using PhotoLibrary.Core.Db;
using PhotoLibrary.Core.Imaging;
using SkiaSharp;

namespace PhotoLibrary.Core.Thumbnails;

/// <summary>
/// Disk cache of small JPEG thumbnails keyed by content hash, so duplicates share one and re-filing a photo
/// never invalidates it. Returns the cached file path; the UI loads it as a bitmap.
/// </summary>
public sealed class ThumbnailService(string cacheDir)
{
    public const int Edge = 320;

    private readonly SemaphoreSlim _decodeGate = new(Math.Clamp(Environment.ProcessorCount - 1, 2, 6));

    public string CacheDir { get; } = cacheDir;

    public string PathFor(string sha) => Path.Combine(CacheDir, sha[..2], sha + ".jpg");

    public string? Cached(MediaRecord r)
    {
        var p = PathFor(r.Sha256);
        return File.Exists(p) ? p : null;
    }

    /// <summary>Returns the thumbnail path, generating it if needed; null when the file can't be decoded.</summary>
    public async Task<string?> GetAsync(MediaRecord r, CancellationToken ct)
    {
        var p = PathFor(r.Sha256);
        if (File.Exists(p)) return p;
        var failed = p + ".none";
        if (File.Exists(failed)) return null;
        await _decodeGate.WaitAsync(ct);
        try
        {
            if (File.Exists(p)) return p;
            return await Task.Run(() => Generate(r, p, failed), ct);
        }
        finally
        {
            _decodeGate.Release();
        }
    }

    private static string? Generate(MediaRecord r, string dest, string failedMarker)
    {
        // Without ffmpeg a video can't be decoded at all. That is a missing tool, not a bad file, so no failure
        // marker: the thumbnail is attempted again as soon as ffmpeg is installed.
        if (r.Kind == MediaKind.Video && !ImageDecoder.FfmpegAvailable) return null;
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        using var bmp = ImageDecoder.Decode(r.Path, Edge, r.Kind);
        if (bmp is null)
        {
            try { File.WriteAllBytes(failedMarker, []); } catch (IOException) { }
            return null;
        }
        using var data = bmp.Encode(SKEncodedImageFormat.Jpeg, 82);
        if (data is null) return null;
        var tmp = dest + "." + Environment.CurrentManagedThreadId + ".tmp";
        using (var fs = File.Create(tmp)) data.SaveTo(fs);
        try { File.Move(tmp, dest, overwrite: true); }
        catch (IOException) { try { File.Delete(tmp); } catch (IOException) { } }
        return File.Exists(dest) ? dest : null;
    }

    /// <summary>Decodes a larger version for the preview pane (not cached).</summary>
    public static SKBitmap? Preview(MediaRecord r, int maxEdge, int? atMs = null) =>
        r.Kind == MediaKind.Video && atMs is { } ms ? ImageDecoder.DecodeVideoFrameAt(r.Path, maxEdge, ms) : ImageDecoder.Decode(r.Path, maxEdge, r.Kind);

    /// <summary>Drops the "could not decode" markers so these files are tried again, e.g. after ffmpeg was installed.</summary>
    public int ForgetFailures(IEnumerable<MediaRecord> records)
    {
        var n = 0;
        foreach (var r in records)
        {
            var marker = PathFor(r.Sha256) + ".none";
            try { if (File.Exists(marker)) { File.Delete(marker); n++; } } catch (IOException) { }
        }
        return n;
    }

    public void Forget(string sha)
    {
        foreach (var p in new[] { PathFor(sha), PathFor(sha) + ".none" })
            try { if (File.Exists(p)) File.Delete(p); } catch (IOException) { }
    }
}
