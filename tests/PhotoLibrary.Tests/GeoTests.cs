using PhotoLibrary.Core.Geo;
using Xunit;

namespace PhotoLibrary.Tests;

/// <summary>The bundled offline map data loads, and GPS positions turn into the right places.</summary>
public class GeoTests
{
    [Fact]
    public void BundledDataLoads()
    {
        Assert.InRange(GeoData.Countries.Count, 200, 300);
        Assert.InRange(GeoData.Towns.Count, 20_000, 60_000);
        Assert.Equal("France", GeoData.CountryName("FR"));
    }

    [Theory]
    [InlineData(48.8584, 2.2945, "Paris", "France")]          // Eiffel Tower
    [InlineData(51.5007, -0.1246, "London", "United Kingdom")] // Westminster
    [InlineData(40.6892, -74.0445, null, "United States of America")] // Statue of Liberty: some town nearby
    [InlineData(-33.8568, 151.2153, "Sydney", "Australia")]
    [InlineData(64.1466, -21.9426, "Reykjavík", "Iceland")]
    [InlineData(48.8049, 2.1204, "Versailles", "France")]     // the palace: a real town, not "Paris"
    public void FindsTownsAndCountries(double lat, double lon, string? town, string country)
    {
        var place = PlaceFinder.Shared.Find(lat, lon);
        Assert.Equal(country, place.Country);
        if (town is not null) Assert.Equal(town, place.Town);
        Assert.True(place.Town.Length > 0);
    }

    [Fact]
    public void RemotePlacesFallBackToTheCountryAndTheSeaIsUnknown()
    {
        var sahara = PlaceFinder.Shared.Find(23.4, 12.0);        // deep in the Sahara, far from any big town
        Assert.Equal("", sahara.Town);
        Assert.True(sahara.IsKnown);
        Assert.False(PlaceFinder.Shared.Find(-30, -140).IsKnown); // South Pacific
        Assert.False(PlaceFinder.Shared.Find(double.NaN, 0).IsKnown);
    }
}
