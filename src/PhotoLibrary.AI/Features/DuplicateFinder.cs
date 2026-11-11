using PhotoLibrary.AI.Index;
using PhotoLibrary.Core.Db;
using PhotoLibrary.Core.Hashing;

namespace PhotoLibrary.AI.Features;

/// <summary>How far from "byte-identical" the finder is allowed to stray. Each level includes the ones before it.</summary>
public enum DuplicateLevel
{
    /// <summary>Same SHA-256: the files are byte for byte identical. Deleting all but one loses nothing.</summary>
    Identical = 0,
    /// <summary>Same picture saved again: re-encoded, resized or lightly edited. Fingerprints match closely.</summary>
    SamePicture = 1,
    /// <summary>Near-identical shots: bursts, a small crop or re-frame. The model sees them as the same image.</summary>
    NearIdentical = 2,
    /// <summary>Similar: same scene and subject, but genuinely different photos. Review carefully.</summary>
    Similar = 3,
}

public sealed record DuplicateGroup(DuplicateLevel Level, string Reason, List<long> FileIds, long ReclaimableBytes);

/// <summary>
/// Groups photos that are duplicates of each other, from exact copies out to "same scene". Exact copies come
/// from the stored SHA-256; the looser levels combine the 64-bit difference hash with CLIP cosine similarity.
/// Pairs above the threshold are merged with union-find, and a group's level is its weakest link.
/// </summary>
public sealed class DuplicateFinder
{
    public int SamePictureHashDistance { get; init; } = 3;
    public float SamePictureMinCosine { get; init; } = 0.90f; // guards against plain/dark images that all hash alike
    public int NearHashDistance { get; init; } = 8;
    public float NearMinCosine { get; init; } = 0.975f;
    public float SimilarMinCosine { get; init; } = 0.93f;

    /// <summary>
    /// Cameras that shoot RAW + JPEG write two files of the same shot on purpose. Off by default: RAW files are
    /// then only compared with other RAW files, so a JPEG is never offered for removal because of its RAW twin
    /// (or, through a chain of matches, because of any RAW).
    /// </summary>
    public bool IncludeRawJpegPairs { get; init; }

    public List<DuplicateGroup> Find(IReadOnlyList<MediaRecord> records, DuplicateLevel level, EmbeddingIndex? index, CancellationToken ct = default)
    {
        var byId = records.ToDictionary(r => r.Id);
        var uf = new UnionFind(records.Select(r => r.Id));
        var edgeLevel = new Dictionary<(long, long), (DuplicateLevel Level, string Reason)>();

        void Link(long a, long b, DuplicateLevel l, string reason)
        {
            if (!IncludeRawJpegPairs && Core.MediaKinds.IsRaw(byId[a].Path) != Core.MediaKinds.IsRaw(byId[b].Path)) return;
            // Files of one shot (a photo and its RAW, or a Pixel portrait's two renderings) are meant to be together.
            if (SameShot(byId[a], byId[b])) return;
            var key = a < b ? (a, b) : (b, a);
            if (edgeLevel.TryGetValue(key, out var existing) && existing.Level <= l) return;
            edgeLevel[key] = (l, reason);
            uf.Union(a, b);
        }

        // Level 0: identical bytes.
        foreach (var g in records.GroupBy(r => r.Sha256).Where(g => g.Count() > 1))
        {
            var first = g.First().Id;
            foreach (var r in g.Skip(1)) Link(first, r.Id, DuplicateLevel.Identical, "identical files");
        }

        if (level >= DuplicateLevel.SamePicture)
        {
            // Fingerprint pairs: cheap enough to do exhaustively (popcount of a xor).
            var hashed = records.Where(r => r.DHash is not null).ToArray();
            var cos = index is null ? null : new Func<long, long, float?>((a, b) =>
            {
                var va = index.Get(a);
                var vb = index.Get(b);
                return va is null || vb is null ? null : Clip.ClipModel.Dot(va, vb);
            });
            var maxDist = level >= DuplicateLevel.NearIdentical ? NearHashDistance : SamePictureHashDistance;
            Parallel.For(0, hashed.Length, new ParallelOptions { CancellationToken = ct }, i =>
            {
                var a = hashed[i];
                var ha = a.DHash!.Value;
                for (var j = i + 1; j < hashed.Length; j++)
                {
                    var b = hashed[j];
                    if (a.Sha256 == b.Sha256) continue;
                    var d = DHash.Distance(ha, b.DHash!.Value);
                    if (d > maxDist) continue;
                    // Plain, dark or blurry images all fingerprint alike: demand the model (or the dates) agree.
                    var c = cos?.Invoke(a.Id, b.Id);
                    var agree = c is { } s ? s >= SamePictureMinCosine : DatesAgree(a, b);
                    if (!agree) continue;
                    var l = d <= SamePictureHashDistance ? DuplicateLevel.SamePicture : DuplicateLevel.NearIdentical;
                    lock (edgeLevel) Link(a.Id, b.Id, l, d <= SamePictureHashDistance
                        ? $"same picture (fingerprint distance {d}/64)"
                        : $"near-identical (fingerprint distance {d}/64)");
                }
            });
        }

        if (level >= DuplicateLevel.NearIdentical && index is not null)
        {
            var min = level >= DuplicateLevel.Similar ? SimilarMinCosine : NearMinCosine;
            foreach (var p in index.AllPairs(min, ct))
            {
                if (!byId.ContainsKey(p.A) || !byId.ContainsKey(p.B)) continue;
                if (byId[p.A].Sha256 == byId[p.B].Sha256) continue;
                var l = p.Score >= NearMinCosine ? DuplicateLevel.NearIdentical : DuplicateLevel.Similar;
                Link(p.A, p.B, l, l == DuplicateLevel.NearIdentical ? $"near-identical (similarity {p.Score:0.000})" : $"similar (similarity {p.Score:0.000})");
            }
        }

        // Assemble groups. Union-find alone chains A~B~C~D into one sprawling group where A and D may look
        // nothing alike, so each connected component is re-cut into stars: the best-connected photo seeds a
        // group and takes every member it has a direct match with; what's left seeds the next star.
        var components = new Dictionary<long, List<long>>();
        foreach (var r in records)
        {
            var root = uf.Find(r.Id);
            if (!components.TryGetValue(root, out var list)) components[root] = list = [];
            list.Add(r.Id);
        }
        var degree = new Dictionary<long, int>();
        foreach (var (a, b) in edgeLevel.Keys)
        {
            degree[a] = degree.GetValueOrDefault(a) + 1;
            degree[b] = degree.GetValueOrDefault(b) + 1;
        }
        (DuplicateLevel Level, string Reason)? Edge(long a, long b) =>
            edgeLevel.TryGetValue(a < b ? (a, b) : (b, a), out var e) ? e : null;

        var result = new List<DuplicateGroup>();
        foreach (var ids in components.Values)
        {
            if (ids.Count < 2) continue;
            var pending = ids.OrderByDescending(id => degree.GetValueOrDefault(id)).ThenByDescending(id => Quality(byId[id])).ToList();
            while (pending.Count > 1)
            {
                var seed = pending[0];
                var members = new List<long> { seed };
                var weakest = (Level: DuplicateLevel.Identical, Reason: "identical files");
                foreach (var id in pending.Skip(1))
                {
                    if (Edge(seed, id) is not { } e || e.Level > level) continue;
                    members.Add(id);
                    if (e.Level > weakest.Level) weakest = e;
                }
                pending.RemoveAll(members.Contains);
                if (members.Count < 2) continue;
                var ordered = members.Select(id => byId[id]).OrderByDescending(Quality).ThenBy(r => r.Added).Select(r => r.Id).ToList();
                var reclaim = ordered.Skip(1).Sum(id => byId[id].Size);
                result.Add(new DuplicateGroup(weakest.Level, weakest.Reason, ordered, reclaim));
            }
        }
        return result.OrderBy(g => g.Level).ThenByDescending(g => g.ReclaimableBytes).ToList();
    }

    /// <summary>
    /// The copy worth keeping sorts first: a RAW file over any rendered copy of it, then most pixels, then the
    /// biggest file, then the earliest added. A RAW is never the one offered for removal in favour of its JPEG.
    /// </summary>
    public static (bool IsRaw, long Pixels, long Size) Quality(MediaRecord r) =>
        (Core.MediaKinds.IsRaw(r.Path), (long)(r.Width ?? 0) * (r.Height ?? 0), r.Size);

    private static bool SameShot(MediaRecord a, MediaRecord b) =>
        a.PairOf == b.Id || b.PairOf == a.Id || (a.PairOf is { } pa && pa == b.PairOf);

    private static bool DatesAgree(MediaRecord a, MediaRecord b) =>
        a.Taken is { } ta && b.Taken is { } tb && Math.Abs((ta - tb).TotalHours) <= 26;

    private sealed class UnionFind
    {
        private readonly Dictionary<long, long> _parent;

        public UnionFind(IEnumerable<long> ids)
        {
            _parent = ids.ToDictionary(id => id, id => id);
        }

        public long Find(long x)
        {
            while (_parent[x] != x)
            {
                _parent[x] = _parent[_parent[x]];
                x = _parent[x];
            }
            return x;
        }

        public void Union(long a, long b)
        {
            lock (_parent)
            {
                var ra = Find(a);
                var rb = Find(b);
                if (ra != rb) _parent[Math.Max(ra, rb)] = Math.Min(ra, rb);
            }
        }
    }
}
