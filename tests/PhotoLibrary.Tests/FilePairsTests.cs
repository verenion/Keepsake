using PhotoLibrary.Core;
using PhotoLibrary.Core.Albums;
using PhotoLibrary.Core.Db;
using PhotoLibrary.Core.Import;
using PhotoLibrary.Core.Layout;
using PhotoLibrary.Core.Organise;
using PhotoLibrary.Core.Pairs;
using PhotoLibrary.Core.Scanning;
using Xunit;

namespace PhotoLibrary.Tests;

/// <summary>A photo's RAW file, Live Photo clip or Pixel ORIGINAL travels with it and isn't shown on its own.</summary>
public class FilePairsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "photolib-pairs-" + Guid.NewGuid().ToString("N"));
    private readonly LibraryDb _db;
    private readonly AlbumService _albums;
    private string Lib => Path.Combine(_root, "Library");
    private static readonly DateTime May31 = new(2012, 5, 31, 23, 30, 0);
    private int _n;

    public FilePairsTests()
    {
        Directory.CreateDirectory(Lib);
        _db = new LibraryDb(Path.Combine(_root, "test.sqlite"));
        _db.AddDateFolders(["2012"]);
        _albums = new AlbumService(_db, Lib);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private MediaRecord File_(string rel, DateTime? taken, long? durationMs = null, string? under = null)
    {
        var path = Path.Combine(under ?? Lib, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [(byte)++_n]);
        var r = new MediaRecord
        {
            Path = path, Sha256 = _n.ToString("x").PadLeft(64, '0'), Size = 1, MTime = 1, Kind = MediaKinds.KindOf(path)!.Value,
            InLibrary = under is null, Taken = taken, DurationMs = durationMs, Added = DateTime.Now,
        };
        if (under is null) _db.Upsert(r);
        return r;
    }

    private static MediaRecord R(string path, long? durationMs = null) =>
        new() { Path = path, Sha256 = "x", Kind = MediaKinds.KindOf(path)!.Value, DurationMs = durationMs, Added = DateTime.Now };

    [Theory]
    [InlineData("IMG_1234.HEIC", "IMG_1234")]
    [InlineData("PXL_20240101_101112345.RAW-01.COVER.jpg", "PXL_20240101_101112345")]
    [InlineData("PXL_20240101_101112345.RAW-02.ORIGINAL.dng", "PXL_20240101_101112345")]
    [InlineData("PXL_20240101_101112345.RAW-01.MP.COVER.jpg", "PXL_20240101_101112345")]
    [InlineData("PXL_20240101_101112345.MP.COVER", "PXL_20240101_101112345")]
    [InlineData("Holiday.jpg", "Holiday")]
    [InlineData("v1.2.jpg", "v1.2")]
    public void KeysIgnoreExtensionsAndPhoneSuffixes(string name, string key) => Assert.Equal(key, FilePairs.Key(name));

    [Fact]
    public void FindsTheCombinationsPhonesAndCamerasWrite()
    {
        var pairs = FilePairs.Find([
            R(@"C:\L\a\PXL_1.RAW-01.COVER.jpg"), R(@"C:\L\a\PXL_1.RAW-02.ORIGINAL.dng"),
            R(@"C:\L\a\IMG_2.HEIC"), R(@"C:\L\a\IMG_2.MOV", 2500),
            R(@"C:\L\a\DSC_3.JPG"), R(@"C:\L\a\DSC_3.NEF"),
            R(@"C:\L\a\PXL_4.PORTRAIT-01.COVER.jpg"), R(@"C:\L\a\PXL_4.PORTRAIT-02.ORIGINAL.jpg"),
            R(@"C:\L\a\IMG_5.JPG"), R(@"C:\L\a\IMG_5.MOV", 95_000),   // a long video is its own thing
            R(@"C:\L\a\IMG_6.JPG"), R(@"C:\L\a\IMG_6.PNG"),           // two stills: ambiguous, left alone
            R(@"C:\L\a\IMG_7.JPG"), R(@"C:\L\b\IMG_7.CR2"),           // different folders
        ]);
        Assert.Equal(4, pairs.Count);
        Assert.Contains(pairs, p => p.Primary.FileName == "PXL_1.RAW-01.COVER.jpg" && p.Kind == PairKind.Raw);
        Assert.Contains(pairs, p => p.Primary.FileName == "IMG_2.HEIC" && p.Kind == PairKind.Live);
        Assert.Contains(pairs, p => p.Primary.FileName == "DSC_3.JPG" && p.Kind == PairKind.Raw);
        Assert.Contains(pairs, p => p.Primary.FileName == "PXL_4.PORTRAIT-01.COVER.jpg" && p.Kind == PairKind.Original);
    }

    [Fact]
    public void CompanionsAreHiddenFromTheGridAndCounts()
    {
        File_(@"2012\2012_05\2012_05_31\IMG_1.JPG", May31);
        File_(@"2012\2012_05\2012_05_31\IMG_1.CR2", May31);
        Assert.Equal(1, FilePairs.Refresh(_db));
        var shown = _db.GetLibraryRange(new DateTime(2012, 1, 1), new DateTime(2013, 1, 1));
        var photo = Assert.Single(shown);
        Assert.Equal(PairKind.Raw, photo.PairKind);
        Assert.Equal(1, Assert.Single(_db.GetDayCounts()).Count);
        Assert.Equal(2, _db.WithCompanions(shown).Count);

        // The JPEG goes (e.g. a duplicate removed on its own): the RAW then shows by itself.
        _db.Hide([photo.Id]);
        FilePairs.Refresh(_db);
        Assert.Equal("IMG_1.CR2", Assert.Single(_db.GetLibraryRange(new DateTime(2012, 1, 1), new DateTime(2013, 1, 1))).FileName);
    }

    [Fact]
    public void OrganiseMovesAShotTogetherEvenWhenTheClipsDateIsADayOut()
    {
        File_(@"Inbox\IMG_1.HEIC", May31);
        File_(@"Inbox\IMG_1.MOV", May31.AddHours(1), 2000);     // QuickTime time in UTC: already 1 June
        File_(@"2012\2012_05\2012_05_31\IMG_1.HEIC", May31);    // a different shot with the same name
        _albums.SetRole("Inbox", FolderRole.Date);
        var plan = Organizer.Plan(new ScanResult { Records = _db.GetLibrary(), OtherFiles = [] }, useMtime: false, _albums.Folders());
        var day = Path.Combine(Lib, "2012", "2012_05", "2012_05_31");
        Assert.Contains(plan.Items, i => i.Dest == Path.Combine(day, "IMG_1_1.HEIC"));
        Assert.Contains(plan.Items, i => i.Dest == Path.Combine(day, "IMG_1_1.MOV"));
        Assert.Equal(2, plan.Items.Count);
    }

    [Fact]
    public void PixelShotsKeepMatchingNamesWhenRenamed()
    {
        var alloc = new DestAllocator();
        alloc.Reserve(@"C:\L\d\PXL_1.RAW-01.COVER.jpg");
        var (photo, companions) = alloc.ClaimGroup(@"C:\L\d\PXL_1.RAW-01.COVER.jpg", ["PXL_1.RAW-02.ORIGINAL.dng"]);
        Assert.Equal(@"C:\L\d\PXL_1_1.RAW-01.COVER.jpg", photo);
        Assert.Equal(@"C:\L\d\PXL_1_1.RAW-02.ORIGINAL.dng", Assert.Single(companions));
        Assert.Equal("PXL_1_1", FilePairs.Key(Path.GetFileName(companions[0])));
    }

    [Fact]
    public void AlbumsMoveAndRemoveTheWholeShot()
    {
        var jpg = File_(@"2012\2012_05\2012_05_31\PXL_1.RAW-01.COVER.jpg", May31);
        File_(@"2012\2012_05\2012_05_31\PXL_1.RAW-02.ORIGINAL.dng", May31);
        FilePairs.Refresh(_db);
        jpg = _db.GetByPath(jpg.Path)!;

        var album = _albums.Create("Party", AlbumKind.Physical);
        _albums.Add(album, [jpg]);
        Assert.True(File.Exists(Path.Combine(Lib, "Party", "PXL_1.RAW-02.ORIGINAL.dng")));
        Assert.Equal(1, _db.GetAlbums().Single().Count);

        _albums.Remove(album, [_db.GetByPath(Path.Combine(Lib, "Party", "PXL_1.RAW-01.COVER.jpg"))!]);
        Assert.True(File.Exists(Path.Combine(Lib, "2012", "2012_05", "2012_05_31", "PXL_1.RAW-02.ORIGINAL.dng")));
        Assert.False(File.Exists(Path.Combine(Lib, "Party", "PXL_1.RAW-02.ORIGINAL.dng")));
    }

    [Fact]
    public async Task ImportCopiesAShotTogetherAndPutsAMissingRawBesideItsPhoto()
    {
        var src = Path.Combine(_root, "Card");
        var heic = File_("IMG_1.HEIC", May31, under: src);
        var mov = File_("IMG_1.MOV", May31.AddHours(1), 2000, under: src);
        var known = File_("DSC_2.JPG", May31, under: src);
        var raw = File_("DSC_2.NEF", May31, under: src);
        var inLibrary = File_(@"Trip\DSC_2.JPG", May31);
        inLibrary.Sha256 = known.Sha256;
        _db.Upsert(inLibrary);

        var report = new CheckReport
        {
            Folder = src, LibraryRoot = Lib, Skipped = [],
            Items =
            [
                new CheckItem { Record = heic, Status = MatchStatus.Missing },
                new CheckItem { Record = mov, Status = MatchStatus.Missing },
                new CheckItem { Record = known, Status = MatchStatus.Present, Match = inLibrary },
                new CheckItem { Record = raw, Status = MatchStatus.Missing },
            ],
        };
        Assert.Equal(3, await new Importer(_db).ImportAsync(report, _albums.Folders(), null, CancellationToken.None));
        var day = Path.Combine(Lib, "2012", "2012_05", "2012_05_31");
        Assert.True(File.Exists(Path.Combine(day, "IMG_1.HEIC")));
        Assert.True(File.Exists(Path.Combine(day, "IMG_1.MOV")));
        Assert.True(File.Exists(Path.Combine(Lib, "Trip", "DSC_2.NEF")));
        Assert.Equal(2, _db.GetPairing().Count(p => p.Value.PairOf is not null));
    }

    [Fact]
    public void DuplicatesPreferTheCopyThatKeepsTheShotTogether()
    {
        var lone = R(@"C:\L\2026\a.jpg");
        var paired = R(@"C:\L\2026\b\a.jpg");
        paired.PairKind = PairKind.Raw;
        Assert.Equal(1, KeepChoice.Choose([lone, paired], identical: true, _ => false, r => r.PairKind != PairKind.None));
        Assert.Equal(0, KeepChoice.Choose([lone, paired], identical: true, r => r == lone, r => r.PairKind != PairKind.None)); // albums still win
    }
}
