using System.Text.RegularExpressions;
using PhotoLibrary.Core.Db;

namespace PhotoLibrary.Core.Pairs;

/// <summary>What travels with a paired photo.</summary>
[Flags]
public enum PairKind
{
    None = 0,
    /// <summary>A RAW file of the same shot (camera RAW + JPEG, or a Pixel's RAW-02.ORIGINAL.dng).</summary>
    Raw = 1,
    /// <summary>A short clip: an iPhone Live Photo's .MOV, or a Pixel motion photo's clip from Google Takeout.</summary>
    Live = 2,
    /// <summary>A second rendering of the shot, such as a Pixel portrait's unblurred ORIGINAL beside its COVER.</summary>
    Original = 4,
}

/// <summary>One shot stored as several files: the photo shown in the grid and the files that go everywhere with it.</summary>
public sealed record FilePair<T>(T Primary, List<T> Companions, PairKind Kind);

/// <summary>
/// Finds files that are one shot: same folder, same base name, and a combination that cameras and phones
/// actually write. Base names ignore the extension and the suffixes Pixel phones and Google Takeout add, so
/// <c>PXL_20240101_101112345.RAW-01.COVER.jpg</c> and <c>PXL_20240101_101112345.RAW-02.ORIGINAL.dng</c> are one
/// shot, as are <c>IMG_1234.HEIC</c> and <c>IMG_1234.MOV</c>. The rendered still is the primary; RAW files, short
/// clips and Pixel ORIGINAL renderings are its companions. Anything ambiguous is left unpaired.
/// </summary>
public static partial class FilePairs
{
    /// <summary>Longer clips with a matching name are ordinary videos, not a Live Photo's motion.</summary>
    public const long MaxLiveClipMs = 10_000;

    [GeneratedRegex(@"(?i:\.(?:COVER|ORIGINAL|MP))$|\.[A-Z]+-\d{2}$")]
    private static partial Regex SuffixRegex();

    [GeneratedRegex(@"(?i)\.COVER(\.|$)")]
    private static partial Regex CoverRegex();

    [GeneratedRegex(@"(?i)\.ORIGINAL(\.|$)")]
    private static partial Regex OriginalRegex();

    /// <summary>The shared part of a paired file's name: <c>IMG_1234</c>, or <c>PXL_20240101_101112345</c>.</summary>
    public static string Key(string fileName)
    {
        var s = Path.GetFileNameWithoutExtension(fileName);
        while (SuffixRegex().Match(s) is { Success: true } m && m.Index > 0) s = s[..m.Index];
        return s;
    }

    /// <summary>Everything after the key, e.g. <c>.RAW-02.ORIGINAL.dng</c> or <c>.MOV</c>.</summary>
    public static string Tail(string fileName) => fileName[Key(fileName).Length..];

    /// <summary>A companion's name beside a primary that is (or will be) called <paramref name="primaryName"/>.</summary>
    public static string CompanionName(string primaryName, string companionName) => Key(primaryName) + Tail(companionName);

    /// <summary>"Kept with its RAW file and Live Photo clip", for tooltips; empty when there's nothing paired.</summary>
    public static string Describe(PairKind kind)
    {
        var parts = new List<string>();
        if (kind.HasFlag(PairKind.Raw)) parts.Add("its RAW file");
        if (kind.HasFlag(PairKind.Live)) parts.Add("its Live Photo clip");
        if (kind.HasFlag(PairKind.Original)) parts.Add("its ORIGINAL version");
        return parts.Count == 0 ? "" : "Kept with " + string.Join(" and ", parts);
    }

    /// <summary>The tile badge: "RAW+JPG", "LIVE", "RAW+JPG · LIVE"…; null when there's nothing paired.</summary>
    public static string? Badge(PairKind kind, string extension)
    {
        if (kind == PairKind.None) return null;
        var ext = extension.TrimStart('.').ToUpperInvariant() is "JPEG" ? "JPG" : extension.TrimStart('.').ToUpperInvariant();
        var parts = new List<string>();
        if (kind.HasFlag(PairKind.Raw)) parts.Add("RAW+" + ext);
        if (kind.HasFlag(PairKind.Original)) parts.Add("+ORIGINAL");
        if (kind.HasFlag(PairKind.Live)) parts.Add("LIVE");
        return string.Join(" · ", parts);
    }

    public static bool IsCover(string fileName) => CoverRegex().IsMatch(fileName);
    public static bool IsOriginal(string fileName) => OriginalRegex().IsMatch(fileName);

    public static List<FilePair<MediaRecord>> Find(IEnumerable<MediaRecord> records) =>
        Find(records, r => r.Path, r => r.Kind, r => r.DurationMs);

    public static List<FilePair<T>> Find<T>(IEnumerable<T> items, Func<T, string> path, Func<T, MediaKind> kind, Func<T, long?> durationMs)
    {
        var result = new List<FilePair<T>>();
        var groups = items.GroupBy(i =>
        {
            var p = path(i);
            return (Dir: (Path.GetDirectoryName(p) ?? "").ToLowerInvariant(), Key: Key(Path.GetFileName(p)).ToLowerInvariant());
        });
        foreach (var g in groups)
        {
            var members = g.ToList();
            if (members.Count < 2) continue;
            var stills = members.Where(m => kind(m) == MediaKind.Image && !MediaKinds.IsRaw(path(m))).ToList();
            var raws = members.Where(m => kind(m) == MediaKind.Image && MediaKinds.IsRaw(path(m))).ToList();
            var clips = members.Where(m => kind(m) == MediaKind.Video && (durationMs(m) is not { } d || d <= MaxLiveClipMs)).ToList();

            T primary;
            var companions = new List<T>();
            var pairKind = PairKind.None;
            if (stills.Count == 1) primary = stills[0];
            else if (stills.Count > 1)
            {
                // Two stills of one shot only when a phone says which is which (Pixel portrait COVER + ORIGINAL).
                var covers = stills.Where(s => IsCover(Path.GetFileName(path(s)))).ToList();
                var rest = stills.Except(covers).ToList();
                if (covers.Count != 1 || !rest.All(s => IsOriginal(Path.GetFileName(path(s))))) continue;
                primary = covers[0];
                companions.AddRange(rest);
                pairKind |= PairKind.Original;
            }
            else continue; // a RAW and a clip with no still: nothing to show for the shot, leave them alone

            if (raws.Count > 0) { companions.AddRange(raws); pairKind |= PairKind.Raw; }
            if (clips.Count > 0) { companions.AddRange(clips); pairKind |= PairKind.Live; }
            if (companions.Count > 0) result.Add(new FilePair<T>(primary, companions, pairKind));
        }
        return result;
    }

    /// <summary>
    /// Works out the pairs in the library and stores them: each companion points at its primary, which is then
    /// left out of the grid, the date tree's counts and album counts. Records in <paramref name="library"/> are
    /// updated in place. Returns the number of pairs.
    /// </summary>
    public static int Refresh(LibraryDb db, IReadOnlyList<MediaRecord>? library = null)
    {
        library ??= db.GetLibrary();
        var pairs = Find(library.Where(r => r.InLibrary && !r.Hidden));
        var wantOf = new Dictionary<long, long>();
        var wantKind = new Dictionary<long, PairKind>();
        foreach (var p in pairs)
        {
            wantKind[p.Primary.Id] = p.Kind;
            foreach (var c in p.Companions) wantOf[c.Id] = p.Primary.Id;
        }
        var current = db.GetPairing();
        var changes = new List<(long Id, long? PairOf, PairKind Kind)>();
        foreach (var id in current.Keys.Union(wantOf.Keys).Union(wantKind.Keys))
        {
            long? of = wantOf.TryGetValue(id, out var o) ? o : null;
            var k = wantKind.GetValueOrDefault(id);
            if (!current.TryGetValue(id, out var now) || now.PairOf != of || now.Kind != k) changes.Add((id, of, k));
        }
        if (changes.Count > 0) db.SetPairing(changes);
        foreach (var r in library)
        {
            r.PairOf = wantOf.TryGetValue(r.Id, out var o) ? o : null;
            r.PairKind = wantKind.GetValueOrDefault(r.Id);
        }
        return pairs.Count;
    }
}
