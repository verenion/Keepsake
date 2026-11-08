using System.Globalization;
using PhotoLibrary.AI.Clip;
using PhotoLibrary.Core.Db;

namespace PhotoLibrary.AI.Features;

public sealed record EventGroup(string Name, DateTime Start, DateTime End, IReadOnlyList<long> FileIds);

/// <summary>
/// Splits the dated library into events. Photos are walked in time order and a new event starts when the
/// gap is long, the GPS position has moved far, or the gap is moderate and the pictures no longer look alike.
/// </summary>
public sealed class EventGrouper
{
    /// <summary>A gap longer than this always starts a new event.</summary>
    public TimeSpan MaxGap { get; init; } = TimeSpan.FromHours(3);
    /// <summary>A gap longer than this starts a new event if the photos also look different.</summary>
    public TimeSpan SoftGap { get; init; } = TimeSpan.FromMinutes(40);
    public float SoftGapSimilarity { get; init; } = 0.6f;
    public double MaxDistanceKm { get; init; } = 15;
    public int MinSize { get; init; } = 3;

    public List<EventGroup> Group(IEnumerable<MediaRecord> records, Func<long, float[]?> embeddingOf, IReadOnlyDictionary<long, string> topTag)
    {
        var sorted = records.Where(r => r.InLibrary && r.Taken.HasValue).OrderBy(r => r.Taken!.Value).ToList();
        var events = new List<EventGroup>();
        var current = new List<MediaRecord>();

        foreach (var r in sorted)
        {
            if (current.Count > 0 && StartsNewEvent(current[^1], r, embeddingOf))
            {
                Flush();
                current = [];
            }
            current.Add(r);
        }
        Flush();
        return events;

        void Flush()
        {
            if (current.Count < MinSize) return;
            events.Add(new EventGroup(Name(current, topTag), current[0].Taken!.Value, current[^1].Taken!.Value, current.Select(x => x.Id).ToList()));
        }
    }

    private bool StartsNewEvent(MediaRecord prev, MediaRecord next, Func<long, float[]?> embeddingOf)
    {
        var gap = next.Taken!.Value - prev.Taken!.Value;
        if (gap > MaxGap) return true;
        if (prev.Lat is { } lat1 && prev.Lon is { } lon1 && next.Lat is { } lat2 && next.Lon is { } lon2 &&
            HaversineKm(lat1, lon1, lat2, lon2) > MaxDistanceKm) return true;
        if (gap > SoftGap && embeddingOf(prev.Id) is { } a && embeddingOf(next.Id) is { } b &&
            ClipModel.Dot(a, b) < SoftGapSimilarity) return true;
        return false;
    }

    /// <summary>"Sat 14 Mar 2020 · beach" or "14–16 Mar 2020 · hiking", with the commonest tag among the members.</summary>
    public static string Name(IReadOnlyList<MediaRecord> members, IReadOnlyDictionary<long, string> topTag)
    {
        var start = members[0].Taken!.Value;
        var end = members[^1].Taken!.Value;
        var ci = CultureInfo.CurrentCulture;
        string when;
        if (start.Date == end.Date) when = start.ToString("ddd d MMM yyyy", ci);
        else if (start.Year == end.Year && start.Month == end.Month) when = $"{start.Day}–{end.ToString("d MMM yyyy", ci)}";
        else when = $"{start.ToString("d MMM", ci)} – {end.ToString("d MMM yyyy", ci)}";

        var tag = members.Select(m => topTag.GetValueOrDefault(m.Id)).Where(t => t is not null)
            .GroupBy(t => t).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;
        return tag is null ? when : $"{when} · {tag}";
    }

    public static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371;
        var dLat = (lat2 - lat1) * Math.PI / 180;
        var dLon = (lon2 - lon1) * Math.PI / 180;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }
}
