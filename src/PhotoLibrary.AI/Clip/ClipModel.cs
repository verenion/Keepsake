using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace PhotoLibrary.AI.Clip;

/// <summary>
/// CLIP ViT-B/32 via ONNX Runtime: maps images and text into the same 512-d space, so a text query can be
/// scored against photos by cosine similarity. Every embedding returned here is L2-normalised, so the
/// similarity of two is simply their dot product.
/// </summary>
public sealed class ClipModel : IDisposable
{
    public const int ImageSize = 224;
    public int Dimension { get; }

    /// <summary>Where the sessions ended up: "CPU" or "GPU n (name)". Set after construction.</summary>
    public string Device { get; private set; } = "CPU";

    /// <summary>Why the GPU was not used when one was requested, or null.</summary>
    public string? GpuFallbackReason { get; private set; }

    private static readonly float[] Mean = [0.48145466f, 0.4578275f, 0.40821073f];
    private static readonly float[] Std = [0.26862954f, 0.26130258f, 0.27577711f];

    private readonly InferenceSession _vision;
    private readonly InferenceSession _text;
    private readonly ClipTokenizer _tokenizer;
    private readonly string _visionInput, _visionOutput, _textIdsInput, _textMaskInput, _textOutput;
    private readonly object _textGate = new();

    /// <summary>
    /// DirectML wants sequential execution and no memory-pattern planning; the EP docs are explicit about both.
    /// Optimisation stays at BASIC: the extended fusions crash ONNX Runtime 1.23 (access violation during session
    /// creation) on the int8 vision models when DirectML is attached. BASIC is safe for every model in the catalogue.
    /// </summary>
    private static SessionOptions GpuOptions(int deviceId)
    {
        var opts = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_BASIC,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            EnableMemoryPattern = false,
        };
        opts.AppendExecutionProvider_DML(deviceId);
        return opts;
    }

    public ClipModel() : this(ClipModelFiles.Default) { }

    public ClipModel(ClipModelFiles files, ClipRuntime? runtime = null)
        : this(files.VisionModel, files.TextModel, files.Vocab, files.Merges, runtime) { }

    public ClipModel(string visionPath, string textPath, string vocabPath, string mergesPath, ClipRuntime? runtime = null)
    {
        runtime ??= ClipRuntime.Cpu;
        InferenceSession? vision = null, text = null;
        if (runtime.UseGpu)
        {
            try
            {
                vision = new InferenceSession(visionPath, GpuOptions(runtime.GpuDeviceId));
                text = new InferenceSession(textPath, GpuOptions(runtime.GpuDeviceId));
                var name = GpuDevices.List().FirstOrDefault(d => d.Index == runtime.GpuDeviceId)?.Name;
                Device = name is null ? $"GPU {runtime.GpuDeviceId}" : $"GPU {runtime.GpuDeviceId} ({name})";
            }
            catch (Exception ex)
            {
                vision?.Dispose();
                text?.Dispose();
                vision = text = null;
                GpuFallbackReason = ex.Message;
            }
        }
        if (vision is null || text is null)
        {
            var cpu = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
            vision = new InferenceSession(visionPath, cpu);
            text = new InferenceSession(textPath, cpu);
            Device = "CPU";
        }
        _vision = vision;
        _text = text;
        _tokenizer = new ClipTokenizer(vocabPath, mergesPath);

        _visionInput = _vision.InputMetadata.Keys.First();
        _visionOutput = PickEmbedOutput(_vision, "image_embeds");
        _textIdsInput = _text.InputMetadata.Keys.FirstOrDefault(k => k.Contains("input_ids")) ?? _text.InputMetadata.Keys.First();
        _textMaskInput = _text.InputMetadata.Keys.FirstOrDefault(k => k.Contains("attention_mask")) ?? "";
        _textOutput = PickEmbedOutput(_text, "text_embeds");
        Dimension = _vision.OutputMetadata[_visionOutput].Dimensions[^1];
    }

    private static string PickEmbedOutput(InferenceSession s, string preferred)
    {
        if (s.OutputMetadata.ContainsKey(preferred)) return preferred;
        return s.OutputMetadata.First(o => o.Value.Dimensions.Length == 2).Key;
    }

    /// <summary>Resize shortest side to 224, centre-crop, scale to [0,1] and normalise with CLIP's mean/std into NCHW.</summary>
    public static void Preprocess(SKBitmap bmp, DenseTensor<float> tensor, int batchIndex)
    {
        var scale = (float)ImageSize / Math.Min(bmp.Width, bmp.Height);
        var w = Math.Max(ImageSize, (int)MathF.Round(bmp.Width * scale));
        var h = Math.Max(ImageSize, (int)MathF.Round(bmp.Height * scale));
        using var resized = bmp.Resize(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul), new SKSamplingOptions(SKCubicResampler.CatmullRom));
        var ox = (w - ImageSize) / 2;
        var oy = (h - ImageSize) / 2;
        var pixels = resized.Pixels;
        for (var y = 0; y < ImageSize; y++)
        {
            var row = (y + oy) * w + ox;
            for (var x = 0; x < ImageSize; x++)
            {
                var c = pixels[row + x];
                tensor[batchIndex, 0, y, x] = (c.Red / 255f - Mean[0]) / Std[0];
                tensor[batchIndex, 1, y, x] = (c.Green / 255f - Mean[1]) / Std[1];
                tensor[batchIndex, 2, y, x] = (c.Blue / 255f - Mean[2]) / Std[2];
            }
        }
    }

    public float[] EmbedImage(SKBitmap bmp) => EmbedImages([bmp])[0];

    /// <summary>One batched forward pass. Preprocessing is parallel; the model call itself is one ORT run.</summary>
    public float[][] EmbedImages(IReadOnlyList<SKBitmap> images)
    {
        if (images.Count == 0) return [];
        var tensor = new DenseTensor<float>([images.Count, 3, ImageSize, ImageSize]);
        Parallel.For(0, images.Count, i => Preprocess(images[i], tensor, i));
        return EmbedPreprocessed(tensor, images.Count);
    }

    public float[][] EmbedPreprocessed(DenseTensor<float> tensor, int count)
    {
        var inputs = new[] { NamedOnnxValue.CreateFromTensor(_visionInput, tensor) };
        using var results = _vision.Run(inputs, [_visionOutput]);
        return SplitAndNormalise(results[0].AsTensor<float>(), count);
    }

    public float[] EmbedText(string text) => EmbedTexts([text])[0];

    public float[][] EmbedTexts(IReadOnlyList<string> texts)
    {
        if (texts.Count == 0) return [];
        var ids = new DenseTensor<long>([texts.Count, ClipTokenizer.ContextLength]);
        var mask = new DenseTensor<long>([texts.Count, ClipTokenizer.ContextLength]);
        lock (_textGate)
        {
            for (var i = 0; i < texts.Count; i++)
            {
                var (tokIds, tokMask) = _tokenizer.Encode(texts[i]);
                for (var j = 0; j < ClipTokenizer.ContextLength; j++) { ids[i, j] = tokIds[j]; mask[i, j] = tokMask[j]; }
            }
            var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(_textIdsInput, ids) };
            if (_textMaskInput.Length > 0) inputs.Add(NamedOnnxValue.CreateFromTensor(_textMaskInput, mask));
            using var results = _text.Run(inputs, [_textOutput]);
            return SplitAndNormalise(results[0].AsTensor<float>(), texts.Count);
        }
    }

    private static float[][] SplitAndNormalise(Tensor<float> output, int count)
    {
        var dim = output.Dimensions[^1];
        var flat = output.ToArray();
        var result = new float[count][];
        for (var i = 0; i < count; i++)
        {
            var v = new float[dim];
            Array.Copy(flat, i * dim, v, 0, dim);
            Normalise(v);
            result[i] = v;
        }
        return result;
    }

    public static void Normalise(float[] v)
    {
        var sum = 0f;
        foreach (var x in v) sum += x * x;
        var norm = MathF.Sqrt(sum);
        if (norm == 0) return;
        for (var i = 0; i < v.Length; i++) v[i] /= norm;
    }

    public static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var n = a.Length;
        var w = System.Numerics.Vector<float>.Count;
        var acc = System.Numerics.Vector<float>.Zero;
        var i = 0;
        for (; i + w <= n; i += w)
            acc += new System.Numerics.Vector<float>(a.Slice(i, w)) * new System.Numerics.Vector<float>(b.Slice(i, w));
        var s = System.Numerics.Vector.Dot(acc, System.Numerics.Vector<float>.One);
        for (; i < n; i++) s += a[i] * b[i];
        return s;
    }

    public void Dispose()
    {
        _vision.Dispose();
        _text.Dispose();
    }
}
