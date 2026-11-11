using PhotoLibrary.Core.Db;

namespace PhotoLibrary.Core.Layout;

/// <summary>Which copy in a group of duplicates to keep.</summary>
public static class KeepChoice
{
    /// <summary>
    /// The index of the copy to keep. <paramref name="bestFirst"/> is ordered best quality first.
    /// A copy that's in an album (a folder album, or listed in a virtual one) wins, because removing it would
    /// take it out of that album; for byte-identical files that costs nothing. For looser matches an album copy
    /// only wins when it has at least as many pixels as the best copy, and a RAW is never given up for a non-RAW
    /// copy, so quality is never silently lost. Next comes a copy that has paired files (its RAW or Live Photo
    /// clip), so keeping it keeps the whole shot together.
    /// </summary>
    public static int Choose(IReadOnlyList<MediaRecord> bestFirst, bool identical, Func<MediaRecord, bool> inAlbum, Func<MediaRecord, bool>? paired = null)
    {
        if (bestFirst.Count == 0) return -1;
        var bestPixels = Pixels(bestFirst[0]);
        var bestIsRaw = MediaKinds.IsRaw(bestFirst[0].Path);
        var (pick, score) = (0, 0);
        for (var i = 0; i < bestFirst.Count; i++)
        {
            var r = bestFirst[i];
            if (!identical && !(Pixels(r) >= bestPixels && (!bestIsRaw || MediaKinds.IsRaw(r.Path)))) continue;
            var s = (inAlbum(r) ? 2 : 0) + (paired?.Invoke(r) == true ? 1 : 0);
            if (s > score) (pick, score) = (i, s);
        }
        return pick;
    }

    private static long Pixels(MediaRecord r) => (long)(r.Width ?? 0) * (r.Height ?? 0);
}
