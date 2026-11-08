using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime.Tensors;
using PhotoLibrary.AI.Clip;
using PhotoLibrary.AI.Db;
using PhotoLibrary.AI.Features;
using PhotoLibrary.AI.Index;
using PhotoLibrary.Core.Db;
using PhotoLibrary.Core.Hashing;
using PhotoLibrary.Core.Thumbnails;
using SkiaSharp;

namespace PhotoLibrary.AI;

/// <summary>A hit. For a video matched on one of its sampled frames, <paramref name="AtMs"/> is where that frame is.</summary>
public sealed record SearchResult(MediaRecord Record, float Score, int? AtMs = null);
public sealed record IndexProgress(int Done, int Total, string? Current);
public sealed record IndexResult(int Indexed, int Skipped, int Total);
public sealed record PoorPhoto(MediaRecord Record, string Reason, float Severity);
public sealed record DuplicateSet(DuplicateLevel Level, string Reason, List<MediaRecord> Records, long ReclaimableBytes);
public sealed record StorageReport(long DatabaseBytes, int ThumbnailCount, long ThumbnailBytes, long ModelBytes,
    List<IndexStats> Indexes, int QualityMeasured, int LibraryItems);

/// <summary>
/// Everything the AI tab needs, in one long-lived object: lazy model loading, the in-memory vector index,
/// incremental indexing of the library, text and image search, tag counts and event grouping.
/// Heavy methods are synchronous and meant to be called from Task.Run.
/// </summary>
public sealed partial class AiEngine(LibraryDb db, ThumbnailService thumbs) : IDisposable
{
    private readonly object _gate = new();
    private ClipModel? _model;
    private ZeroShotTagger? _tagger;
    private EmbeddingIndex? _index;
    private VideoFrameIndex? _frames;
    private readonly HashSet<long> _undecodable = [];

    public const int BatchSize = 16;

    /// <summary>The model in use. Switch with <see cref="SetModel"/>; each model keeps its own index rows.</summary>
    public ClipModelFiles Files { get; private set; } = ClipModelFiles.Default;
    public ClipModelSpec Spec => Files.Spec;
    public string ModelId => Files.Id;

    public bool ModelInstalled => Files.IsInstalled;
    public bool ModelLoaded { get { lock (_gate) return _model is not null; } }

    /// <summary>CPU or GPU. Change with <see cref="SetRuntime"/>; takes effect on the next load.</summary>
    public ClipRuntime Runtime { get; private set; } = ClipRuntime.Cpu;

    /// <summary>Where the loaded model is running, or null while nothing is loaded.</summary>
    public string? LoadedDevice { get { lock (_gate) return _model?.Device; } }
    public string? GpuFallbackReason { get { lock (_gate) return _model?.GpuFallbackReason; } }

    public void SetRuntime(ClipRuntime runtime)
    {
        lock (_gate)
        {
            if (runtime == Runtime) return;
            Runtime = runtime;
            _model?.Dispose();
            _model = null;
            _tagger = null;
        }
    }

    // ---- tunables, persisted by the app's settings

    /// <summary>A text-search hit must score this many standard deviations above the library mean. Lower is looser.</summary>
    public float SearchMinZ { get; set; } = 3f;
    public int SearchMaxResults { get; set; } = 400;
    /// <summary>Cosine floor for "photos like this one". Image-image scores are much higher than image-text ones.</summary>
    public float SimilarMinScore { get; set; } = 0.6f;
    public int SimilarMaxResults { get; set; } = 100;

    public void EnsureSchema()
    {
        ClipModelFiles.MigrateLegacyLayout();
        db.EnsureAiSchema();
    }

    /// <summary>Switch models. Unloads the current one; stored vectors for both models are kept.</summary>
    public void SetModel(ClipModelSpec spec)
    {
        lock (_gate)
        {
            if (spec.Id == Files.Id) return;
            Files = new ClipModelFiles(spec);
            _model?.Dispose();
            _model = null;
            _tagger = null;
            _index = null;
            _frames = null;
            _undecodable.Clear();
        }
    }

    public Task DownloadModelAsync(IProgress<ClipModelFiles.DownloadProgress>? progress, CancellationToken ct) =>
        Files.DownloadAsync(progress, ct);

    /// <summary>Loads the ONNX sessions (a few seconds) and the stored vectors on first use.</summary>
    private (ClipModel Model, ZeroShotTagger Tagger, EmbeddingIndex Index) Load()
    {
        lock (_gate)
        {
            if (_model is null)
            {
                if (!ModelInstalled) throw new InvalidOperationException($"{Files.DisplayName} has not been downloaded yet.");
                _model = new ClipModel(Files, Runtime);
                _tagger = new ZeroShotTagger(_model);
            }
            _index ??= EmbeddingIndex.FromRows(_model.Dimension, db.LoadEmbeddings(ModelId));
            _frames ??= VideoFrameIndex.FromRows(_model.Dimension, db.LoadVideoFrames(ModelId));
            return (_model, _tagger!, _index);
        }
    }

    /// <summary>Forget cached vectors so the next call re-reads them (after a rescan may have changed in_library flags).</summary>
    public void InvalidateIndex() { lock (_gate) { _index = null; _frames = null; } }

    public (int Embedded, int Total) IndexStatus() => IndexStatus(ModelId);

    public (int Embedded, int Total) IndexStatus(string modelId) => (db.CountEmbedded(modelId), (int)db.Count("in_library=1"));

    /// <summary>Embed and tag every library item that has no vector yet. Safe to re-run; it only does new work.</summary>
    public async Task<IndexResult> IndexAsync(IProgress<IndexProgress>? progress, CancellationToken ct)
    {
        var (model, tagger, index) = Load();
        db.PruneOrphans();
        var have = db.EmbeddedFileIds(ModelId);
        var haveFrames = db.VideoFrameFileIds(ModelId);
        // A video is done once its frames are stored; one indexed from a single thumbnail (older versions) is redone.
        var pending = db.GetLibrary().Where(r => !_undecodable.Contains(r.Id)
            && (r.Kind == Core.MediaKind.Video ? !haveFrames.Contains(r.Id) : !have.Contains(r.Id))).ToList();
        var videos = pending.Where(r => r.Kind == Core.MediaKind.Video).ToList();
        pending = pending.Where(r => r.Kind != Core.MediaKind.Video).ToList();
        var total = pending.Count + videos.Count;
        var done = 0;
        var indexed = 0;
        var skipped = 0;
        progress?.Report(new IndexProgress(0, total, null));

        foreach (var batch in pending.Chunk(BatchSize))
        {
            ct.ThrowIfCancellationRequested();
            var paths = await Task.WhenAll(batch.Select(r => thumbs.GetAsync(r, ct)));
            var usable = new List<(MediaRecord Record, SKBitmap Bitmap)>();
            for (var i = 0; i < batch.Length; i++)
            {
                var bmp = paths[i] is { } p ? SKBitmap.Decode(p) : null;
                if (bmp is null) { lock (_gate) _undecodable.Add(batch[i].Id); skipped++; continue; }
                usable.Add((batch[i], bmp));
            }
            if (usable.Count > 0)
            {
                try
                {
                    var tensor = new DenseTensor<float>([usable.Count, 3, ClipModel.ImageSize, ClipModel.ImageSize]);
                    Parallel.For(0, usable.Count, i => ClipModel.Preprocess(usable[i].Bitmap, tensor, i));
                    var vectors = model.EmbedPreprocessed(tensor, usable.Count);
                    var rows = new List<(long, float[], IReadOnlyList<(string, float)>)>(usable.Count);
                    for (var i = 0; i < usable.Count; i++) rows.Add((usable[i].Record.Id, vectors[i], tagger.Tag(vectors[i])));
                    db.StoreBatch(ModelId, rows);
                    for (var i = 0; i < usable.Count; i++) index.Add(usable[i].Record.Id, vectors[i]);
                    indexed += usable.Count;
                }
                finally
                {
                    foreach (var u in usable) u.Bitmap.Dispose();
                }
            }
            done += batch.Length;
            progress?.Report(new IndexProgress(done, total, batch[^1].FileName));
        }

        foreach (var video in videos)
        {
            ct.ThrowIfCancellationRequested();
            if (await Task.Run(() => IndexVideo(model, tagger, index, video, ct), ct)) indexed++;
            else { lock (_gate) _undecodable.Add(video.Id); skipped++; }
            done++;
            progress?.Report(new IndexProgress(done, total, video.FileName));
        }
        return new IndexResult(indexed, skipped, total);
    }

    /// <summary>Seconds between sampled video frames (widened on long videos to stay under <see cref="VideoMaxFrames"/>).</summary>
    public double VideoFrameInterval { get; set; } = 10;
    public int VideoMaxFrames { get; set; } = 60;

    /// <summary>
    /// Samples frames through one video and embeds each. The frames are stored for search; their normalised mean
    /// becomes the file's own vector (and tags), which similarity, duplicates and events use. False when the
    /// video can't be decoded (no ffmpeg, or a broken file).
    /// </summary>
    private bool IndexVideo(ClipModel model, ZeroShotTagger tagger, EmbeddingIndex index, MediaRecord video, CancellationToken ct)
    {
        var sampled = Core.Video.VideoFrames.Sample(video.Path, VideoFrameInterval, VideoMaxFrames, video.DurationMs, ClipModel.ImageSize * 2, ct);
        try
        {
            if (sampled.Count == 0) return false;
            var frames = new List<(int Ms, float[] Vector)>(sampled.Count);
            foreach (var chunk in sampled.Chunk(BatchSize))
            {
                var tensor = new DenseTensor<float>([chunk.Length, 3, ClipModel.ImageSize, ClipModel.ImageSize]);
                Parallel.For(0, chunk.Length, i => ClipModel.Preprocess(chunk[i].Bitmap, tensor, i));
                var vectors = model.EmbedPreprocessed(tensor, chunk.Length);
                for (var i = 0; i < chunk.Length; i++) frames.Add((chunk[i].Ms, vectors[i]));
            }
            var mean = VideoFrameIndex.Mean(frames.Select(f => f.Vector).ToList());
            db.StoreBatch(ModelId, [(video.Id, mean, tagger.Tag(mean))]);
            db.StoreVideoFrames(ModelId, video.Id, frames);
            index.Add(video.Id, mean);
            lock (_gate) _frames?.Replace(video.Id, frames);
            return true;
        }
        finally
        {
            foreach (var f in sampled) f.Dispose();
        }
    }

    /// <summary>Raises each video's score to its best-matching frame, and records where that frame is.</summary>
    private Dictionary<long, FrameHit> BestFrames(ReadOnlySpan<float> query, float minScore = float.MinValue, long excludeFileId = -1)
    {
        VideoFrameIndex? frames;
        lock (_gate) frames = _frames;
        return frames is null || frames.Count == 0 ? [] : frames.BestPerFile(query, minScore, excludeFileId);
    }

    /// <summary>Drop every video's vectors for the current model, so the next index samples them at the current interval.</summary>
    public int ForgetVideoIndex()
    {
        var n = db.ForgetVideoIndex(ModelId);
        lock (_gate) { _index = null; _frames = null; }
        return n;
    }

    [GeneratedRegex(@"\b(19[0-9]{2}|20[0-9]{2})\b")]
    private static partial Regex YearPattern();

    /// <summary>
    /// Natural-language search. A four-digit year in the query restricts results to that year.
    /// CLIP image-text scores sit in a narrow, query-dependent band (most of a library scores 0.19-0.22 for
    /// any query), so an absolute cutoff is meaningless. Instead a photo is a hit when it scores at least
    /// <paramref name="minZ"/> standard deviations above the library's mean score for this query.
    /// </summary>
    public List<SearchResult> SearchText(string query, int? top = null, float? minZ = null)
    {
        var (model, _, index) = Load();
        var limit = top ?? SearchMaxResults;
        var z = minZ ?? SearchMinZ;
        int? year = null;
        var m = YearPattern().Match(query);
        if (m.Success)
        {
            year = int.Parse(m.Value);
            query = YearPattern().Replace(query, " ").Trim();
            if (query.Length == 0) query = "a photo";
        }
        var q = model.EmbedText(query);
        var hits = index.Search(q, index.Count);
        if (hits.Count == 0) return [];
        var mean = hits.Average(h => h.Score);
        var std = MathF.Sqrt(hits.Sum(h => (h.Score - mean) * (h.Score - mean)) / hits.Count);
        var cutoff = std < 1e-6f ? float.MinValue : mean + z * std;
        // The statistics come from one score per file; a video then counts as its best moment.
        var frames = BestFrames(q, cutoff);
        var scoreOf = hits.ToDictionary(h => h.FileId, h => h.Score);
        var atMs = new Dictionary<long, int>();
        foreach (var (fileId, f) in frames)
            if (!scoreOf.TryGetValue(fileId, out var s) || f.Score > s) { scoreOf[fileId] = f.Score; atMs[fileId] = f.Ms; }
        var keep = scoreOf.Where(kv => kv.Value >= cutoff).OrderByDescending(kv => kv.Value).Select(kv => kv.Key).ToList();
        var records = db.GetByIds(keep);
        return records.Where(r => r.InLibrary && (year is null || r.Taken?.Year == year))
            .Take(limit).Select(r => new SearchResult(r, scoreOf[r.Id], atMs.TryGetValue(r.Id, out var ms) ? ms : null)).ToList();
    }

    /// <summary>Photos that look like the given one. Uses its stored vector, or embeds it now if it isn't indexed yet.</summary>
    public async Task<List<SearchResult>> FindSimilarAsync(MediaRecord record, int? top = null, float? minScore = null, CancellationToken ct = default)
    {
        var (model, _, index) = Load();
        var q = index.Get(record.Id);
        if (q is null)
        {
            var path = await thumbs.GetAsync(record, ct) ?? throw new InvalidOperationException("This file can't be decoded.");
            using var bmp = SKBitmap.Decode(path) ?? throw new InvalidOperationException("This file can't be decoded.");
            q = model.EmbedImage(bmp);
        }
        var min = minScore ?? SimilarMinScore;
        var limit = top ?? SimilarMaxResults;
        var hits = index.Search(q, limit, min, excludeFileId: record.Id);
        var scoreOf = hits.ToDictionary(h => h.FileId, h => h.Score);
        var atMs = new Dictionary<long, int>();
        foreach (var (fileId, f) in BestFrames(q, min, record.Id))
            if (!scoreOf.TryGetValue(fileId, out var s) || f.Score > s) { scoreOf[fileId] = f.Score; atMs[fileId] = f.Ms; }
        var keep = scoreOf.OrderByDescending(kv => kv.Value).Take(limit).Select(kv => kv.Key).ToList();
        return db.GetByIds(keep).Where(r => r.InLibrary)
            .Select(r => new SearchResult(r, scoreOf[r.Id], atMs.TryGetValue(r.Id, out var ms) ? ms : null)).ToList();
    }

    public List<TagCount> TagCounts(int top = 40) => db.TagCounts(ModelId, top);

    public List<(string Tag, float Score)> TagsFor(long fileId) => db.TagsFor(ModelId, fileId);

    /// <summary>Re-derive events for the whole library and store them, replacing the previous set.</summary>
    public List<PhotoEvent> BuildEvents()
    {
        var index = ModelInstalled ? Load().Index : null;
        var grouper = new EventGrouper();
        var groups = grouper.Group(db.GetLibrary(), id => index?.Get(id), db.TopTags(ModelId));
        db.ReplaceEvents(groups.Select(g => (g.Name, g.Start, g.End, g.FileIds)).ToList());
        return db.GetEvents();
    }

    public List<PhotoEvent> Events() => db.GetEvents();

    // ---- poor photos

    public (int Measured, int Total) QualityStatus() => (db.CountQuality(QualityAnalyzer.Version), (int)db.Count("in_library=1 AND kind='image'"));

    /// <summary>Measure brightness, contrast and sharpness for every image that hasn't been measured. No model needed.</summary>
    public async Task<IndexResult> AnalyseQualityAsync(IProgress<IndexProgress>? progress, CancellationToken ct)
    {
        db.PruneOrphans();
        var have = db.QualityDone(QualityAnalyzer.Version);
        var pending = db.GetLibrary().Where(r => r.IsImage && !have.Contains(r.Id) && !_undecodable.Contains(r.Id)).ToList();
        var total = pending.Count;
        int done = 0, measured = 0, skipped = 0;
        progress?.Report(new IndexProgress(0, total, null));
        foreach (var batch in pending.Chunk(64))
        {
            ct.ThrowIfCancellationRequested();
            var paths = await Task.WhenAll(batch.Select(r => thumbs.GetAsync(r, ct)));
            var rows = new (long, float, float, float)?[batch.Length];
            Parallel.For(0, batch.Length, i =>
            {
                using var bmp = paths[i] is { } p ? SKBitmap.Decode(p) : null;
                if (bmp is null) return;
                var q = QualityAnalyzer.Measure(bmp);
                rows[i] = (batch[i].Id, q.Brightness, q.Contrast, q.Sharpness);
            });
            var ok = rows.Where(r => r is not null).Select(r => r!.Value).ToList();
            for (var i = 0; i < batch.Length; i++) if (rows[i] is null) { skipped++; lock (_gate) _undecodable.Add(batch[i].Id); }
            if (ok.Count > 0) db.StoreQuality(QualityAnalyzer.Version, ok);
            measured += ok.Count;
            done += batch.Length;
            progress?.Report(new IndexProgress(done, total, batch[^1].FileName));
        }
        return new IndexResult(measured, skipped, total);
    }

    /// <summary>Photos judged poor at this strictness (0..1), worst first.</summary>
    public List<PoorPhoto> PoorPhotos(float strictness)
    {
        var rows = db.LoadQuality(QualityAnalyzer.Version);
        var verdicts = new List<(long Id, QualityVerdict V)>();
        foreach (var (id, b, c, s) in rows)
            if (QualityAnalyzer.Judge(new QualityMetrics(b, c, s), strictness) is { } v) verdicts.Add((id, v));
        var ordered = verdicts.OrderByDescending(v => v.V.Severity).ToList();
        var records = db.GetByIds(ordered.Select(v => v.Id).ToList()).ToDictionary(r => r.Id);
        return ordered.Where(v => records.ContainsKey(v.Id)).Select(v => new PoorPhoto(records[v.Id], v.V.Reason, v.V.Severity)).ToList();
    }

    // ---- duplicates

    /// <summary>
    /// Group duplicate images at the given level. Fingerprints are computed from thumbnails for any image that
    /// lacks one (a one-off), and the CLIP index is used when the model is installed and the library indexed.
    /// </summary>
    public async Task<List<DuplicateSet>> FindDuplicatesAsync(DuplicateLevel level, IProgress<IndexProgress>? progress, CancellationToken ct, bool includeRawJpegPairs = false)
    {
        var images = db.GetLibrary().Where(r => r.IsImage).ToList();
        if (level >= DuplicateLevel.SamePicture)
        {
            var todo = images.Where(r => !r.DHashDone && !_undecodable.Contains(r.Id)).ToList();
            var done = 0;
            progress?.Report(new IndexProgress(0, todo.Count, null));
            foreach (var batch in todo.Chunk(64))
            {
                ct.ThrowIfCancellationRequested();
                var paths = await Task.WhenAll(batch.Select(r => thumbs.GetAsync(r, ct)));
                var hashes = new (string Path, ulong? Hash)[batch.Length];
                Parallel.For(0, batch.Length, i =>
                {
                    using var bmp = paths[i] is { } p ? SKBitmap.Decode(p) : null;
                    hashes[i] = (batch[i].Path, bmp is null ? null : DHash.Compute(bmp));
                    batch[i].DHash = hashes[i].Hash;
                    batch[i].DHashDone = true;
                });
                db.UpdateDHashMany(hashes);
                done += batch.Length;
                progress?.Report(new IndexProgress(done, todo.Count, batch[^1].FileName));
            }
        }
        EmbeddingIndex? index = null;
        if (level >= DuplicateLevel.SamePicture && ModelInstalled)
        {
            try { index = Load().Index; } catch (InvalidOperationException) { }
        }
        var finder = new DuplicateFinder { IncludeRawJpegPairs = includeRawJpegPairs };
        var groups = await Task.Run(() => finder.Find(images, level, index, ct), ct);
        var byId = images.ToDictionary(r => r.Id);
        return groups.Select(g => new DuplicateSet(g.Level, g.Reason, g.FileIds.Select(id => byId[id]).ToList(), g.ReclaimableBytes)).ToList();
    }

    /// <summary>Whether looser duplicate levels can use the model: it is installed and the library is indexed.</summary>
    public bool DuplicatesCanUseModel()
    {
        if (!ModelInstalled) return false;
        var (embedded, total) = IndexStatus();
        return total > 0 && embedded >= total * 0.9;
    }

    // ---- storage

    public StorageReport Storage()
    {
        long dbBytes = 0;
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            if (File.Exists(db.Path + suffix)) dbBytes += new FileInfo(db.Path + suffix).Length;
        int thumbCount = 0;
        long thumbBytes = 0;
        if (Directory.Exists(thumbs.CacheDir))
            foreach (var f in new DirectoryInfo(thumbs.CacheDir).EnumerateFiles("*.jpg", SearchOption.AllDirectories)) { thumbCount++; thumbBytes += f.Length; }
        long modelBytes = 0;
        if (Directory.Exists(Core.AppPaths.AiModelDir))
            foreach (var f in new DirectoryInfo(Core.AppPaths.AiModelDir).EnumerateFiles("*", SearchOption.AllDirectories)) modelBytes += f.Length;
        return new StorageReport(dbBytes, thumbCount, thumbBytes, modelBytes, db.IndexStats(), db.CountQuality(QualityAnalyzer.Version), (int)db.Count("in_library=1"));
    }

    /// <summary>Drops every quality measurement; the Quality tab measures again from scratch.</summary>
    public void ClearQuality() => db.Exec("DELETE FROM ai_quality");

    public void DeleteIndex(string modelId)
    {
        db.DeleteIndex(modelId);
        if (modelId == ModelId) InvalidateIndex();
    }

    /// <summary>
    /// One-off after the RAW decoder changed (full-size CR2 previews in v2; DNG orientation, sensor size and
    /// sensor-data previews in v3): throw away thumbnails, fingerprints and AI rows derived from the old
    /// previews so they are rebuilt, and have the next scan re-read the files' size and orientation. Also
    /// covers Pixel ".COVER" motion-photo clips, which were classed as images until v3. Keyed by a setting so
    /// it runs once.
    /// </summary>
    public int RebuildRawDerivedData()
    {
        const string key = "raw_preview_v3";
        if (db.GetSetting(key) == "1") return 0;
        var rows = db.Query("1=1").Where(r => Core.MediaKinds.IsRaw(r.Path)
            || r.Path.EndsWith(".cover", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var r in rows) thumbs.Forget(r.Sha256);
        db.ForgetAnalysis(rows.Select(r => r.Id));
        db.ResetDHash(rows.Select(r => r.Id));
        db.ResetMetadata(rows.Select(r => r.Id));
        InvalidateIndex();
        db.SetSetting(key, "1");
        return rows.Count;
    }

    /// <summary>Files that failed to decode get another go on the next index, fingerprint or quality pass.</summary>
    public void ForgetUndecodable()
    {
        lock (_gate) _undecodable.Clear();
    }

    /// <summary>Drop cached vectors so a removed file's rows are no longer searched.</summary>
    public void Forget(IEnumerable<long> fileIds)
    {
        lock (_gate)
        {
            _frames = null;
            if (_index is null) return;
            _index = null; // simplest correct thing: reload from the database on next use
        }
    }

    public List<MediaRecord> EventMembers(long eventId) =>
        db.GetByIds(db.EventMemberIds(eventId)).Where(r => r.InLibrary).OrderBy(r => r.Taken).ToList();

    public void Dispose()
    {
        lock (_gate)
        {
            _model?.Dispose();
            _model = null;
        }
    }
}
