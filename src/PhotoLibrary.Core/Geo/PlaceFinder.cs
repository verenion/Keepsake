namespace PhotoLibrary.Core.Geo;

/// <summary>Where a photo was taken, in words: the nearest town (if any is close) and the country.</summary>
public sealed record Place(string Town, string Region, string Country)
{
    public static readonly Place Unknown = new("", "", "");
    public bool IsKnown => Country.Length > 0;
    /// <summary>"Paris, France"; "France" when no town is near; "Unknown place" at sea or off the map.</summary>
    public string Label => Town.Length > 0 ? $"{Town}, {Country}" : Country.Length > 0 ? Country : "Unknown place";
}

/// <summary>
/// Offline reverse geocoding: GPS position → nearest town within <see cref="MaxTownKm"/>, else the country whose
/// outline contains it. Towns are bucketed in a one-degree grid so a lookup only checks its neighbourhood.
/// </summary>
public sealed class PlaceFinder
{
    public const double MaxTownKm = 50;
    private const double PopulationWeightKm = 6;

    private readonly Dictionary<(int, int), List<Town>> _grid = [];
    private readonly IReadOnlyList<Country> _countries;

    public static PlaceFinder Shared => _shared.Value;
    private static readonly Lazy<PlaceFinder> _shared = new(() => new PlaceFinder(GeoData.Towns, GeoData.Countries));

    public PlaceFinder(IReadOnlyList<Town> towns, IReadOnlyList<Country> countries)
    {
        _countries = countries;
        foreach (var t in towns)
        {
            var key = Cell(t.Lat, t.Lon);
            if (!_grid.TryGetValue(key, out var list)) _grid[key] = list = [];
            list.Add(t);
        }
    }

    private static (int, int) Cell(double lat, double lon) => ((int)Math.Floor(lat), (int)Math.Floor(lon));

    public Place Find(double lat, double lon)
    {
        if (double.IsNaN(lat) || double.IsNaN(lon) || lat is < -90 or > 90 || lon is < -180 or > 180) return Place.Unknown;
        var (cy, cx) = Cell(lat, lon);
        Town? best = null;
        var bestKm = MaxTownKm;
        // 50 km is under one degree of latitude, but a degree of longitude shrinks towards the poles.
        var spanX = Math.Min(180, (int)Math.Ceiling(1 / Math.Max(0.05, Math.Cos(lat * Math.PI / 180))));
        for (var dy = -1; dy <= 1; dy++)
            for (var dx = -spanX; dx <= spanX; dx++)
            {
                var x = (cx + dx + 540) % 360 - 180; // wrap across the date line
                if (!_grid.TryGetValue((cy + dy, x), out var towns)) continue;
                foreach (var t in towns)
                {
                    var km = DistanceKm(lat, lon, t.Lat, t.Lon);
                    // Each tenfold step in population counts as 6 km closer: Paris beats "Paris 16 Passy" (a district)
                    // at the Eiffel Tower, while Versailles still beats Paris at the palace.
                    var score = km - Math.Log10(Math.Max(1, t.Population)) * PopulationWeightKm;
                    var bestScore = best is null ? double.MaxValue : bestKm - Math.Log10(Math.Max(1, best.Population)) * PopulationWeightKm;
                    if (km <= MaxTownKm && score < bestScore) { best = t; bestKm = km; }
                }
            }
        if (best is not null) return new Place(best.Name, best.Region, GeoData.CountryName(best.Iso2));
        foreach (var c in _countries)
            if (c.Contains(lat, lon)) return new Place("", "", c.Name);
        return Place.Unknown;
    }

    public static double DistanceKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double r = 6371;
        var dLat = (lat2 - lat1) * Math.PI / 180;
        var dLon = (lon2 - lon1) * Math.PI / 180;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                + Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * r * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }
}
