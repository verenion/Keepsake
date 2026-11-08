using PhotoLibrary.Core;
using PhotoLibrary.Core.Db;
using PhotoLibrary.Core.Thumbnails;
using SkiaSharp;
using Xunit;

namespace PhotoLibrary.Tests;

/// <summary>Thumbnails are sized by their short edge (tiles crop to fill) at the configured size, and can be cleared.</summary>
public class ThumbnailTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "photolib-thumbs-" + Guid.NewGuid().ToString("N"));

    public ThumbnailTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private MediaRecord Photo(int w, int h, string name)
    {
        var path = Path.Combine(_root, name);
        using (var bmp = new SKBitmap(w, h))
        {
            bmp.Erase(SKColors.Teal);
            File.WriteAllBytes(path, SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Jpeg, 90).ToArray());
        }
        return new MediaRecord { Path = path, Sha256 = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"), Size = 1, MTime = 1, Kind = MediaKind.Image, Width = w, Height = h };
    }

    [Theory]
    [InlineData(1200, 800, 256)]   // landscape: short edge is the height
    [InlineData(800, 1200, 384)]   // portrait: short edge is the width
    public async Task ShortEdgeMatchesTheSetting(int w, int h, int size)
    {
        var thumbs = new ThumbnailService(Path.Combine(_root, "cache")) { Size = size, Quality = 90 };
        var path = await thumbs.GetAsync(Photo(w, h, $"p{w}x{h}.jpg"), CancellationToken.None);
        Assert.NotNull(path);
        using var t = SKBitmap.Decode(path);
        Assert.Equal(size, Math.Min(t.Width, t.Height));
    }

    [Fact]
    public void PanoramasAreCappedAndUnknownShapesAssumeThreeToTwo()
    {
        var thumbs = new ThumbnailService(Path.Combine(_root, "cache")) { Size = 300 };
        Assert.Equal(900, thumbs.LongEdgeFor(new MediaRecord { Path = "x", Sha256 = "x", Width = 10000, Height = 1000 }));
        Assert.Equal(450, thumbs.LongEdgeFor(new MediaRecord { Path = "x", Sha256 = "x" }));
    }

    [Fact]
    public async Task ClearRemovesTheCache()
    {
        var thumbs = new ThumbnailService(Path.Combine(_root, "cache"));
        var path = await thumbs.GetAsync(Photo(600, 400, "c.jpg"), CancellationToken.None);
        Assert.True(File.Exists(path));
        Assert.Equal(1, thumbs.Clear());
        Assert.False(File.Exists(path));
    }
}
