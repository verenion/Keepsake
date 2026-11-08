using PhotoLibrary.Core;
using Xunit;

namespace PhotoLibrary.Tests;

public class TakeoutSidecarTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "takeout-test-" + Guid.NewGuid().ToString("N"));

    public TakeoutSidecarTests()
    {
        Directory.CreateDirectory(_dir);
        TakeoutSidecar.ClearCache();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string Touch(string name, string content = "{}")
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, content);
        return p;
    }

    [Theory]
    [InlineData("IMG_1234.JPG", "IMG_1234.JPG.supplemental-metadata.json")]
    [InlineData("IMG_1234.JPG", "IMG_1234.JPG.supplemental-met.json")] // truncated by Google
    [InlineData("IMG_1234.JPG", "IMG_1234.JPG.json")]                   // older takeouts
    [InlineData("IMG_1234(1).JPG", "IMG_1234.JPG.supplemental-metadata(1).json")]
    [InlineData("IMG_1234-edited.JPG", "IMG_1234.JPG.supplemental-metadata.json")]
    [InlineData("PXL_20260721_004259098.NIGHT.RAW-02.ORIGINAL.dng", "PXL_20260721_004259098.NIGHT.RAW-02.ORIGINAL.dng.supplemental-metadata.json")]
    public void FindsSidecar(string media, string json)
    {
        var jp = Touch(json);
        var mp = Touch(media, "x");
        TakeoutSidecar.ClearCache();
        Assert.Equal(jp, TakeoutSidecar.Find(mp));
    }

    [Fact]
    public void NumberedCopyDoesNotStealPlainSidecar()
    {
        Touch("IMG_1.JPG.supplemental-metadata.json");
        var mp = Touch("IMG_1(1).JPG", "x");
        TakeoutSidecar.ClearCache();
        Assert.Null(TakeoutSidecar.Find(mp));
    }

    [Fact]
    public void ReadsTakenTimeAndGeo()
    {
        var jp = Touch("a.jpg.json", """
            {"title":"a.jpg","description":"hello","photoTakenTime":{"timestamp":"1562880000"},
             "geoData":{"latitude":51.5,"longitude":-0.12}}
            """);
        var info = TakeoutSidecar.Read(jp)!;
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1562880000).ToLocalTime().DateTime, info.Taken);
        Assert.Equal("hello", info.Description);
        Assert.Equal(51.5, info.Lat);
        Assert.Equal(-0.12, info.Lon);
    }
}
