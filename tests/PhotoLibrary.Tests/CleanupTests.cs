using PhotoLibrary.AI.Features;
using PhotoLibrary.AI.Index;
using PhotoLibrary.Core;
using PhotoLibrary.Core.Db;
using PhotoLibrary.Core.Hashing;
using PhotoLibrary.Core.Import;
using PhotoLibrary.Core.Scanning;
using SkiaSharp;
using Xunit;

namespace PhotoLibrary.Tests;

/// <summary>Duplicate grouping, poor-photo judgement, and soft removal surviving rescans and imports.</summary>
public class CleanupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "photolib-clean-" + Guid.NewGuid().ToString("N"));

    public CleanupTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static MediaRecord Rec(long id, string sha, ulong? dhash = null, int w = 100, int h = 100, long size = 1000, DateTime? taken = null) =>
        new() { Id = id, Path = $@"C:\lib\{id}.jpg", Sha256 = sha.PadLeft(64, '0'), DHash = dhash, DHashDone = dhash is not null, Width = w, Height = h, Size = size, Kind = MediaKind.Image, InLibrary = true, Taken = taken ?? new DateTime(2020, 1, 1), Added = new DateTime(2021, 1, 1).AddDays(id) };

    private static float[] Unit(params float[] v)
    {
        var n = MathF.Sqrt(v.Sum(x => x * x));
        return v.Select(x => x / n).ToArray();
    }

    [Fact]
    public void DuplicateLevelsWidenStepByStep()
    {
        // 1,2: identical bytes. 3: same picture as 1 (fingerprint 1 bit off, high cosine). 4: burst shot (cosine 0.98).
        // 5: same scene (cosine 0.95). 6: unrelated. 7,8: plain images that fingerprint alike but look different.
        var records = new List<MediaRecord>
        {
            Rec(1, "a", 0b1111, w: 4000, h: 3000, size: 5000), Rec(2, "a", 0b1111, w: 4000, h: 3000, size: 5000),
            Rec(3, "b", 0b1110, w: 1000, h: 750, size: 500),
            Rec(4, "c", 0b1111_0000_1111, w: 4000, h: 3000, size: 5100),
            Rec(5, "d", 0xF0F0F0F0),
            Rec(6, "e", 0x0F0F0F0F),
            Rec(7, "f", 0xFFFF_FFFF_0000_0000UL), Rec(8, "g", 0xFFFF_FFFF_0000_0000UL),
        };
        var index = new EmbeddingIndex(3);
        index.Add(1, Unit(1, 0, 0)); index.Add(2, Unit(1, 0, 0)); index.Add(3, Unit(1, 0.01f, 0));
        index.Add(4, Unit(1, 0.2f, 0));       // cos ≈ 0.981
        index.Add(5, Unit(1, 0, 0.36f));      // cos ≈ 0.94 to 1-3, but only 0.92 to 4: similar, never near-identical
        index.Add(6, Unit(0, 0, 1));
        index.Add(7, Unit(0, 1, 0)); index.Add(8, Unit(0, 0.3f, -1)); // cos ≈ 0.29: not the same picture (and far from 6)
        var finder = new DuplicateFinder();

        var exact = finder.Find(records, DuplicateLevel.Identical, index);
        var g = Assert.Single(exact);
        Assert.Equal(DuplicateLevel.Identical, g.Level);
        Assert.Equal([1L, 2L], g.FileIds.Order());
        Assert.Equal(5000, g.ReclaimableBytes);

        var same = finder.Find(records, DuplicateLevel.SamePicture, index);
        g = Assert.Single(same);
        Assert.Equal(DuplicateLevel.SamePicture, g.Level);
        Assert.Equal([1L, 2L, 3L], g.FileIds.Order());
        Assert.Equal(1L, g.FileIds[0]); // the biggest copy is kept first
        Assert.DoesNotContain(same, x => x.FileIds.Contains(7)); // plain images rejected by the model

        var near = finder.Find(records, DuplicateLevel.NearIdentical, index);
        g = Assert.Single(near);
        Assert.Equal(DuplicateLevel.NearIdentical, g.Level);
        Assert.Equal([1L, 2L, 3L, 4L], g.FileIds.Order());

        var similar = finder.Find(records, DuplicateLevel.Similar, index);
        g = Assert.Single(similar);
        Assert.Equal(DuplicateLevel.Similar, g.Level);
        Assert.Equal([1L, 2L, 3L, 4L, 5L], g.FileIds.Order());

        // Without a model, "same picture" falls back to fingerprints plus matching dates.
        var noModel = finder.Find(records, DuplicateLevel.SamePicture, null);
        Assert.Contains(noModel, x => x.FileIds.Order().SequenceEqual([1L, 2L, 3L]));
        Assert.Contains(noModel, x => x.FileIds.Order().SequenceEqual([7L, 8L])); // the known weakness the model fixes
    }

    [Fact]
    public void RawJpegPairsAreLeftAloneUnlessAsked()
    {
        var raw = Rec(1, "a", 0b1111, w: 1280, h: 960, size: 30_000); // recorded smaller, as a preview-sized DNG would be
        raw.Path = @"C:\lib\IMG_0001.CR2";
        var jpg = Rec(2, "b", 0b1111, w: 6000, h: 4000, size: 3_000);
        jpg.Path = @"C:\lib\IMG_0001.JPG";
        var other = Rec(3, "c", 0b1111, w: 6000, h: 4000, size: 3_000);
        other.Path = @"C:\lib\IMG_0002.JPG";
        var records = new List<MediaRecord> { raw, jpg, other };

        var groups = new DuplicateFinder().Find(records, DuplicateLevel.SamePicture, null);
        var g = Assert.Single(groups);
        Assert.Equal([2L, 3L], g.FileIds.Order()); // the JPEG copies pair up; the RAW is only compared with RAWs

        groups = new DuplicateFinder { IncludeRawJpegPairs = true }.Find(records, DuplicateLevel.SamePicture, null);
        g = Assert.Single(groups);
        Assert.Equal([1L, 2L, 3L], g.FileIds.Order());
        Assert.Equal(1L, g.FileIds[0]); // the RAW is the keeper even though it is recorded with fewer pixels
    }

    [Fact]
    public void AllPairsMatchesBruteForce()
    {
        var rng = new Random(1);
        var index = new EmbeddingIndex(8);
        var vectors = new Dictionary<long, float[]>();
        for (long i = 0; i < 60; i++) { var v = Unit(Enumerable.Range(0, 8).Select(_ => (float)rng.NextDouble()).ToArray()); vectors[i] = v; index.Add(i, v); }
        var pairs = index.AllPairs(0.95f);
        var expected = 0;
        foreach (var a in vectors.Keys) foreach (var b in vectors.Keys) if (a < b && PhotoLibrary.AI.Clip.ClipModel.Dot(vectors[a], vectors[b]) >= 0.95f) expected++;
        Assert.Equal(expected, pairs.Count);
        Assert.All(pairs, p => Assert.True(p.A < p.B && p.Score >= 0.95f));
    }

    [Fact]
    public void QualityJudgesSyntheticImages()
    {
        static SKBitmap Make(Action<SKCanvas> draw)
        {
            var bmp = new SKBitmap(320, 240);
            using var c = new SKCanvas(bmp);
            draw(c);
            return bmp;
        }
        using var black = Make(c => c.Clear(SKColors.Black));
        using var white = Make(c => c.Clear(SKColors.White));
        using var flat = Make(c => c.Clear(new SKColor(120, 130, 140)));
        using var sharp = Make(c =>
        {
            c.Clear(SKColors.White);
            using var p = new SKPaint { Color = SKColors.Black };
            var rng = new Random(3);
            for (var i = 0; i < 400; i++) c.DrawRect(rng.Next(320), rng.Next(240), 6, 6, p);
        });
        using var blurry = Make(c =>
        {
            using var img = SKImage.FromBitmap(sharp);
            using var p = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(6, 6) };
            c.Clear(SKColors.White);
            c.DrawImage(img, 0, 0, p);
        });

        var qBlack = QualityAnalyzer.Measure(black);
        var qWhite = QualityAnalyzer.Measure(white);
        var qFlat = QualityAnalyzer.Measure(flat);
        var qSharp = QualityAnalyzer.Measure(sharp);
        var qBlurry = QualityAnalyzer.Measure(blurry);

        Assert.InRange(qBlack.Brightness, 0, 0.02f);
        Assert.InRange(qWhite.Brightness, 0.98f, 1);
        Assert.InRange(qFlat.Contrast, 0, 0.01f);
        Assert.True(qSharp.Sharpness > qBlurry.Sharpness * 5, $"sharp {qSharp.Sharpness} vs blurry {qBlurry.Sharpness}");

        // At the strictest-but-sane setting the hopeless ones are flagged and the good one passes.
        Assert.StartsWith("Almost completely black", QualityAnalyzer.Judge(qBlack, 0)!.Reason);
        Assert.StartsWith("Almost completely white", QualityAnalyzer.Judge(qWhite, 0)!.Reason);
        Assert.StartsWith("A flat", QualityAnalyzer.Judge(qFlat, 0)!.Reason);
        Assert.Null(QualityAnalyzer.Judge(qSharp, 1));
        Assert.StartsWith("Blurry", QualityAnalyzer.Judge(qBlurry, 1)!.Reason);
        // Severity orders black before merely dark.
        var merelyDark = QualityAnalyzer.Judge(new QualityMetrics(0.05f, 0.2f, 100), 0.5f);
        Assert.NotNull(merelyDark);
        Assert.StartsWith("Very dark", merelyDark.Reason);
        Assert.True(QualityAnalyzer.Judge(qBlack, 0.5f)!.Severity > merelyDark.Severity);
    }

    [Fact]
    public async Task HiddenPhotosStayOutOfRescansAndImports()
    {
        using var db = new LibraryDb(Path.Combine(_root, "lib.sqlite"));
        var lib = Path.Combine(_root, "library");
        Directory.CreateDirectory(lib);
        var a = WriteJpeg(Path.Combine(lib, "a.jpg"), SKColors.Red);
        var b = WriteJpeg(Path.Combine(lib, "b.jpg"), SKColors.Blue);
        var scanner = new Scanner(db);
        var scan = await scanner.IndexTreeAsync(lib, null, true, null, CancellationToken.None);
        Assert.Equal(2, scan.Records.Count);

        var hideMe = db.GetByPath(a)!;
        db.Hide([hideMe.Id]);
        Assert.Single(db.GetLibrary());
        Assert.Equal(1, db.CountHidden());

        // A rescan does not resurrect it, and the row survives (so the SHA is remembered).
        scan = await scanner.IndexTreeAsync(lib, null, true, null, CancellationToken.None);
        Assert.Equal(["b.jpg"], scan.Records.Select(r => r.FileName));
        Assert.True(db.GetByPath(a)!.Hidden);
        Assert.False(db.GetByPath(a)!.InLibrary);

        // Even if the file is touched (re-hashed path), it stays hidden.
        File.SetLastWriteTimeUtc(a, DateTime.UtcNow.AddMinutes(1));
        scan = await scanner.IndexTreeAsync(lib, null, true, null, CancellationToken.None);
        Assert.Equal(["b.jpg"], scan.Records.Select(r => r.FileName));
        Assert.True(db.GetByPath(a)!.Hidden);

        // An identical copy offered for import is reported present, not missing, and is not copied.
        var incoming = Path.Combine(_root, "incoming");
        Directory.CreateDirectory(incoming);
        File.Copy(a, Path.Combine(incoming, "copy-of-a.jpg"));
        var importer = new Importer(db);
        var report = await importer.CheckAsync(lib, incoming, db.GetLibrary(), exactOnly: true, null, CancellationToken.None);
        var item = Assert.Single(report.Items);
        Assert.Equal(MatchStatus.Present, item.Status);
        Assert.Contains("removed from the library", item.How);
        Assert.Equal(0, await importer.ImportAsync(report, new PhotoLibrary.Core.Layout.FolderMap(db, report.LibraryRoot), null, CancellationToken.None));

        // Restore brings it back.
        Assert.Equal(1, db.RestoreHidden());
        Assert.Equal(2, db.GetLibrary().Count);
        Assert.Equal(0, db.CountHidden());

        // Hidden rows whose file has gone are dropped by restore.
        db.Hide([hideMe.Id]);
        File.Delete(a);
        Assert.Equal(0, db.RestoreHidden());
        Assert.Null(db.GetByPath(a));
    }

    [Fact]
    public void RecycleRemovesFilesAndReportsFailures()
    {
        var p = Path.Combine(_root, "gone.txt");
        File.WriteAllText(p, "x");
        var failed = FileOps.Recycle([p, Path.Combine(_root, "never-existed.txt")]);
        Assert.Empty(failed);
        Assert.False(File.Exists(p));
    }

    private static string WriteJpeg(string path, SKColor color)
    {
        using var bmp = new SKBitmap(64, 48);
        using (var c = new SKCanvas(bmp)) { c.Clear(color); using var p = new SKPaint { Color = SKColors.White }; c.DrawCircle(20 + path.Length % 7, 24, 8, p); }
        using var data = bmp.Encode(SKEncodedImageFormat.Jpeg, 90);
        using var fs = File.Create(path);
        data.SaveTo(fs);
        return path;
    }
}
