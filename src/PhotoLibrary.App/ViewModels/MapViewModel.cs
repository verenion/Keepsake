using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoLibrary.Core.Db;
using PhotoLibrary.Core.Geo;

namespace PhotoLibrary.App.ViewModels;

/// <summary>One located photo on the map.</summary>
public sealed record MapPoint(long Id, double Lat, double Lon);

/// <summary>An area to show, in degrees.</summary>
public sealed record MapBounds(double MinLat, double MinLon, double MaxLat, double MaxLon)
{
    public static MapBounds Of(IEnumerable<MapPoint> points)
    {
        var list = points as IReadOnlyCollection<MapPoint> ?? points.ToList();
        return new MapBounds(list.Min(p => p.Lat), list.Min(p => p.Lon), list.Max(p => p.Lat), list.Max(p => p.Lon));
    }
}

/// <summary>A country or a town in the place list, with how many photos were taken there.</summary>
public sealed partial class PlaceNode : ObservableObject
{
    public required string Name { get; init; }
    public required IReadOnlyList<MapPoint> Points { get; init; }
    public ObservableCollection<PlaceNode> Children { get; } = [];
    public string Label => $"{Name}  ({Points.Count:N0})";
    [ObservableProperty] public partial bool IsExpanded { get; set; }
}

/// <summary>
/// The Map tab: every photo with GPS coordinates on an offline world map, a list of the countries and towns they
/// were taken in, and the photos of whatever is picked (a cluster on the map, or a place in the list). Places come
/// from the bundled GeoNames towns, worked out on this PC; nothing is looked up online.
/// </summary>
public sealed partial class MapViewModel(MainViewModel main) : ViewModelBase
{
    private Dictionary<long, (MediaRecord Record, Place Place)> _byId = [];
    private List<MediaRecord>? _loadedFrom;
    private int _loadedCount = -1;

    [ObservableProperty] public partial IReadOnlyList<MapPoint> Points { get; set; } = [];
    public ObservableCollection<PlaceNode> Places { get; } = [];
    [ObservableProperty] public partial PlaceNode? SelectedPlace { get; set; }
    [ObservableProperty] public partial MapBounds? Focus { get; set; }
    [ObservableProperty] public partial IReadOnlySet<long>? Highlight { get; set; }
    [ObservableProperty] public partial string SummaryText { get; set; } = "";
    [ObservableProperty] public partial bool IsLoading { get; set; }

    public ObservableCollection<DayGroupViewModel> Groups { get; } = [];
    [ObservableProperty] public partial string PhotosHeader { get; set; } = "Click a circle on the map, or a place in the list, to see its photos.";
    [ObservableProperty] public partial bool HasPhotos { get; set; }

    /// <summary>For the map's hover text.</summary>
    public Func<IReadOnlyList<long>, string> Describe => ids => $"{PlaceText(ids)}  ·  {Count(ids.Count)}";

    /// <summary>Loads (or reloads, when the library changed since) the located photos and their places.</summary>
    public async Task EnsureLoadedAsync()
    {
        var library = main.State.Library;
        if (ReferenceEquals(library, _loadedFrom) && library.Count == _loadedCount) return;
        _loadedFrom = library;
        _loadedCount = library.Count;
        IsLoading = true;
        SummaryText = "Working out places…";
        try
        {
            var snapshot = library.ToList();
            var (located, unlocated) = await Task.Run(() =>
            {
                var finder = PlaceFinder.Shared;
                var located = new List<(MediaRecord, Place)>();
                var unlocated = 0;
                foreach (var r in snapshot)
                {
                    if (r.IsCompanion) continue; // a RAW or Live Photo clip is its photo's
                    // 0,0 ("Null Island") is what broken GPS writes, not a real place.
                    if (r.Lat is not { } lat || r.Lon is not { } lon || (Math.Abs(lat) < 1e-6 && Math.Abs(lon) < 1e-6)) { unlocated++; continue; }
                    located.Add((r, finder.Find(lat, lon)));
                }
                return (located, unlocated);
            });
            Build(located, unlocated);
        }
        finally { IsLoading = false; }
    }

    private void Build(List<(MediaRecord Record, Place Place)> located, int unlocated)
    {
        _byId = located.ToDictionary(x => x.Record.Id, x => x);
        Points = located.Select(x => new MapPoint(x.Record.Id, x.Record.Lat!.Value, x.Record.Lon!.Value)).ToList();
        Places.Clear();
        foreach (var country in located.GroupBy(x => x.Place.IsKnown ? x.Place.Country : "Unknown place")
                     .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.CurrentCulture))
        {
            var node = new PlaceNode { Name = country.Key, Points = country.Select(ToPoint).ToList() };
            foreach (var town in country.GroupBy(x => x.Place.Town.Length > 0 ? x.Place.Town : "Elsewhere in " + country.Key)
                         .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.CurrentCulture))
            {
                if (country.Key == "Unknown place") break;
                node.Children.Add(new PlaceNode { Name = town.Key, Points = town.Select(ToPoint).ToList() });
            }
            Places.Add(node);
        }
        var countries = Places.Count(p => p.Name != "Unknown place");
        var towns = Places.Sum(p => p.Children.Count);
        SummaryText = located.Count == 0
            ? "None of your photos has a location yet. Phones usually record one when location is switched on for the camera."
            : $"{Count(located.Count)} with a location, in {towns:N0} town{(towns == 1 ? "" : "s")} across {countries:N0} countr{(countries == 1 ? "y" : "ies")}"
              + (unlocated > 0 ? $"  ·  {unlocated:N0} without" : "");
        ClearPhotos();

        static MapPoint ToPoint((MediaRecord Record, Place Place) x) => new(x.Record.Id, x.Record.Lat!.Value, x.Record.Lon!.Value);
    }

    partial void OnSelectedPlaceChanged(PlaceNode? value)
    {
        if (value is null) return;
        Focus = null;
        Focus = MapBounds.Of(value.Points);
        ShowPhotos(value.Points.Select(p => p.Id).ToList(), value.Name);
    }

    /// <summary>A cluster was clicked on the map.</summary>
    [RelayCommand]
    private void ShowCluster(IReadOnlyList<long>? ids)
    {
        if (ids is not { Count: > 0 }) return;
        SelectedPlace = null;
        ShowPhotos(ids, PlaceText(ids));
    }

    [RelayCommand]
    private void ShowEverywhere()
    {
        SelectedPlace = null;
        Focus = null; // so the same bounds still re-centre the map after panning
        if (Points.Count > 0) Focus = MapBounds.Of(Points);
        ClearPhotos();
    }

    private void ShowPhotos(IReadOnlyList<long> ids, string title)
    {
        foreach (var g in Groups) foreach (var p in g.Photos) p.ReleaseThumbnail();
        Groups.Clear();
        var records = ids.Select(id => _byId.TryGetValue(id, out var x) ? x.Record : null).OfType<MediaRecord>()
            .OrderByDescending(r => r.Taken).ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var day in records.GroupBy(r => r.Taken is { } t ? t.ToString("dddd d MMMM yyyy", CultureInfo.CurrentCulture) : "No date"))
            Groups.Add(new DayGroupViewModel(day.Key, day.Select(r => new PhotoViewModel(r, main, SelectInGrid))));
        Highlight = records.Select(r => r.Id).ToHashSet();
        PhotosHeader = $"{title}  ·  {Count(records.Count)}";
        HasPhotos = records.Count > 0;
    }

    private void ClearPhotos()
    {
        foreach (var g in Groups) foreach (var p in g.Photos) p.ReleaseThumbnail();
        Groups.Clear();
        Highlight = null;
        HasPhotos = false;
        PhotosHeader = "Click a circle on the map, or a place in the list, to see its photos.";
    }

    private void SelectInGrid(PhotoViewModel photo, SelectMode mode) =>
        main.ApplySelection(Groups.SelectMany(g => g.Photos).ToList(), photo, mode);

    /// <summary>"Paris, France", or "Paris, Lyon and 3 other places" for a mix.</summary>
    private string PlaceText(IReadOnlyList<long> ids)
    {
        var labels = ids.Select(id => _byId.TryGetValue(id, out var x) ? x.Place.Label : null).OfType<string>()
            .GroupBy(l => l).OrderByDescending(g => g.Count()).Select(g => g.Key).ToList();
        return labels.Count switch
        {
            0 => "Unknown place",
            1 => labels[0],
            2 => $"{labels[0]} and {labels[1]}",
            _ => $"{labels[0]}, {labels[1]} and {labels.Count - 2:N0} other place{(labels.Count == 3 ? "" : "s")}",
        };
    }

    private static string Count(int n) => n == 1 ? "1 photo" : $"{n:N0} photos";

    /// <summary>Photos left the library: drop them, and reload the map next time it's shown.</summary>
    public void OnPhotosRemoved(IReadOnlySet<long> ids)
    {
        _loadedFrom = null;
        foreach (var g in Groups.ToList())
        {
            foreach (var p in g.Photos.Where(p => ids.Contains(p.Record.Id)).ToList()) { p.ReleaseThumbnail(); g.Photos.Remove(p); }
            if (g.Photos.Count == 0) Groups.Remove(g);
        }
        HasPhotos = Groups.Count > 0;
    }
}
