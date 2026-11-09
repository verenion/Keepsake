using PhotoLibrary.Core.Backup;
using PhotoLibrary.Core.Db;
using Xunit;

namespace PhotoLibrary.Tests;

/// <summary>The backup copies new and changed files, never loses an old copy, and refuses unsafe folders.</summary>
public class BackupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "photolib-backup-" + Guid.NewGuid().ToString("N"));
    private string Lib => Path.Combine(_root, "Library");
    private string Dest => Path.Combine(_root, "Backup");

    public BackupTests()
    {
        Directory.CreateDirectory(Lib);
        Directory.CreateDirectory(Dest);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private string Write(string rel, string text, DateTime? mtime = null)
    {
        var path = Path.Combine(Lib, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        File.SetLastWriteTimeUtc(path, mtime ?? new DateTime(2024, 5, 1, 12, 0, 0, DateTimeKind.Utc));
        return path;
    }

    private BackupResult Run() => BackupService.Run(Lib, Dest, null);

    private string[] Archived() => Directory.Exists(Path.Combine(Dest, BackupService.ArchiveFolder))
        ? Directory.GetFiles(Path.Combine(Dest, BackupService.ArchiveFolder), "*", SearchOption.AllDirectories)
        : [];

    [Fact]
    public void Copies_the_library_layout_and_skips_unchanged_files()
    {
        Write(@"2024\2024_05\2024_05_01\a.jpg", "a");
        Write(@"_Vault\v1\x.plv", "secret");

        var first = Run();
        Assert.True(first.Ok, string.Join("; ", first.Errors));
        Assert.Equal(2, first.Copied);
        Assert.Equal("a", File.ReadAllText(Path.Combine(Dest, @"2024\2024_05\2024_05_01\a.jpg")));
        Assert.Equal("secret", File.ReadAllText(Path.Combine(Dest, @"_Vault\v1\x.plv")));

        var second = Run();
        Assert.Equal(0, second.Copied + second.Updated + second.Archived);
        Assert.Equal(2, second.Unchanged);
    }

    [Fact]
    public void A_changed_file_keeps_its_old_copy_in_the_archive()
    {
        var path = Write("a.jpg", "old");
        Run();
        File.WriteAllText(path, "new!");

        var r = Run();
        Assert.Equal(1, r.Updated);
        Assert.Equal("new!", File.ReadAllText(Path.Combine(Dest, "a.jpg")));
        Assert.Equal("old", File.ReadAllText(Assert.Single(Archived())));
    }

    [Fact]
    public void Deleted_and_moved_files_are_archived_not_deleted()
    {
        var path = Write(@"2024\a.jpg", "a");
        Write("keep.jpg", "k");
        Run();
        File.Move(path, Path.Combine(Lib, "b.jpg"));

        var r = Run();
        Assert.Equal(1, r.Copied);
        Assert.Equal(1, r.Archived);
        Assert.True(File.Exists(Path.Combine(Dest, "b.jpg")));
        Assert.False(Directory.Exists(Path.Combine(Dest, "2024")));   // emptied folder is tidied away
        Assert.Equal("a", File.ReadAllText(Assert.Single(Archived())));
    }

    [Fact]
    public void An_empty_library_never_archives_the_backup()
    {
        var path = Write("a.jpg", "a");
        Run();
        File.Delete(path);

        var r = Run();
        Assert.Equal(0, r.Archived);
        Assert.True(File.Exists(Path.Combine(Dest, "a.jpg")));
    }

    [Fact]
    public void Snapshots_the_database()
    {
        Write("a.jpg", "a");
        using var db = new LibraryDb(Path.Combine(_root, "app.sqlite"));
        db.SetSetting("probe", "42");

        var r = BackupService.Run(Lib, Dest, db.SnapshotTo);
        Assert.True(r.Ok, string.Join("; ", r.Errors));

        using var copy = new LibraryDb(Path.Combine(Dest, BackupService.AppDataFolder, "library.sqlite"));
        Assert.Equal("42", copy.GetSetting("probe"));
    }

    [Fact]
    public void Rejects_unsafe_or_missing_folders()
    {
        Assert.NotNull(BackupService.Validate(Lib, Lib));
        Assert.NotNull(BackupService.Validate(Lib, Path.Combine(Lib, "inside")));
        Directory.CreateDirectory(Path.Combine(Lib, "inside"));
        Assert.NotNull(BackupService.Validate(Lib, Path.Combine(Lib, "inside")));
        Assert.NotNull(BackupService.Validate(Lib, _root));                       // contains the library
        Assert.NotNull(BackupService.Validate(Lib, Path.Combine(_root, "nope"))); // drive not connected
        Assert.Null(BackupService.Validate(Lib, Dest));

        var r = BackupService.Run(Lib, Path.Combine(_root, "nope"), null);
        Assert.False(r.Ok);
    }
}
