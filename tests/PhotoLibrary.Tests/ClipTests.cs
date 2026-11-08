using PhotoLibrary.AI.Clip;
using PhotoLibrary.AI.Features;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace PhotoLibrary.Tests;

/// <summary>
/// Tokenizer and model checks against the downloaded CLIP files. Skips quietly when the model isn't installed
/// (it is ~150 MB and fetched on demand from the AI tab).
/// </summary>
public class ClipTests(ITestOutputHelper output)
{
    private static bool Installed => ClipModelFiles.Default.IsInstalled;

    [Fact]
    public void TokenizerMatchesReferenceIds()
    {
        if (!Installed) { output.WriteLine("model not installed; skipped"); return; }
        var tok = new ClipTokenizer(ClipModelFiles.Default.Vocab, ClipModelFiles.Default.Merges);

        // Reference ids from openai/clip simple_tokenizer.
        Assert.Equal([320, 1125, 539, 320, 2368], tok.EncodeTokens("a photo of a cat"));
        Assert.Equal([3306, 1002], tok.EncodeTokens("Hello   World"));
        Assert.Equal(tok.EncodeTokens("a photo of a cat"), tok.EncodeTokens("A PHOTO of a Cat"));

        var (ids, mask) = tok.Encode("a photo of a cat");
        Assert.Equal(ClipTokenizer.ContextLength, ids.Length);
        Assert.Equal(ClipTokenizer.StartOfText, ids[0]);
        Assert.Equal(ClipTokenizer.EndOfText, ids[6]);
        Assert.Equal(7, mask.Sum());
        Assert.All(ids.Skip(7), id => Assert.Equal(ClipTokenizer.EndOfText, id));
    }

    [Fact]
    public void TokenizerTruncatesLongText()
    {
        if (!Installed) { output.WriteLine("model not installed; skipped"); return; }
        var tok = new ClipTokenizer(ClipModelFiles.Default.Vocab, ClipModelFiles.Default.Merges);
        var (ids, mask) = tok.Encode(string.Join(" ", Enumerable.Repeat("dog", 200)));
        Assert.Equal(ClipTokenizer.ContextLength, ids.Length);
        Assert.Equal(ClipTokenizer.EndOfText, ids[^1]);
        Assert.Equal(ClipTokenizer.ContextLength, mask.Sum());
    }

    [Fact]
    public void TextEmbeddingsAreUnitLengthAndSemantic()
    {
        if (!Installed) { output.WriteLine("model not installed; skipped"); return; }
        using var model = new ClipModel();
        Assert.Equal(512, model.Dimension);

        var e = model.EmbedTexts(["a photo of a dog", "a photo of a puppy", "a spreadsheet of quarterly sales figures"]);
        Assert.All(e, v => Assert.InRange(MathF.Sqrt(v.Sum(x => x * x)), 0.999f, 1.001f));
        var dogPuppy = ClipModel.Dot(e[0], e[1]);
        var dogSpreadsheet = ClipModel.Dot(e[0], e[2]);
        output.WriteLine($"dog~puppy {dogPuppy:0.000}  dog~spreadsheet {dogSpreadsheet:0.000}");
        // CLIP text embeddings are anisotropic (unrelated sentences still score ~0.85), so compare, don't threshold.
        Assert.True(dogPuppy > dogSpreadsheet + 0.05f);

        // Same text embedded alone or in a batch gives the same vector.
        var alone = model.EmbedText("a photo of a dog");
        Assert.True(ClipModel.Dot(alone, e[0]) > 0.999f);
    }

    [Fact]
    public void ImageEmbeddingsMatchTextAndBatch()
    {
        if (!Installed) { output.WriteLine("model not installed; skipped"); return; }
        using var model = new ClipModel();
        using var red = Solid(300, 200, SKColors.Red);
        using var blue = Solid(200, 300, SKColors.Blue);

        var batch = model.EmbedImages([red, blue]);
        var redAlone = model.EmbedImage(red);
        // int8 dynamic quantisation scales activations per run, so batch composition shifts vectors very slightly.
        Assert.True(ClipModel.Dot(batch[0], redAlone) > 0.98f, "batched and single embeddings should agree");
        Assert.True(ClipModel.Dot(batch[0], batch[1]) < 0.97f, "different images should differ");

        var texts = model.EmbedTexts(["a red image", "a blue image"]);
        var redRed = ClipModel.Dot(batch[0], texts[0]);
        var redBlue = ClipModel.Dot(batch[0], texts[1]);
        var blueBlue = ClipModel.Dot(batch[1], texts[1]);
        var blueRed = ClipModel.Dot(batch[1], texts[0]);
        output.WriteLine($"red: red {redRed:0.000} blue {redBlue:0.000}   blue: blue {blueBlue:0.000} red {blueRed:0.000}");
        Assert.True(redRed > redBlue);
        Assert.True(blueBlue > blueRed);
    }

    [Fact]
    public void ZeroShotTaggerReturnsRankedTags()
    {
        if (!Installed) { output.WriteLine("model not installed; skipped"); return; }
        using var model = new ClipModel();
        var tagger = new ZeroShotTagger(model);
        using var img = Solid(256, 256, SKColors.ForestGreen);
        var tags = tagger.Tag(model.EmbedImage(img));
        output.WriteLine(string.Join(", ", tags.Select(t => $"{t.Tag} {t.Score:0.00}")));
        Assert.InRange(tags.Count, 0, tagger.MaxTags);
        for (var i = 1; i < tags.Count; i++) Assert.True(tags[i - 1].Score >= tags[i].Score);
        Assert.All(tags, t => Assert.DoesNotContain(t.Tag, new[] { "a ", "an ", "the " }.Select(p => p)));
        Assert.Equal("beach", ZeroShotTagger.TagName("a beach"));
        Assert.Equal("sea", ZeroShotTagger.TagName("the sea"));
        Assert.Equal("people", ZeroShotTagger.TagName("people"));
    }

    [Fact]
    public void GpuAgreesWithCpu()
    {
        if (!Installed) { output.WriteLine("model not installed; skipped"); return; }
        var gpus = GpuDevices.List();
        output.WriteLine("gpus: " + string.Join("; ", gpus.Select(g => g.Label)));
        if (gpus.Count == 0) { output.WriteLine("no GPU; skipped"); return; }

        using var cpu = new ClipModel();
        using var gpu = new ClipModel(ClipModelFiles.Default, new ClipRuntime(true, gpus[0].Index));
        output.WriteLine($"gpu device: {gpu.Device}  fallback: {gpu.GpuFallbackReason ?? "none"}");
        Assert.Equal("CPU", cpu.Device);
        if (gpu.GpuFallbackReason is not null) return; // no DirectML on this machine; the fallback path is what we just exercised

        Assert.StartsWith("GPU", gpu.Device);
        var text = ClipModel.Dot(cpu.EmbedText("a photo of a dog"), gpu.EmbedText("a photo of a dog"));
        using var img = Solid(300, 200, SKColors.Red);
        var image = ClipModel.Dot(cpu.EmbedImage(img), gpu.EmbedImage(img));
        output.WriteLine($"cpu~gpu text {text:0.000} image {image:0.000}");
        Assert.True(text > 0.98f);
        Assert.True(image > 0.98f);
    }

    private static SKBitmap Solid(int w, int h, SKColor color)
    {
        var bmp = new SKBitmap(w, h);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(color);
        return bmp;
    }
}
