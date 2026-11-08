using PhotoLibrary.AI;
using PhotoLibrary.AI.Clip;
using PhotoLibrary.Core;
using PhotoLibrary.Core.Db;
using PhotoLibrary.Core.Thumbnails;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace PhotoLibrary.Tests;

/// <summary>
/// End to end on a throwaway library: index, search by text, find similar, group into events. Needs the
/// downloaded model; skips otherwise.
/// </summary>
public class AiEngineTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "photolib-ai-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Fact]
    public async Task IndexSearchSimilarAndEvents()
    {
        if (!ClipModelFiles.Default.IsInstalled) { output.WriteLine("model not installed; skipped"); return; }
        Directory.CreateDirectory(_root);
        using var db = new LibraryDb(Path.Combine(_root, "test.sqlite"));
        var thumbs = new ThumbnailService(Path.Combine(_root, "thumbs"));
        using var engine = new AiEngine(db, thumbs);
        engine.EnsureSchema();

        // Three "red" shots in one sitting, then a blue one the next day, a green one later that day, and one that can't decode.
        var t = new DateTime(2021, 6, 5, 14, 0, 0);
        var records = new List<MediaRecord>
        {
            Photo("red1.jpg", SKColors.Red, t),
            Photo("red2.jpg", SKColors.Crimson, t.AddMinutes(5)),
            Photo("red3.jpg", SKColors.DarkRed, t.AddMinutes(9)),
            Photo("blue.jpg", SKColors.Blue, t.AddDays(1)),
            Photo("blue2.jpg", SKColors.RoyalBlue, t.AddDays(1).AddMinutes(2)),
            Photo("blue3.jpg", SKColors.Navy, t.AddDays(1).AddMinutes(4)),
        };
        var broken = Path.Combine(_root, "broken.jpg");
        File.WriteAllText(broken, "not an image");
        records.Add(new MediaRecord { Path = broken, Sha256 = "broken".PadLeft(64, '0'), Size = 1, MTime = 1, Kind = MediaKind.Image, InLibrary = true, Taken = t.AddDays(2) });
        db.UpsertMany(records);

        Assert.Equal((0, 7), engine.IndexStatus());
        var reports = new List<IndexProgress>();
        var result = await engine.IndexAsync(new SyncProgress<IndexProgress>(reports.Add), CancellationToken.None);
        Assert.Equal(new IndexResult(6, 1, 7), result);
        Assert.Equal((6, 7), engine.IndexStatus());
        Assert.Equal(7, reports[^1].Done);

        // Re-running only looks at what's missing (the broken file is remembered as undecodable).
        Assert.Equal(new IndexResult(0, 0, 0), await engine.IndexAsync(null, CancellationToken.None));

        var red = engine.SearchText("a red image", top: 3, minZ: float.MinValue);
        output.WriteLine("red: " + string.Join(", ", red.Select(r => $"{r.Record.FileName} {r.Score:0.000}")));
        Assert.Equal(3, red.Count);
        Assert.All(red, r => Assert.StartsWith("red", r.Record.FileName));

        var blue = engine.SearchText("a blue image 2021", top: 3, minZ: float.MinValue);
        Assert.All(blue, r => Assert.StartsWith("blue", r.Record.FileName));
        Assert.Empty(engine.SearchText("a blue image 2019", top: 3, minZ: float.MinValue));

        var similar = await engine.FindSimilarAsync(records[0], top: 2, minScore: float.MinValue);
        Assert.Equal(2, similar.Count);
        Assert.DoesNotContain(similar, s => s.Record.Id == records[0].Id);
        Assert.All(similar, s => Assert.StartsWith("red", s.Record.FileName));

        var tags = engine.TagCounts();
        output.WriteLine("tags: " + string.Join(", ", tags.Select(x => $"{x.Tag} {x.Count}")));
        Assert.NotEmpty(tags);

        var events = engine.BuildEvents();
        Assert.Equal(2, events.Count);
        Assert.Equal(["blue.jpg", "blue2.jpg", "blue3.jpg"], engine.EventMembers(events[0].Id).Select(r => r.FileName));
        Assert.Equal(["red1.jpg", "red2.jpg", "red3.jpg"], engine.EventMembers(events[1].Id).Select(r => r.FileName));
        Assert.Equal(events.Select(e => e.Id), engine.Events().Select(e => e.Id));
    }

    private MediaRecord Photo(string name, SKColor color, DateTime taken)
    {
        var path = Path.Combine(_root, name);
        using (var bmp = new SKBitmap(640, 480))
        {
            using var canvas = new SKCanvas(bmp);
            canvas.Clear(color);
            using var paint = new SKPaint { Color = SKColors.White };
            canvas.DrawCircle(320 + name.Length * 10, 240, 60, paint);
            using var data = bmp.Encode(SKEncodedImageFormat.Jpeg, 90);
            using var fs = File.Create(path);
            data.SaveTo(fs);
        }
        return new MediaRecord { Path = path, Sha256 = name.PadLeft(64, '0'), Size = 1, MTime = 1, Kind = MediaKind.Image, InLibrary = true, Taken = taken };
    }

    /// <summary>Progress&lt;T&gt; posts to a sync context; tests want the reports immediately.</summary>
    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
