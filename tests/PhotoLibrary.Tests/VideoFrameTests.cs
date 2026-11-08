using PhotoLibrary.AI.Db;
using PhotoLibrary.AI.Index;
using PhotoLibrary.Core;
using PhotoLibrary.Core.Db;
using PhotoLibrary.Core.Video;
using SkiaSharp;
using Xunit;

namespace PhotoLibrary.Tests;

/// <summary>Videos are indexed from many frames: storage, best-frame search, the mean vector and ffmpeg's JPEG stream.</summary>
public class VideoFrameTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "photolib-frames-" + Guid.NewGuid().ToString("N") + ".sqlite");
    private readonly LibraryDb _db;

    public VideoFrameTests()
    {
        _db = new LibraryDb(_path);
        _db.EnsureAiSchema();
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_path); } catch (IOException) { }
    }

    private static float[] Unit(params float[] v)
    {
        var n = MathF.Sqrt(v.Sum(x => x * x));
        return v.Select(x => x / n).ToArray();
    }

    [Fact]
    public void BestFramePerVideoWins()
    {
        var index = new VideoFrameIndex(3);
        index.Add(1, 0, Unit(1, 0, 0));
        index.Add(1, 10_000, Unit(0, 1, 0)); // the moment that matches
        index.Add(1, 20_000, Unit(0, 0, 1));
        index.Add(2, 0, Unit(1, 1, 0));

        var best = index.BestPerFile(Unit(0, 1, 0));
        Assert.Equal(10_000, best[1].Ms);
        Assert.Equal(1f, best[1].Score, 3);
        Assert.Equal(0, best[2].Ms);

        Assert.DoesNotContain(2L, index.BestPerFile(Unit(0, 1, 0), minScore: 0.9f).Keys);
        Assert.DoesNotContain(1L, index.BestPerFile(Unit(0, 1, 0), excludeFileId: 1).Keys);

        index.Replace(1, [(5_000, Unit(0, 0, 1))]);
        Assert.Equal(2, index.Count); // video 1's three frames became one; video 2 untouched
        Assert.Equal(5_000, index.BestPerFile(Unit(0, 0, 1))[1].Ms);
    }

    [Fact]
    public void MeanIsNormalised()
    {
        var m = VideoFrameIndex.Mean([Unit(1, 0), Unit(0, 1)]);
        Assert.Equal(1f, MathF.Sqrt(m.Sum(x => x * x)), 4);
        Assert.Equal(m[0], m[1], 4);
    }

    [Fact]
    public void FramesRoundTripAndAreForgottenPerModel()
    {
        var video = new MediaRecord { Path = @"C:\lib\clip.mp4", Sha256 = new string('v', 64), Size = 1, MTime = 1, Kind = MediaKind.Video, InLibrary = true };
        var photo = new MediaRecord { Path = @"C:\lib\a.jpg", Sha256 = new string('p', 64), Size = 1, MTime = 1, Kind = MediaKind.Image, InLibrary = true };
        _db.Upsert(video);
        _db.Upsert(photo);
        _db.StoreVideoFrames("m1", video.Id, [(0, Unit(1, 0)), (10_000, Unit(0, 1))]);
        _db.StoreVideoFrames("m1", video.Id, [(0, Unit(1, 0)), (12_000, Unit(0, 1))]); // replaces, not appends
        _db.StoreVideoFrames("m2", video.Id, [(0, Unit(1, 0))]);
        _db.StoreBatch("m1", [(video.Id, Unit(1, 1), []), (photo.Id, Unit(1, 0), [])]);

        var frames = _db.LoadVideoFrames("m1");
        Assert.Equal([0, 12_000], frames.Select(f => f.Ms));
        Assert.Equal(new HashSet<long> { video.Id }, _db.VideoFrameFileIds("m1"));

        _db.ForgetVideoIndex("m1");
        Assert.Empty(_db.LoadVideoFrames("m1"));
        Assert.Single(_db.LoadVideoFrames("m2"));
        Assert.Equal(new HashSet<long> { photo.Id }, _db.EmbeddedFileIds("m1")); // photos keep their vectors
    }

    [Fact]
    public void LongVideosAreSampledMoreSparsely()
    {
        Assert.Equal(10, VideoFrames.EffectiveInterval(10, 60, 120_000));     // 2 min: every 10 s
        Assert.Equal(60, VideoFrames.EffectiveInterval(10, 60, 3_600_000));   // 1 h: capped at 60 frames
        Assert.Equal(10, VideoFrames.EffectiveInterval(10, 60, null));
    }

    [Fact]
    public void JpegStreamIsSplitIntoFrames()
    {
        byte[] Jpeg(SKColor c)
        {
            using var b = new SKBitmap(8, 8);
            b.Erase(c);
            return SKImage.FromBitmap(b).Encode(SKEncodedImageFormat.Jpeg, 90).ToArray();
        }
        var stream = Jpeg(SKColors.Red).Concat(Jpeg(SKColors.Lime)).Concat(Jpeg(SKColors.Blue)).ToArray();
        var parts = VideoFrames.SplitJpegs(stream).ToList();
        Assert.Equal(3, parts.Count);
        using var second = SKBitmap.Decode(parts[1]);
        Assert.True(second.GetPixel(4, 4).Green > 200);
    }
}
