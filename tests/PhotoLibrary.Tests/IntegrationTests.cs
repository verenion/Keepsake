using PhotoLibrary.Core;
using PhotoLibrary.Core.Albums;
using PhotoLibrary.Core.Db;
using PhotoLibrary.Core.Import;
using PhotoLibrary.Core.Organise;
using PhotoLibrary.Core.Scanning;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace PhotoLibrary.Tests;

/// <summary>
/// End-to-end: organise a messy library, undo it, then check and import an incoming folder. Uses a handful of
/// real camera JPEGs from the folder named by the PHOTOLIBRARY_SAMPLES environment variable when it is set (so
/// EXIF dates are real), otherwise generated images with dates in their file names.
/// </summary>
public class IntegrationTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "photolib-it-" + Guid.NewGuid().ToString("N"));
    private string Lib => Path.Combine(_root, "Library");
    private string Incoming => Path.Combine(_root, "Incoming");
    private string DbPath => Path.Combine(_root, "test.sqlite");

    private static readonly string Samples = Environment.GetEnvironmentVariable("PHOTOLIBRARY_SAMPLES") ?? "";

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    /// <summary>Six distinct photos, each from a different day.</summary>
    private List<string> SampleFiles()
    {
        var files = new List<string>();
        if (Samples.Length > 0 && Directory.Exists(Samples))
        {
            var seenDays = new HashSet<string>();
            foreach (var f in Directory.EnumerateFiles(Samples, "*.jpg", SearchOption.AllDirectories))
            {
                var day = Path.GetFileName(Path.GetDirectoryName(f)!);
                if (!day.StartsWith("2014") || !seenDays.Add(day) || new FileInfo(f).Length > 3_000_000) continue;
                files.Add(f);
                if (files.Count == 6) break;
            }
        }
        if (files.Count < 6)
        {
            files.Clear();
            Directory.CreateDirectory(Path.Combine(_root, "gen"));
            for (var i = 1; i <= 6; i++)
            {
                var p = Path.Combine(_root, "gen", $"2014-03-{10 + i:D2} 12.00.0{i}.jpg");
                WriteImage(p, 640, 480, (byte)(i * 40), 90);
                files.Add(p);
            }
        }
        return files;
    }

    private static void WriteImage(string path, int w, int h, byte seed, int quality)
    {
        using var bmp = new SKBitmap(w, h);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(new SKColor(seed, (byte)(255 - seed), 128));
            using var paint = new SKPaint { Color = SKColors.White };
            for (var i = 0; i < 6; i++) canvas.DrawCircle(w * (i + 1) / 7f, h * ((i * 37 + seed) % 100) / 100f, 20 + i * 8, paint);
        }
        using var data = bmp.Encode(SKEncodedImageFormat.Jpeg, quality);
        File.WriteAllBytes(path, data.ToArray());
    }

    private static void ReencodedCopy(string src, string dest, int quality)
    {
        using var bmp = SKBitmap.Decode(src);
        using var data = bmp.Encode(SKEncodedImageFormat.Jpeg, quality);
        File.WriteAllBytes(dest, data.ToArray());
    }

    [Fact]
    public async Task OrganiseThenUndo()
    {
        var samples = SampleFiles();
        var messy = Path.Combine(Lib, "Camera Roll");
        Directory.CreateDirectory(messy);
        for (var i = 0; i < 4; i++) File.Copy(samples[i], Path.Combine(messy, Path.GetFileName(samples[i])));
        File.Copy(samples[0], Path.Combine(Lib, Path.GetFileName(samples[0]).Replace(".", " (1).")));      // exact duplicate
        WriteImage(Path.Combine(messy, "nodate.jpg"), 64, 64, 7, 80);                                       // no EXIF, no date in name
        File.WriteAllText(Path.Combine(messy, "notes.txt"), "not media");

        using var db = new LibraryDb(DbPath);
        var scanner = new Scanner(db);
        var scan = await scanner.IndexTreeAsync(Lib, LibraryFolders.ExcludedFromIndex, true, null, CancellationToken.None);
        Assert.Equal(6, scan.Records.Count);

        // A folder made outside Keepsake is an album that Organise leaves alone...
        var albums = new AlbumService(db, Lib);
        Assert.True(albums.SyncFolders(scan.Records));
        var cameraRoll = Assert.Single(db.GetAlbums(), a => a.Name == "Camera Roll");
        Assert.True(cameraRoll.IsPhysical);
        Assert.False(cameraRoll.Organise);
        var leftAlone = Organizer.Plan(scan, useMtime: false, albums.Folders());
        Assert.DoesNotContain(leftAlone.Items, i => i.Record.Path.StartsWith(messy, StringComparison.OrdinalIgnoreCase));

        // ...until its album says to organise it.
        cameraRoll.Organise = true;
        db.UpdateAlbum(cameraRoll);
        Assert.True(Organizer.NeedsOrganising(scan.Records, albums.Folders()));
        var plan = Organizer.Plan(scan, useMtime: false, albums.Folders());
        output.WriteLine($"moves={plan.Moves} undated={plan.Undated}");
        Assert.Equal(5, plan.Moves); // the exact duplicate is filed too, with a suffix, for the Duplicates tab
        Assert.Equal(1, plan.Undated);
        Assert.Equal(1, plan.OtherFiles[".txt"]);
        foreach (var m in plan.Items.Where(i => i.Action == OrganiseAction.Move))
            Assert.Matches(@"\\\d{4}\\\d{4}_\d\d\\\d{4}_\d\d_\d\d\\[^\\]+$", m.Dest);

        var outcome = await Organizer.ApplyAsync(db, plan, null, CancellationToken.None);
        Assert.Equal(6, outcome.Moved);
        Assert.Equal(0, outcome.Errors);
        Assert.True(File.Exists(outcome.Manifest));
        Assert.False(Directory.Exists(messy) && Directory.EnumerateFiles(messy).Any(f => !f.EndsWith("notes.txt")));
        Assert.False(Directory.Exists(Path.Combine(Lib, LibraryFolders.Duplicates)));
        Assert.True(File.Exists(Path.Combine(Lib, LibraryFolders.Undated, "Camera Roll", "nodate.jpg")));

        var rescan = await scanner.IndexTreeAsync(Lib, LibraryFolders.ExcludedFromIndex, true, null, CancellationToken.None);
        Assert.Equal(6, rescan.Records.Count); // everything stays in the library
        Assert.False(Organizer.NeedsOrganising(rescan.Records, albums.Folders()));

        var (restored, skipped) = await Organizer.UndoAsync(db, outcome.RunId, null, CancellationToken.None);
        Assert.Equal(6, restored);
        Assert.Equal(0, skipped);
        for (var i = 0; i < 4; i++) Assert.True(File.Exists(Path.Combine(messy, Path.GetFileName(samples[i]))));
        Assert.True(File.Exists(Path.Combine(messy, "nodate.jpg")));
        Assert.False(Directory.Exists(Path.Combine(Lib, LibraryFolders.Duplicates)));
    }

    [Fact]
    public async Task CheckAndImport()
    {
        var samples = SampleFiles();
        using var db = new LibraryDb(DbPath);
        var scanner = new Scanner(db);

        // Library: 4 photos already organised.
        Directory.CreateDirectory(Lib);
        for (var i = 0; i < 4; i++)
        {
            var info = Core.Metadata.MetadataReader.Read(samples[i]);
            var dest = Path.Combine(Lib, DateRules.DatedFolder(info.Taken!.Value), Path.GetFileName(samples[i]));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(samples[i], dest);
        }
        var lib = (await scanner.IndexTreeAsync(Lib, LibraryFolders.ExcludedFromIndex, true, null, CancellationToken.None)).Records;

        // Incoming: an exact copy, a re-encoded copy (no EXIF), a new photo, and a sidecar-only json.
        Directory.CreateDirectory(Incoming);
        File.Copy(samples[0], Path.Combine(Incoming, "exact.jpg"));
        ReencodedCopy(samples[1], Path.Combine(Incoming, "reencoded.jpg"), 60);
        File.Copy(samples[4], Path.Combine(Incoming, Path.GetFileName(samples[4])));
        File.WriteAllText(Path.Combine(Incoming, "exact.jpg.supplemental-metadata.json"), """{"photoTakenTime":{"timestamp":"1400000000"}}""");

        var importer = new Importer(db);
        var report = await importer.CheckAsync(Lib, Incoming, lib, exactOnly: false, null, CancellationToken.None);
        foreach (var i in report.Items) output.WriteLine($"{i.Status,-8} {i.Record.FileName} -> {i.Match?.FileName} {i.How}");
        Assert.Equal(3, report.Items.Count);
        Assert.Equal(1, report.Present);
        Assert.Equal(1, report.Similar);
        Assert.Equal(1, report.Missing);
        Assert.Equal(1, report.Skipped[".json"]);
        var similar = report.Items.Single(i => i.Status == MatchStatus.Similar);
        Assert.Equal("reencoded.jpg", similar.Record.FileName);
        Assert.Equal(Path.GetFileName(samples[1]), similar.Match!.FileName);

        var imported = await importer.ImportAsync(report, new PhotoLibrary.Core.Layout.FolderMap(db, report.LibraryRoot), null, CancellationToken.None);
        Assert.Equal(1, imported);
        var missing = report.Items.Single(i => i.Status == MatchStatus.Missing);
        Assert.NotNull(missing.ImportedTo);
        Assert.True(File.Exists(missing.ImportedTo));
        Assert.StartsWith(Lib, missing.ImportedTo);
        Assert.True(File.Exists(Path.Combine(Incoming, Path.GetFileName(samples[4]))), "source must be left in place");

        // Second check: everything is now present or similar, nothing missing.
        var lib2 = (await scanner.IndexTreeAsync(Lib, LibraryFolders.ExcludedFromIndex, true, null, CancellationToken.None)).Records;
        var again = await importer.CheckAsync(Lib, Incoming, lib2, exactOnly: false, null, CancellationToken.None);
        Assert.Equal(0, again.Missing);
    }
}
