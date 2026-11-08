using PhotoLibrary.AI.Features;
using PhotoLibrary.Core;
using PhotoLibrary.Core.Db;
using Xunit;

namespace PhotoLibrary.Tests;

public class EventGrouperTests
{
    private static long _nextId = 1;

    private static MediaRecord Photo(DateTime taken, double? lat = null, double? lon = null, bool inLibrary = true) => new()
    {
        Id = _nextId++, Path = $@"C:\lib\{_nextId}.jpg", Sha256 = "x", Kind = MediaKind.Image, InLibrary = inLibrary, Taken = taken, Lat = lat, Lon = lon,
    };

    private static readonly Dictionary<long, string> NoTags = new();

    [Fact]
    public void SplitsOnLongGapsAndDropsSmallGroups()
    {
        var t = new DateTime(2020, 5, 1, 10, 0, 0);
        var records = new List<MediaRecord>();
        for (var i = 0; i < 5; i++) records.Add(Photo(t.AddMinutes(i * 10)));      // event 1
        records.Add(Photo(t.AddHours(6)));                                           // lone photo: dropped
        for (var i = 0; i < 3; i++) records.Add(Photo(t.AddDays(1).AddMinutes(i))); // event 2
        records.Add(Photo(t.AddDays(2)));                                            // no date → ignored
        records[^1].Taken = null;
        records.Add(Photo(t.AddDays(3), inLibrary: false));

        var events = new EventGrouper().Group(records.OrderBy(_ => Guid.NewGuid()), _ => null, NoTags);
        Assert.Equal(2, events.Count);
        Assert.Equal(5, events[0].FileIds.Count);
        Assert.Equal(3, events[1].FileIds.Count);
        Assert.Equal(t, events[0].Start);
        Assert.Equal(t.AddMinutes(40), events[0].End);
    }

    [Fact]
    public void SplitsWhenLocationMoves()
    {
        var t = new DateTime(2020, 5, 1, 10, 0, 0);
        var london = (51.5, -0.12);
        var brighton = (50.82, -0.14);
        var records = new List<MediaRecord>();
        for (var i = 0; i < 3; i++) records.Add(Photo(t.AddMinutes(i), london.Item1, london.Item2));
        for (var i = 0; i < 3; i++) records.Add(Photo(t.AddMinutes(10 + i), brighton.Item1, brighton.Item2));
        var events = new EventGrouper().Group(records, _ => null, NoTags);
        Assert.Equal(2, events.Count);
        Assert.InRange(EventGrouper.HaversineKm(london.Item1, london.Item2, brighton.Item1, brighton.Item2), 70, 80);
    }

    [Fact]
    public void SplitsOnModerateGapOnlyWhenPhotosLookDifferent()
    {
        var t = new DateTime(2020, 5, 1, 10, 0, 0);
        var first = Enumerable.Range(0, 3).Select(i => Photo(t.AddMinutes(i))).ToList();
        var second = Enumerable.Range(0, 3).Select(i => Photo(t.AddMinutes(60 + i))).ToList();
        var all = first.Concat(second).ToList();
        float[] look1 = [1, 0];
        float[] look2 = [0, 1];

        var same = new EventGrouper().Group(all, _ => look1, NoTags);
        Assert.Single(same);

        var different = new EventGrouper().Group(all, id => first.Any(p => p.Id == id) ? look1 : look2, NoTags);
        Assert.Equal(2, different.Count);

        var unknown = new EventGrouper().Group(all, _ => null, NoTags);
        Assert.Single(unknown);
    }

    [Fact]
    public void NamesUseDateRangeAndCommonestTag()
    {
        var t = new DateTime(2020, 3, 14, 10, 0, 0);
        var a = Photo(t);
        var b = Photo(t.AddHours(1));
        var c = Photo(t.AddDays(2));
        var tags = new Dictionary<long, string> { [a.Id] = "beach", [b.Id] = "beach", [c.Id] = "food" };
        Assert.EndsWith("· beach", EventGrouper.Name([a, b], tags));
        Assert.Contains("14", EventGrouper.Name([a, b], tags));
        Assert.Contains("14–16", EventGrouper.Name([a, b, c], tags));
        Assert.DoesNotContain("·", EventGrouper.Name([a, b], NoTags));
    }
}
