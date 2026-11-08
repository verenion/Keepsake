using PhotoLibrary.Core;
using PhotoLibrary.Core.Albums;
using PhotoLibrary.Core.Db;
using PhotoLibrary.Core.Organise;
using PhotoLibrary.Core.Scanning;
using Xunit;

namespace PhotoLibrary.Tests;

/// <summary>
/// Virtual albums only touch the database; physical albums are folders under _Albums that files really move
/// into and out of, with one physical album per photo.
/// </summary>
public class AlbumTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "photolib-albums-" + Guid.NewGuid().ToString("N"));
    private readonly LibraryDb _db;
    private readonly AlbumService _albums;
    private string Lib => Path.Combine(_root, "Library");

    public AlbumTests()
    {
        Directory.CreateDirectory(Lib);
        _db = new LibraryDb(Path.Combine(_root, "test.sqlite"));
        _albums = new AlbumService(_db, Lib);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    /// <summary>A real (tiny) file in its dated folder, indexed with the given capture date.</summary>
    private MediaRecord Photo(string name, DateTime? taken)
    {
        var dir = taken is { } t ? Path.Combine(Lib, DateRules.DatedFolder(t)) : Path.Combine(Lib, LibraryFolders.Undated);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, [1, 2, 3]);
        var r = new MediaRecord { Path = path, Sha256 = name.PadLeft(64, '0'), Size = 3, MTime = 1, Kind = MediaKind.Image, InLibrary = true, Taken = taken, Added = DateTime.Now };
        _db.Upsert(r);
        return r;
    }

    private static readonly DateTime Day = new(2021, 6, 15, 10, 0, 0);

    [Fact]
    public void VirtualAlbumsAreJustLists()
    {
        var a = Photo("a.jpg", Day);
        var holiday = _albums.Create("Holiday", AlbumKind.Virtual);
        var best = _albums.Create("Best of", AlbumKind.Virtual, "#E5484D");

        _albums.Add(holiday, [a]);
        _albums.Add(best, [a]);
        _albums.Add(best, [a]); // twice is harmless

        Assert.Equal("#E5484D", best.Colour);
        Assert.Equal(a.Path, _db.GetByPath(a.Path)!.Path);
        Assert.Equal([a.Id], _db.GetAlbumItems(holiday.Id).Select(r => r.Id));
        Assert.Equal(1, _db.GetAlbum(best.Id)!.Count);
        Assert.Equal(new[] { holiday.Id, best.Id }.OrderBy(x => x), _db.AlbumsOf([a.Id])[a.Id].OrderBy(x => x));
        Assert.False(Directory.Exists(_albums.AlbumsDir));

        _albums.Remove(holiday, [a]);
        Assert.Empty(_db.GetAlbumItems(holiday.Id));
        Assert.Single(_db.GetAlbumItems(best.Id));
    }

    [Fact]
    public void PhysicalAlbumMovesFilesAndAllowsOnlyOne()
    {
        var a = Photo("a.jpg", Day);
        var b = Photo("b.jpg", null);
        var wedding = _albums.Create("Wedding: Sam & Alex", AlbumKind.Physical);
        var party = _albums.Create("Party", AlbumKind.Physical);
        var virt = _albums.Create("Faves", AlbumKind.Virtual);
        _albums.Add(virt, [a]);

        var change = _albums.Add(wedding, [a, b]);
        Assert.Equal(2, change.Moved);
        var folder = Path.Combine(Lib, "_Albums", "Wedding_ Sam & Alex");
        Assert.True(File.Exists(Path.Combine(folder, "a.jpg")));
        Assert.True(File.Exists(Path.Combine(folder, "b.jpg")));
        Assert.Equal(Path.Combine(folder, "a.jpg"), _db.GetAlbum(wedding.Id) is { } w ? _db.GetAlbumItems(w.Id)[0].Path : null);
        Assert.Equal(a.Id, _db.GetByPath(Path.Combine(folder, "a.jpg"))!.Id); // same row, so vectors and virtual membership survive
        Assert.Contains(virt.Id, _db.AlbumsOf([a.Id])[a.Id]);

        // Moving into a second physical album takes it out of the first.
        _albums.Add(party, [a]);
        Assert.True(File.Exists(Path.Combine(Lib, "_Albums", "Party", "a.jpg")));
        Assert.False(File.Exists(Path.Combine(folder, "a.jpg")));
        Assert.Equal([b.Id], _db.GetAlbumItems(wedding.Id).Select(r => r.Id));
        Assert.Equal([a.Id], _db.GetAlbumItems(party.Id).Select(r => r.Id));

        // Leaving a physical album re-files the photo into its dated folder (or _Undated).
        _albums.Remove(party, [a]);
        _albums.Remove(wedding, [b]);
        Assert.True(File.Exists(Path.Combine(Lib, DateRules.DatedFolder(Day), "a.jpg")));
        Assert.True(File.Exists(Path.Combine(Lib, LibraryFolders.Undated, "b.jpg")));
        Assert.False(Directory.Exists(Path.Combine(Lib, "_Albums", "Party"))); // emptied folders are pruned
        Assert.Empty(_db.GetAlbumItems(party.Id));
    }

    [Fact]
    public void RenameKindChangeAndDeleteMoveTheFolder()
    {
        var a = Photo("a.jpg", Day);
        var album = _albums.Create("Trip", AlbumKind.Virtual);
        _albums.Add(album, [a]);

        _albums.SetKind(album, AlbumKind.Physical);
        Assert.True(File.Exists(Path.Combine(Lib, "_Albums", "Trip", "a.jpg")));

        _albums.Rename(album, "Road trip");
        Assert.True(File.Exists(Path.Combine(Lib, "_Albums", "Road trip", "a.jpg")));
        Assert.False(Directory.Exists(Path.Combine(Lib, "_Albums", "Trip")));
        Assert.Equal("Road trip", _db.GetAlbum(album.Id)!.Name);
        Assert.Equal(Path.Combine(Lib, "_Albums", "Road trip", "a.jpg"), _db.GetAlbumItems(album.Id)[0].Path);

        _albums.SetKind(album, AlbumKind.Virtual);
        Assert.True(File.Exists(Path.Combine(Lib, DateRules.DatedFolder(Day), "a.jpg")));
        Assert.Single(_db.GetAlbumItems(album.Id)); // still a member, just not a folder any more

        _albums.SetKind(album, AlbumKind.Physical);
        _albums.Delete(album);
        Assert.Null(_db.GetAlbum(album.Id));
        Assert.True(File.Exists(Path.Combine(Lib, DateRules.DatedFolder(Day), "a.jpg"))); // nothing deleted from disk
        Assert.False(Directory.Exists(Path.Combine(Lib, "_Albums", "Road trip")));
    }

    [Fact]
    public void FoldersUnderAlbumsAreSyncedAfterAScan()
    {
        // The user makes a folder in Explorer and drops a photo in it.
        var dir = Path.Combine(Lib, "_Albums", "From Explorer");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "x.jpg"), [9, 9, 9]);
        var album = _albums.Create("Known", AlbumKind.Physical);
        var stray = Photo("stray.jpg", Day);
        _db.AddToAlbum(album.Id, [stray.Id]); // claims membership but the file isn't in the folder

        var library = _db.GetLibrary();
        var x = new MediaRecord { Path = Path.Combine(dir, "x.jpg"), Sha256 = new string('a', 64), Size = 3, MTime = 1, Kind = MediaKind.Image, InLibrary = true, Added = DateTime.Now };
        _db.Upsert(x);
        library.Add(x);

        Assert.True(_albums.SyncPhysical(library));
        var albums = _db.GetAlbums();
        var fromExplorer = Assert.Single(albums, a => a.Name == "From Explorer");
        Assert.True(fromExplorer.IsPhysical);
        Assert.Equal([x.Id], _db.GetAlbumItems(fromExplorer.Id).Select(r => r.Id));
        Assert.Empty(_db.GetAlbumItems(album.Id));
        Assert.False(_albums.SyncPhysical(library)); // settled
    }

    [Fact]
    public void OrganiseLeavesPhysicalAlbumsAlone()
    {
        var a = Photo("a.jpg", Day);
        var album = _albums.Create("Keep", AlbumKind.Physical);
        _albums.Add(album, [a]);
        var loose = Path.Combine(Lib, "loose.jpg");
        File.WriteAllBytes(loose, [4, 5, 6]);
        _db.Upsert(new MediaRecord { Path = loose, Sha256 = new string('b', 64), Size = 3, MTime = 1, Kind = MediaKind.Image, InLibrary = true, Taken = Day, Added = DateTime.Now });

        var records = _db.GetLibrary();
        Assert.True(Organizer.NeedsOrganising(records, Lib));
        var plan = Organizer.Plan(Lib, new ScanResult { Records = records, OtherFiles = [] }, useMtime: false);
        Assert.Single(plan.Items);
        Assert.Equal(loose, plan.Items[0].Record.Path);
        Assert.True(Organizer.NeedsOrganising(records.Where(r => r.Path != loose), Lib) == false);
    }
}
