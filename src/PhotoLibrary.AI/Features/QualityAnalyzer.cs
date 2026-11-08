using SkiaSharp;

namespace PhotoLibrary.AI.Features;

/// <summary>Cheap per-photo measurements taken from the cached thumbnail.</summary>
/// <param name="Brightness">Mean luminance, 0 (black) to 1 (white).</param>
/// <param name="Contrast">Standard deviation of luminance, 0 (flat colour) to about 0.5.</param>
/// <param name="Sharpness">Variance of the Laplacian on a 256px copy; under ~20 is soft, over ~200 is crisp.</param>
public sealed record QualityMetrics(float Brightness, float Contrast, float Sharpness);

public sealed record QualityVerdict(string Reason, float Severity);

/// <summary>
/// Finds photos that are probably not worth keeping: nearly black, nearly white, a flat colour, or blurry.
/// Measurements are stored once; the strictness slider only changes the thresholds applied to them.
/// </summary>
public static class QualityAnalyzer
{
    /// <summary>Bump when the measurement changes so stored rows are recomputed.</summary>
    public const int Version = 1;

    private const int Edge = 256;

    public static QualityMetrics Measure(SKBitmap source)
    {
        // Fixed size so sharpness numbers are comparable across photos and thumbnail sizes.
        var scale = (float)Edge / Math.Max(source.Width, source.Height);
        var w = Math.Max(8, (int)Math.Round(source.Width * scale));
        var h = Math.Max(8, (int)Math.Round(source.Height * scale));
        using var bmp = source.Resize(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear))
            ?? throw new InvalidOperationException("resize failed");

        var grey = new float[w * h];
        var pixels = bmp.GetPixelSpan();
        double sum = 0, sumSq = 0;
        for (var i = 0; i < w * h; i++)
        {
            var o = i * 4;
            var y = 0.114f * pixels[o] + 0.587f * pixels[o + 1] + 0.299f * pixels[o + 2]; // BGRA
            grey[i] = y;
            sum += y;
            sumSq += y * y;
        }
        var n = (double)(w * h);
        var mean = sum / n;
        var variance = Math.Max(0, sumSq / n - mean * mean);

        // Laplacian (4-neighbour) over the interior; its variance is the classic focus measure.
        double lapSum = 0, lapSq = 0;
        var m = 0;
        for (var y = 1; y < h - 1; y++)
        for (var x = 1; x < w - 1; x++)
        {
            var i = y * w + x;
            var lap = 4 * grey[i] - grey[i - 1] - grey[i + 1] - grey[i - w] - grey[i + w];
            lapSum += lap;
            lapSq += lap * lap;
            m++;
        }
        var lapMean = lapSum / m;
        var sharpness = Math.Max(0, lapSq / m - lapMean * lapMean);

        return new QualityMetrics((float)(mean / 255.0), (float)(Math.Sqrt(variance) / 255.0), (float)sharpness);
    }

    /// <summary>
    /// Decide whether a photo is "poor" at the given strictness (0 = only the hopeless cases, 1 = anything
    /// marginal). Returns null when it passes. Severity is 0..1, higher is worse, for sorting.
    /// </summary>
    public static QualityVerdict? Judge(QualityMetrics q, float strictness)
    {
        strictness = Math.Clamp(strictness, 0, 1);
        var dark = Lerp(0.04f, 0.14f, strictness);
        var bright = Lerp(0.97f, 0.90f, strictness);
        var flat = Lerp(0.02f, 0.07f, strictness);
        var blur = Lerp(6f, 60f, strictness * strictness); // sharpness is heavy-tailed; ease in

        if (q.Contrast < flat)
        {
            var what = q.Brightness < 0.2f ? "Almost completely black" : q.Brightness > 0.8f ? "Almost completely white" : "A flat, featureless image";
            return new QualityVerdict($"{what} (contrast {q.Contrast:P0})", 1f - q.Contrast / flat);
        }
        if (q.Brightness < dark) return new QualityVerdict($"Very dark (brightness {q.Brightness:P0})", 1f - q.Brightness / dark);
        if (q.Brightness > bright) return new QualityVerdict($"Blown out (brightness {q.Brightness:P0})", (q.Brightness - bright) / (1f - bright));
        if (q.Sharpness < blur) return new QualityVerdict($"Blurry (sharpness {q.Sharpness:0})", 1f - q.Sharpness / blur);
        return null;
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
