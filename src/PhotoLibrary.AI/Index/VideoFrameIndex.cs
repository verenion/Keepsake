namespace PhotoLibrary.AI.Index;

/// <summary>Where in a video the best-matching frame is, and how well it matched.</summary>
public readonly record struct FrameHit(float Score, int Ms);

/// <summary>
/// Vectors for frames sampled through videos, many per file. Searched brute force like the main index, but
/// reduced to the best frame per video, so a clip matches a query when any moment of it does.
/// </summary>
public sealed class VideoFrameIndex(int dimension)
{
    private readonly object _gate = new();
    private float[] _vectors = new float[dimension * 256];
    private readonly List<(long FileId, int Ms)> _frames = [];

    public int Dimension { get; } = dimension;
    public int Count { get { lock (_gate) return _frames.Count; } }

    public static VideoFrameIndex FromRows(int dimension, IEnumerable<(long FileId, int Ms, float[] Vector)> rows)
    {
        var index = new VideoFrameIndex(dimension);
        foreach (var (f, ms, v) in rows) index.Add(f, ms, v);
        return index;
    }

    public void Add(long fileId, int ms, float[] vector)
    {
        if (vector.Length != Dimension) throw new ArgumentException($"Expected {Dimension} dims, got {vector.Length}");
        lock (_gate)
        {
            if ((_frames.Count + 1) * Dimension > _vectors.Length) Array.Resize(ref _vectors, _vectors.Length * 2);
            vector.CopyTo(_vectors, _frames.Count * Dimension);
            _frames.Add((fileId, ms));
        }
    }

    /// <summary>Replaces every frame of one video (re-indexing it).</summary>
    public void Replace(long fileId, IReadOnlyList<(int Ms, float[] Vector)> frames)
    {
        lock (_gate)
        {
            var keep = new List<int>();
            for (var i = 0; i < _frames.Count; i++) if (_frames[i].FileId != fileId) keep.Add(i);
            if (keep.Count != _frames.Count)
            {
                var vectors = new float[Math.Max(_vectors.Length, Dimension * 256)];
                var kept = new List<(long, int)>(keep.Count);
                for (var k = 0; k < keep.Count; k++)
                {
                    Array.Copy(_vectors, keep[k] * Dimension, vectors, k * Dimension, Dimension);
                    kept.Add(_frames[keep[k]]);
                }
                _vectors = vectors;
                _frames.Clear();
                _frames.AddRange(kept);
            }
        }
        foreach (var (ms, v) in frames) Add(fileId, ms, v);
    }

    /// <summary>Best frame per video for a (normalised) query vector, keeping videos that reach <paramref name="minScore"/>.</summary>
    public Dictionary<long, FrameHit> BestPerFile(ReadOnlySpan<float> query, float minScore = float.MinValue, long excludeFileId = -1)
    {
        var best = new Dictionary<long, FrameHit>();
        lock (_gate)
        {
            for (var i = 0; i < _frames.Count; i++)
            {
                var (fileId, ms) = _frames[i];
                if (fileId == excludeFileId) continue;
                var score = Dot(query, _vectors.AsSpan(i * Dimension, Dimension));
                if (score < minScore) continue;
                if (!best.TryGetValue(fileId, out var b) || score > b.Score) best[fileId] = new FrameHit(score, ms);
            }
        }
        return best;
    }

    private static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var sum = 0f;
        for (var i = 0; i < a.Length; i++) sum += a[i] * b[i];
        return sum;
    }

    /// <summary>The mean of a video's frame vectors, normalised: the file's single vector for similarity, events and tags.</summary>
    public static float[] Mean(IReadOnlyList<float[]> vectors)
    {
        var dim = vectors[0].Length;
        var mean = new float[dim];
        foreach (var v in vectors) for (var d = 0; d < dim; d++) mean[d] += v[d];
        var norm = MathF.Sqrt(mean.Sum(x => x * x));
        if (norm > 0) for (var d = 0; d < dim; d++) mean[d] /= norm;
        return mean;
    }
}
