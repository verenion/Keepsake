using PhotoLibrary.Core;

namespace PhotoLibrary.AI.Clip;

/// <summary>
/// One downloadable CLIP variant. <see cref="Id"/> names the embedding space: vectors from different
/// models are never compared, so each model keeps its own rows in the database.
/// </summary>
public sealed record ClipModelSpec(string Id, string DisplayName, string Description, string Repo, bool Quantized, long VisionBytes, long TextBytes)
{
    public string VisionFile => Quantized ? "vision_model_quantized.onnx" : "vision_model.onnx";
    public string TextFile => Quantized ? "text_model_quantized.onnx" : "text_model.onnx";
    public long TotalBytes => VisionBytes + TextBytes + ClipModelFiles.VocabBytes + ClipModelFiles.MergesBytes;
    public string SizeText => TotalBytes >= 1_000_000_000 ? $"{TotalBytes / 1e9:0.0} GB" : $"{TotalBytes / 1_000_000} MB";
}

/// <summary>How to run a model: on the CPU, or on a DirectX 12 GPU through DirectML (falls back to CPU if that fails).</summary>
public sealed record ClipRuntime(bool UseGpu, int GpuDeviceId)
{
    public static ClipRuntime Cpu { get; } = new(false, 0);
}

/// <summary>The models the app knows how to fetch (Xenova's ONNX exports on Hugging Face).</summary>
public static class ClipModelCatalog
{
    private const string Hf = "https://huggingface.co/Xenova/";

    public static readonly ClipModelSpec[] All =
    [
        new("clip-vit-base-patch32-q8", "CLIP ViT-B/32 (int8)",
            "Smallest and fastest. Good everyday search; the default.",
            Hf + "clip-vit-base-patch32", true, 89_117_001, 64_504_507),
        new("clip-vit-base-patch32", "CLIP ViT-B/32",
            "Same model at full precision: slightly more accurate, four times the download, a little slower.",
            Hf + "clip-vit-base-patch32", false, 351_685_709, 254_058_553),
        new("clip-vit-base-patch16-q8", "CLIP ViT-B/16 (int8)",
            "Finer image patches: better at small details and text in photos. About three times slower to index.",
            Hf + "clip-vit-base-patch16", true, 87_461_602, 64_504_507),
        new("clip-vit-base-patch16", "CLIP ViT-B/16",
            "ViT-B/16 at full precision.",
            Hf + "clip-vit-base-patch16", false, 345_060_583, 254_058_553),
        new("clip-vit-large-patch14-q8", "CLIP ViT-L/14 (int8)",
            "Best search quality. Roughly ten times slower to index than ViT-B/32; fine for a one-off overnight run.",
            Hf + "clip-vit-large-patch14", true, 307_001_500, 124_987_663),
        new("clip-vit-large-patch14", "CLIP ViT-L/14",
            "The large model at full precision. A 1.7 GB download and slow on CPU.",
            Hf + "clip-vit-large-patch14", false, 1_216_438_437, 494_947_485),
    ];

    public static ClipModelSpec Default => All[0];

    public static ClipModelSpec? Find(string? id) => All.FirstOrDefault(m => m.Id == id);
}

/// <summary>Where one model lives on disk and how to fetch it. Each model has its own folder under the app data dir.</summary>
public sealed class ClipModelFiles(ClipModelSpec spec)
{
    public const long VocabBytes = 862_328;
    public const long MergesBytes = 524_619;

    public static ClipModelFiles Default { get; } = new(ClipModelCatalog.Default);

    public ClipModelSpec Spec { get; } = spec;
    public string Id => Spec.Id;
    public string DisplayName => Spec.DisplayName;

    public string Dir => Path.Combine(AppPaths.AiModelDir, Spec.Id);
    public string VisionModel => Path.Combine(Dir, Spec.VisionFile);
    public string TextModel => Path.Combine(Dir, Spec.TextFile);
    public string Vocab => Path.Combine(Dir, "vocab.json");
    public string Merges => Path.Combine(Dir, "merges.txt");

    public (string Url, string LocalPath, long Bytes)[] Files =>
    [
        (Spec.Repo + "/resolve/main/onnx/" + Spec.VisionFile, VisionModel, Spec.VisionBytes),
        (Spec.Repo + "/resolve/main/onnx/" + Spec.TextFile, TextModel, Spec.TextBytes),
        (Spec.Repo + "/resolve/main/vocab.json", Vocab, VocabBytes),
        (Spec.Repo + "/resolve/main/merges.txt", Merges, MergesBytes),
    ];

    public long TotalBytes => Spec.TotalBytes;

    public bool IsInstalled => Files.All(f => File.Exists(f.LocalPath));

    /// <summary>Remove the downloaded files. Stored embeddings are kept, so re-downloading later needs no re-index.</summary>
    public void Delete()
    {
        if (Directory.Exists(Dir)) Directory.Delete(Dir, recursive: true);
    }

    /// <summary>
    /// The first release kept the default model's files directly in the ai_models folder. Move them into the
    /// per-model folder so an existing download is still found.
    /// </summary>
    public static void MigrateLegacyLayout()
    {
        var d = Default;
        var root = AppPaths.AiModelDir;
        if (!Directory.Exists(root) || d.IsInstalled) return;
        var legacy = new[] { d.Spec.VisionFile, d.Spec.TextFile, "vocab.json", "merges.txt" };
        if (!legacy.All(f => File.Exists(Path.Combine(root, f)))) return;
        Directory.CreateDirectory(d.Dir);
        foreach (var f in legacy) File.Move(Path.Combine(root, f), Path.Combine(d.Dir, f), overwrite: true);
    }

    public sealed record DownloadProgress(long BytesDone, long BytesTotal, string CurrentFile);

    /// <summary>Downloads any missing files. Data goes to a .part file first so a failure never leaves a truncated model.</summary>
    public async Task DownloadAsync(IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Dir);
        using var http = new HttpClient { Timeout = TimeSpan.FromHours(2) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("PhotoLibrary/1.0");
        var files = Files;
        long done = files.Where(f => File.Exists(f.LocalPath)).Sum(f => f.Bytes);

        foreach (var (url, local, expected) in files)
        {
            if (File.Exists(local)) continue;
            var name = Path.GetFileName(local);
            progress?.Report(new DownloadProgress(done, TotalBytes, name));
            var part = local + ".part";
            using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
                var buffer = new byte[1 << 16];
                long fileDone = 0;
                int n;
                while ((n = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                    fileDone += n;
                    progress?.Report(new DownloadProgress(done + fileDone, TotalBytes, name));
                }
            }
            File.Move(part, local, overwrite: true);
            done += expected;
        }
        progress?.Report(new DownloadProgress(TotalBytes, TotalBytes, ""));
    }
}
