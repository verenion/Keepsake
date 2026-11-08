using System.Diagnostics;
using SkiaSharp;

namespace PhotoLibrary.Core.Imaging;

/// <summary>
/// Decodes any supported media to an upright bitmap no larger than a requested edge. JPEG/PNG/WebP/GIF/BMP
/// go through Skia; RAW files use their embedded JPEG preview; videos use ffmpeg when it is on PATH; HEIC
/// uses libheif when available (see HeicDecoder). Returns null when nothing can decode the file.
/// </summary>
public static class ImageDecoder
{
    public static SKBitmap? Decode(string path, int maxEdge, MediaKind kind)
    {
        try
        {
            if (kind == MediaKind.Video) return DecodeVideoFrame(path, maxEdge);
            if (MediaKinds.IsRaw(path)) return DecodeRaw(path, maxEdge);
            var ext = Path.GetExtension(path);
            if (ext.Equals(".heic", StringComparison.OrdinalIgnoreCase) || ext.Equals(".heif", StringComparison.OrdinalIgnoreCase))
                return HeicDecoder.DecodeNative(path, maxEdge) ?? DecodeWithFfmpeg(path, maxEdge, seekSeconds: null) ?? HeicDecoder.EmbeddedThumbnail(path, maxEdge);
            if (ext.Equals(".psd", StringComparison.OrdinalIgnoreCase) || ext.Equals(".psb", StringComparison.OrdinalIgnoreCase))
                return PsdDecoder.Decode(path, maxEdge);
            if (IsTiff(ext))
                return OrientTiff(WicImage.DecodeFile(path, maxEdge), () => TiffPreview.Inspect(path, readPreview: false))
                       ?? DecodeWithFfmpeg(path, maxEdge, seekSeconds: null);
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            return DecodeStream(fs, maxEdge);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    private static bool IsTiff(string ext) =>
        ext.Equals(".tif", StringComparison.OrdinalIgnoreCase) || ext.Equals(".tiff", StringComparison.OrdinalIgnoreCase);

    /// <summary>Windows' TIFF decoder hands back pixels as stored; turn them upright by the file's Orientation tag.</summary>
    private static SKBitmap? OrientTiff(SKBitmap? bmp, Func<TiffInfo?> inspect)
    {
        if (bmp is null) return null;
        var origin = OriginFromExif(inspect()?.Orientation);
        return origin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default ? bmp : Orient(bmp, origin);
    }

    /// <summary>
    /// Decodes a file held in memory (a decrypted vault item), choosing the decoder by its original extension.
    /// Everything stays in memory; formats that need a file on disk (video, HEIC without an embedded preview)
    /// return null rather than writing one.
    /// </summary>
    public static SKBitmap? DecodeInMemory(byte[] data, string originalName, MediaKind kind, int maxEdge)
    {
        if (kind == MediaKind.Video) return null;
        try
        {
            var ext = Path.GetExtension(originalName);
            using var ms = new MemoryStream(data, writable: false);
            if (MediaKinds.IsRaw(originalName))
            {
                var tiff = TiffPreview.Inspect(ms);
                if (tiff is null) return null;
                SKBitmap? bmp = null;
                var origin = SKEncodedOrigin.TopLeft;
                if (tiff.Preview is not null)
                {
                    using var pv = new MemoryStream(tiff.Preview, writable: false);
                    bmp = DecodeCore(pv, maxEdge, out origin);
                }
                if (bmp is null && tiff.Raw is not null) { ms.Position = 0; bmp = CfaPreview.Decode(ms, tiff.Raw, maxEdge); }
                if (bmp is null) return null;
                if (origin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default) origin = OriginFromExif(tiff.Orientation);
                return Orient(bmp, origin);
            }
            if (ext.Equals(".psd", StringComparison.OrdinalIgnoreCase) || ext.Equals(".psb", StringComparison.OrdinalIgnoreCase))
                return PsdDecoder.Decode(ms, maxEdge);
            if (ext.Equals(".heic", StringComparison.OrdinalIgnoreCase) || ext.Equals(".heif", StringComparison.OrdinalIgnoreCase))
                return WicImage.DecodeMemory(data, maxEdge) ?? HeicDecoder.EmbeddedThumbnail(data, maxEdge);
            if (IsTiff(ext))
                return OrientTiff(WicImage.DecodeMemory(data, maxEdge), () => { ms.Position = 0; return TiffPreview.Inspect(ms, readPreview: false); });
            return DecodeStream(ms, maxEdge);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// RAW files: decode the embedded JPEG preview, or render the sensor data when there is none. The preview
    /// carries no EXIF of its own, so the orientation comes from the TIFF header (Pixel DNGs are nearly all
    /// rotated); a preview that does declare its own orientation is trusted instead.
    /// </summary>
    private static SKBitmap? DecodeRaw(string path, int maxEdge)
    {
        var tiff = TiffPreview.Inspect(path);
        if (tiff is null) return null;
        SKBitmap? bmp = null;
        var origin = SKEncodedOrigin.TopLeft;
        if (tiff.Preview is not null)
        {
            using var ms = new MemoryStream(tiff.Preview, writable: false);
            bmp = DecodeCore(ms, maxEdge, out origin);
        }
        bmp ??= tiff.Raw is null ? null : CfaPreview.Decode(path, tiff.Raw, maxEdge);
        if (bmp is null) return null;
        if (origin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default) origin = OriginFromExif(tiff.Orientation);
        return Orient(bmp, origin);
    }

    public static SKBitmap? DecodeBytes(byte[] data, int maxEdge)
    {
        using var ms = new MemoryStream(data, writable: false);
        return DecodeStream(ms, maxEdge);
    }

    public static SKBitmap? DecodeStream(Stream stream, int maxEdge)
    {
        var bmp = DecodeCore(stream, maxEdge, out var origin);
        return bmp is null ? null : Orient(bmp, origin);
    }

    /// <summary>Decodes and shrinks without applying the orientation, which is handed back for the caller to apply.</summary>
    private static SKBitmap? DecodeCore(Stream stream, int maxEdge, out SKEncodedOrigin origin)
    {
        origin = SKEncodedOrigin.TopLeft;
        using var codec = SKCodec.Create(stream);
        if (codec is null) return null;
        origin = codec.EncodedOrigin;
        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0) return null;

        // JPEG can decode straight to a smaller size (DCT scaling): much faster for 20MP camera files.
        var scale = Math.Min(1f, (float)maxEdge / Math.Max(info.Width, info.Height));
        var size = codec.GetScaledDimensions(scale);
        var target = new SKImageInfo(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var bmp = new SKBitmap(target);
        var result = codec.GetPixels(target, bmp.GetPixels());
        if (result != SKCodecResult.Success && result != SKCodecResult.IncompleteInput)
        {
            bmp.Dispose();
            return null;
        }
        return FitWithin(bmp, maxEdge);
    }

    /// <summary>Shrinks (never grows) so the longest edge is at most maxEdge.</summary>
    public static SKBitmap FitWithin(SKBitmap bmp, int maxEdge)
    {
        var longest = Math.Max(bmp.Width, bmp.Height);
        if (longest <= maxEdge) return bmp;
        var s = (float)maxEdge / longest;
        var info = new SKImageInfo(Math.Max(1, (int)Math.Round(bmp.Width * s)), Math.Max(1, (int)Math.Round(bmp.Height * s)),
            SKColorType.Bgra8888, SKAlphaType.Premul);
        var resized = bmp.Resize(info, new SKSamplingOptions(SKCubicResampler.Mitchell));
        bmp.Dispose();
        return resized ?? throw new InvalidOperationException("resize failed");
    }

    /// <summary>Applies the EXIF orientation so the bitmap is upright.</summary>
    public static SKBitmap Orient(SKBitmap src, SKEncodedOrigin origin)
    {
        if (origin == SKEncodedOrigin.TopLeft || origin == SKEncodedOrigin.Default) return src;
        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var w = swap ? src.Height : src.Width;
        var h = swap ? src.Width : src.Height;
        var dst = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(dst))
        {
            canvas.Clear(SKColors.Transparent);
            switch (origin)
            {
                case SKEncodedOrigin.TopRight: canvas.Translate(w, 0); canvas.Scale(-1, 1); break;
                case SKEncodedOrigin.BottomRight: canvas.Translate(w, h); canvas.RotateDegrees(180); break;
                case SKEncodedOrigin.BottomLeft: canvas.Translate(0, h); canvas.Scale(1, -1); break;
                case SKEncodedOrigin.LeftTop: canvas.RotateDegrees(90); canvas.Scale(1, -1); break;
                case SKEncodedOrigin.RightTop: canvas.Translate(w, 0); canvas.RotateDegrees(90); break;
                case SKEncodedOrigin.RightBottom: canvas.Translate(w, h); canvas.RotateDegrees(270); canvas.Scale(1, -1); canvas.Translate(0, -w); canvas.Translate(0, 0); break;
                case SKEncodedOrigin.LeftBottom: canvas.Translate(0, h); canvas.RotateDegrees(270); break;
            }
            canvas.DrawBitmap(src, 0, 0, new SKSamplingOptions(SKFilterMode.Linear));
        }
        src.Dispose();
        return dst;
    }

    public static SKEncodedOrigin OriginFromExif(int? orientation) => orientation switch
    {
        2 => SKEncodedOrigin.TopRight, 3 => SKEncodedOrigin.BottomRight, 4 => SKEncodedOrigin.BottomLeft,
        5 => SKEncodedOrigin.LeftTop, 6 => SKEncodedOrigin.RightTop, 7 => SKEncodedOrigin.RightBottom,
        8 => SKEncodedOrigin.LeftBottom, _ => SKEncodedOrigin.TopLeft,
    };

    // ---- video via ffmpeg

    public static bool FfmpegAvailable => Video.Ffmpeg.Available;

    /// <summary>A frame from one second in (skipping the black lead-in many phone clips start with), else the first frame.</summary>
    /// <summary>One frame from a given moment of a video (for showing where a search matched).</summary>
    public static SKBitmap? DecodeVideoFrameAt(string path, int maxEdge, int ms) =>
        DecodeWithFfmpeg(path, maxEdge, seekSeconds: ms / 1000.0) ?? DecodeVideoFrame(path, maxEdge);

    private static SKBitmap? DecodeVideoFrame(string path, int maxEdge) =>
        DecodeWithFfmpeg(path, maxEdge, seekSeconds: 1.0) ?? DecodeWithFfmpeg(path, maxEdge, seekSeconds: null);

    /// <summary>
    /// Runs ffmpeg to pull one frame (or, for a still image it can read such as HEIC, the image itself) as a
    /// JPEG over a pipe. The orientation is applied by ffmpeg's autorotate, so the result is upright.
    /// </summary>
    private static SKBitmap? DecodeWithFfmpeg(string path, int maxEdge, double? seekSeconds)
    {
        var ffmpeg = Video.Ffmpeg.ExePath;
        if (ffmpeg is null) return null;
        var psi = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        var args = new List<string> { "-v", "error", "-nostdin" };
        if (seekSeconds is { } s) args.AddRange(["-ss", s.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        args.AddRange(["-i", path, "-frames:v", "1", "-vf", $"scale='min({maxEdge},iw)':'min({maxEdge},ih)':force_original_aspect_ratio=decrease",
            "-f", "image2pipe", "-vcodec", "mjpeg", "-q:v", "4", "-"]);
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var proc = Process.Start(psi);
            if (proc is null) return null;
            using var ms = new MemoryStream();
            _ = proc.StandardError.ReadToEndAsync();
            proc.StandardOutput.BaseStream.CopyTo(ms);
            if (!proc.WaitForExit(60_000)) { try { proc.Kill(); } catch (InvalidOperationException) { } return null; }
            return ms.Length == 0 ? null : DecodeBytes(ms.ToArray(), maxEdge);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
