using PhotoLibrary.AI.Clip;

namespace PhotoLibrary.AI.Index;

public sealed record IndexHit(long FileId, float Score);

/// <summary>
/// In-memory matrix of unit vectors for the whole library (e.g. 13k × 512 floats ≈ 26 MB). A query is one
/// pass of dot products, which is a few milliseconds, so there is no need for an approximate index.
/// </summary>
public sealed class EmbeddingIndex
{
    private readonly object _gate = new();
    private readonly Dictionary<long, int> _rowOf = new();
    private readonly List<long> _ids = [];
    private float[] _vectors;
    private int _count;

    public int Dimension { get; }

    public EmbeddingIndex(int dimension, int capacity = 1024)
    {
        Dimension = dimension;
        _vectors = new float[Math.Max(capacity, 16) * dimension];
    }

    public static EmbeddingIndex FromRows(int dimension, IReadOnlyList<(long FileId, float[] Vector)> rows)
    {
        var index = new EmbeddingIndex(dimension, rows.Count);
        foreach (var (id, v) in rows) index.Add(id, v);
        return index;
    }

    public int Count { get { lock (_gate) return _count; } }

    public bool Contains(long fileId) { lock (_gate) return _rowOf.ContainsKey(fileId); }

    public void Add(long fileId, float[] vector)
    {
        if (vector.Length != Dimension) throw new ArgumentException($"Expected {Dimension} dims, got {vector.Length}");
        lock (_gate)
        {
            if (_rowOf.TryGetValue(fileId, out var row))
            {
                vector.CopyTo(_vectors, row * Dimension);
                return;
            }
            if ((_count + 1) * Dimension > _vectors.Length) Array.Resize(ref _vectors, _vectors.Length * 2);
            vector.CopyTo(_vectors, _count * Dimension);
            _rowOf[fileId] = _count;
            _ids.Add(fileId);
            _count++;
        }
    }

    public float[]? Get(long fileId)
    {
        lock (_gate)
        {
            if (!_rowOf.TryGetValue(fileId, out var row)) return null;
            return _vectors.AsSpan(row * Dimension, Dimension).ToArray();
        }
    }

    public sealed record Pair(long A, long B, float Score);

    /// <summary>
    /// Every pair of vectors whose cosine similarity is at least minScore. O(n²) dot products, parallel over rows:
    /// about a second for 12k photos on a desktop CPU. The caller should keep minScore high (0.9+) or the list gets large.
    /// </summary>
    public List<Pair> AllPairs(float minScore, CancellationToken ct = default)
    {
        float[] vectors;
        long[] ids;
        int count;
        lock (_gate)
        {
            count = _count;
            vectors = _vectors;
            ids = _ids.ToArray();
        }
        var dim = Dimension;
        var bag = new System.Collections.Concurrent.ConcurrentBag<Pair>();
        Parallel.For(0, count, new ParallelOptions { CancellationToken = ct }, i =>
        {
            var a = vectors.AsSpan(i * dim, dim);
            for (var j = i + 1; j < count; j++)
            {
                var s = ClipModel.Dot(a, vectors.AsSpan(j * dim, dim));
                if (s >= minScore) bag.Add(new Pair(ids[i], ids[j], s));
            }
        });
        return bag.ToList();
    }

    /// <summary>Top hits by cosine similarity, highest first, dropping anything below minScore.</summary>
    public List<IndexHit> Search(ReadOnlySpan<float> query, int top, float minScore = float.MinValue, long excludeFileId = -1)
    {
        lock (_gate)
        {
            var hits = new List<IndexHit>();
            for (var row = 0; row < _count; row++)
            {
                var id = _ids[row];
                if (id == excludeFileId) continue;
                var score = ClipModel.Dot(query, _vectors.AsSpan(row * Dimension, Dimension));
                if (score >= minScore) hits.Add(new IndexHit(id, score));
            }
            hits.Sort((a, b) => b.Score.CompareTo(a.Score));
            if (hits.Count > top) hits.RemoveRange(top, hits.Count - top);
            return hits;
        }
    }
}
