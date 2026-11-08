using System.Diagnostics;
using System.Globalization;
using PhotoLibrary.Core.Imaging;
using SkiaSharp;

namespace PhotoLibrary.Core.Video;

/// <summary>One sampled frame: where it sits in the video and its pixels.</summary>
public sealed record VideoFrame(int Ms, SKBitmap Bitmap) : IDisposable
{
    public void Dispose() => Bitmap.Dispose();
}

/// <summary>
/// Samples frames through a video for the AI index: one every <c>interval</c> seconds, spread further apart on
/// long videos so there are never more than <c>maxFrames</c>. A single ffmpeg run decodes only keyframes
/// (<c>-skip_frame nokey</c>), which is many times faster than decoding the whole stream; phones write a
/// keyframe every second or two, so the sampled times land within a second or so of the requested ones.
/// </summary>
public static class VideoFrames
{
    /// <summary>The spacing actually used: the requested interval, widened so a long video stays under the cap.</summary>
    public static double EffectiveInterval(double intervalSeconds, int maxFrames, long? durationMs)
    {
        var interval = Math.Max(1, intervalSeconds);
        if (durationMs is > 0 && maxFrames > 0) interval = Math.Max(interval, durationMs.Value / 1000.0 / maxFrames);
        return interval;
    }

    public static List<VideoFrame> Sample(string path, double intervalSeconds, int maxFrames, long? durationMs, int maxEdge, CancellationToken ct = default)
    {
        var ffmpeg = Ffmpeg.ExePath;
        if (ffmpeg is null) return [];
        var interval = EffectiveInterval(intervalSeconds, maxFrames, durationMs);
        var psi = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        string[] args =
        [
            "-v", "error", "-nostdin", "-skip_frame", "nokey", "-i", path,
            "-vf", $"fps=1/{interval.ToString("0.###", CultureInfo.InvariantCulture)},scale='min({maxEdge},iw)':'min({maxEdge},ih)':force_original_aspect_ratio=decrease",
            "-frames:v", maxFrames.ToString(CultureInfo.InvariantCulture),
            "-f", "image2pipe", "-vcodec", "mjpeg", "-q:v", "4", "-",
        ];
        foreach (var a in args) psi.ArgumentList.Add(a);

        byte[] output;
        try
        {
            using var proc = Process.Start(psi);
            if (proc is null) return [];
            _ = proc.StandardError.ReadToEndAsync(ct);
            // Generous but bounded: keyframe-only decoding runs far faster than real time.
            var timeout = TimeSpan.FromSeconds(60 + (durationMs ?? 0) / 1000.0 / 4);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            using var ms = new MemoryStream();
            try { proc.StandardOutput.BaseStream.CopyToAsync(ms, cts.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                ct.ThrowIfCancellationRequested();
                return [];
            }
            proc.WaitForExit(5_000);
            output = ms.ToArray();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return [];
        }

        var frames = new List<VideoFrame>();
        var i = 0;
        foreach (var jpeg in SplitJpegs(output))
        {
            var bmp = ImageDecoder.DecodeBytes(jpeg, maxEdge);
            if (bmp is not null) frames.Add(new VideoFrame((int)Math.Round(i * interval * 1000), bmp));
            i++;
        }
        return frames;
    }

    /// <summary>
    /// Cuts a stream of back-to-back JPEGs (ffmpeg's image2pipe) into separate images. Inside entropy-coded
    /// data every 0xFF byte is stuffed with 0x00, so the end-of-image marker FF D9 only appears at the end.
    /// </summary>
    public static IEnumerable<byte[]> SplitJpegs(byte[] data)
    {
        var start = -1;
        for (var i = 0; i + 1 < data.Length; i++)
        {
            if (data[i] != 0xFF) continue;
            if (start < 0 && data[i + 1] == 0xD8) { start = i; i++; }
            else if (start >= 0 && data[i + 1] == 0xD9)
            {
                yield return data[start..(i + 2)];
                start = -1;
                i++;
            }
        }
    }
}
