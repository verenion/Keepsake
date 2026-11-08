using PhotoLibrary.AI.Clip;

namespace PhotoLibrary.AI.Features;

/// <summary>
/// Zero-shot tagging: each label is embedded once as "a photo of …", and a photo's tags are the labels
/// whose text embedding is closest to its image embedding (softmax over CLIP's scaled similarities).
/// The label list is tuned for a family photo library rather than ImageNet's 1000 classes.
/// </summary>
public sealed class ZeroShotTagger
{
    public static readonly string[] Labels =
    [
        "people", "a selfie", "a group of people", "a baby", "a child", "a wedding", "a birthday party", "a christmas tree",
        "a dog", "a cat", "a bird", "a horse", "farm animals", "wild animals", "a zoo",
        "a beach", "the sea", "a swimming pool", "a lake", "a river", "a waterfall", "mountains", "a forest", "snow",
        "a sunset", "the night sky", "fireworks", "countryside", "a garden", "flowers", "a park", "a playground",
        "a city street", "a building", "a castle", "a church", "a museum", "a market", "a shop",
        "food", "a drink", "a restaurant", "a cake", "a kitchen", "a living room", "a bedroom", "a house",
        "a car", "a boat", "an airplane", "a train", "a bicycle", "a motorbike", "a road trip",
        "sports", "football", "hiking", "camping", "skiing", "a concert", "a festival", "a theme park",
        "a school", "an office", "a computer screen", "a screenshot", "a document", "text", "a receipt", "a whiteboard",
        "a painting", "a drawing", "a cartoon", "a toy", "a video game", "a pet", "a costume", "a holiday",
    ];

    public int MaxTags { get; init; } = 3;
    public float MinProbability { get; init; } = 0.08f;

    private readonly float[][] _labelEmbeddings;

    public ZeroShotTagger(ClipModel model)
    {
        _labelEmbeddings = model.EmbedTexts(Labels.Select(l => "a photo of " + l).ToArray());
    }

    /// <summary>Display form of a label: strip the article so the tag cloud reads "beach", not "a beach".</summary>
    public static string TagName(string label)
    {
        foreach (var prefix in new[] { "a ", "an ", "the " })
            if (label.StartsWith(prefix, StringComparison.Ordinal)) return label[prefix.Length..];
        return label;
    }

    public List<(string Tag, float Score)> Tag(ReadOnlySpan<float> imageEmbedding)
    {
        var logits = new float[Labels.Length];
        var max = float.MinValue;
        for (var i = 0; i < Labels.Length; i++)
        {
            logits[i] = 100f * ClipModel.Dot(imageEmbedding, _labelEmbeddings[i]);
            if (logits[i] > max) max = logits[i];
        }
        var sum = 0f;
        for (var i = 0; i < logits.Length; i++) { logits[i] = MathF.Exp(logits[i] - max); sum += logits[i]; }
        var ranked = new List<(string, float)>();
        for (var i = 0; i < logits.Length; i++)
        {
            var p = logits[i] / sum;
            if (p >= MinProbability) ranked.Add((TagName(Labels[i]), p));
        }
        ranked.Sort((a, b) => b.Item2.CompareTo(a.Item2));
        if (ranked.Count > MaxTags) ranked.RemoveRange(MaxTags, ranked.Count - MaxTags);
        return ranked;
    }
}
