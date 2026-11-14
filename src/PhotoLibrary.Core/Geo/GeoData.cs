using System.IO.Compression;
using System.Text;

namespace PhotoLibrary.Core.Geo;

/// <summary>A country's outline: rings of (longitude, latitude) points, with a bounding box for quick tests.</summary>
public sealed class Country(string name, string iso2, IReadOnlyList<float[]> rings)
{
    public string Name { get; } = name;
    public string Iso2 { get; } = iso2;
    /// <summary>Each ring is lon0, lat0, lon1, lat1, …</summary>
    public IReadOnlyList<float[]> Rings { get; } = rings;
    public (float MinLon, float MinLat, float MaxLon, float MaxLat) Bounds { get; } = BoundsOf(rings);

    /// <summary>Even-odd point-in-polygon over every ring (holes such as lakes and enclaves cancel out).</summary>
    public bool Contains(double lat, double lon)
    {
        var (minLon, minLat, maxLon, maxLat) = Bounds;
        if (lon < minLon || lon > maxLon || lat < minLat || lat > maxLat) return false;
        var inside = false;
        foreach (var r in Rings)
        {
            for (int i = 0, j = r.Length - 2; i < r.Length; j = i, i += 2)
            {
                double xi = r[i], yi = r[i + 1], xj = r[j], yj = r[j + 1];
                if ((yi > lat) != (yj > lat) && lon < (xj - xi) * (lat - yi) / (yj - yi) + xi) inside = !inside;
            }
        }
        return inside;
    }

    private static (float, float, float, float) BoundsOf(IReadOnlyList<float[]> rings)
    {
        float minLon = 180, minLat = 90, maxLon = -180, maxLat = -90;
        foreach (var r in rings)
            for (var i = 0; i < r.Length; i += 2)
            {
                minLon = Math.Min(minLon, r[i]); maxLon = Math.Max(maxLon, r[i]);
                minLat = Math.Min(minLat, r[i + 1]); maxLat = Math.Max(maxLat, r[i + 1]);
            }
        return (minLon, minLat, maxLon, maxLat);
    }
}

/// <summary>A town or city from GeoNames.</summary>
public sealed record Town(string Name, string Region, string Iso2, float Lat, float Lon, int Population);

/// <summary>
/// The offline world map: Natural Earth country outlines (public domain) and GeoNames towns of 15,000+ people
/// (CC BY 4.0), bundled with the app and loaded once. Nothing is ever fetched from the internet.
/// </summary>
public static class GeoData
{
    private static readonly Lazy<IReadOnlyList<Country>> _countries = new(() => Load("countries.bin.gz", ReadCountries));
    private static readonly Lazy<IReadOnlyList<Town>> _towns = new(() => Load("places.bin.gz", ReadTowns));

    public static IReadOnlyList<Country> Countries => _countries.Value;
    public static IReadOnlyList<Town> Towns => _towns.Value;

    private static readonly Lazy<Dictionary<string, string>> _countryNames = new(() =>
        Countries.Where(c => c.Iso2.Length == 2).GroupBy(c => c.Iso2).ToDictionary(g => g.Key, g => g.First().Name, StringComparer.OrdinalIgnoreCase));

    /// <summary>"France" for "FR"; the code itself for the few territories the outlines don't name.</summary>
    public static string CountryName(string iso2) => _countryNames.Value.GetValueOrDefault(iso2, iso2);

    private static T Load<T>(string name, Func<BinaryReader, T> read)
    {
        using var raw = typeof(GeoData).Assembly.GetManifestResourceStream("PhotoLibrary.Core.Geo.Data." + name)
                        ?? throw new InvalidOperationException($"Map data {name} is missing from the build.");
        using var gz = new GZipStream(raw, CompressionMode.Decompress);
        using var reader = new BinaryReader(gz, Encoding.UTF8);
        return read(reader);
    }

    private static string ReadString(BinaryReader r) => Encoding.UTF8.GetString(r.ReadBytes(r.ReadUInt16()));

    private static void Expect(BinaryReader r, string magic)
    {
        if (Encoding.ASCII.GetString(r.ReadBytes(4)) != magic) throw new InvalidDataException("Map data is damaged.");
    }

    private static IReadOnlyList<Country> ReadCountries(BinaryReader r)
    {
        Expect(r, "KSC1");
        var list = new List<Country>(r.ReadInt32());
        for (var n = list.Capacity; n > 0; n--)
        {
            var name = ReadString(r);
            var iso = ReadString(r);
            var rings = new float[r.ReadInt32()][];
            for (var i = 0; i < rings.Length; i++)
            {
                var ring = rings[i] = new float[r.ReadInt32() * 2];
                for (var k = 0; k < ring.Length; k++) ring[k] = r.ReadSingle();
            }
            list.Add(new Country(name, iso, rings));
        }
        return list;
    }

    private static IReadOnlyList<Town> ReadTowns(BinaryReader r)
    {
        Expect(r, "KSP1");
        var list = new List<Town>(r.ReadInt32());
        for (var n = list.Capacity; n > 0; n--)
            list.Add(new Town(ReadString(r), ReadString(r), ReadString(r), r.ReadSingle(), r.ReadSingle(), r.ReadInt32()));
        return list;
    }
}
