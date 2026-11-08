using PhotoLibrary.Core.Db;
using PhotoLibrary.Core.Organise;
using PhotoLibrary.Core.Scanning;
using PhotoLibrary.Core.Vault;

namespace PhotoLibrary.Core.Albums;

/// <summary>What an album operation did to the disk: how many files moved, and which rows now have a new path.</summary>
public sealed record AlbumChange(int Added, int Removed, int Moved, List<(long Id, string NewPath)> Relocated, List<string> Errors)
{
    public static readonly AlbumChange None = new(0, 0, 0, [], []);
}

/// <summary>
/// Album membership plus the file moves a physical album implies. A physical album is the folder
/// <c>&lt;library&gt;\_Albums\&lt;name&gt;</c>: adding a photo moves its file there (out of any other physical
/// album), removing it moves the file back into its dated folder. Rows keep their ids throughout, so AI
/// vectors, thumbnails and virtual-album membership survive the move.
/// </summary>
public sealed class AlbumService(LibraryDb db, string libraryRoot)
{
    public string Root { get; } = Path.GetFullPath(libraryRoot);

    public string AlbumsDir => Path.Combine(Root, LibraryFolders.Albums);

    public string FolderFor(Album album) => album.IsSecure && album.VaultId is { } v
        ? VaultStore.FolderFor(Root, v)
        : Path.Combine(AlbumsDir, Album.FolderName(album.Name));

    public bool IsInFolder(Album album, string path) => IsUnder(path, FolderFor(album));

    private static bool IsUnder(string path, string dir)
    {
        var prefix = dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    /// <summary>Where a file belongs once it leaves every physical album: its dated folder, or _Undated.</summary>
    public string HomeFor(MediaRecord r) => HomeFor(r.Taken, r.FileName);

    public string HomeFor(DateTime? taken, string fileName) =>
        taken is { } t ? Path.Combine(Root, DateRules.DatedFolder(t), fileName) : Path.Combine(Root, LibraryFolders.Undated, fileName);

    public Album Create(string name, AlbumKind kind, string? colour = null, string? password = null)
    {
        if (kind == AlbumKind.Secure)
        {
            if (string.IsNullOrEmpty(password)) throw new ArgumentException("A secure album needs a password.", nameof(password));
            return db.CreateAlbum(name, kind, colour, VaultStore.Create(Root, password));
        }
        var album = db.CreateAlbum(name, kind, colour);
        if (album.IsPhysical) Directory.CreateDirectory(FolderFor(album));
        return album;
    }

    public AlbumChange Add(Album album, IReadOnlyList<MediaRecord> records)
    {
        var existing = db.GetAlbumItemIds(album.Id);
        var fresh = records.Where(r => !existing.Contains(r.Id)).ToList();
        if (!album.IsPhysical)
        {
            db.AddToAlbum(album.Id, fresh.Select(r => r.Id));
            return new AlbumChange(fresh.Count, 0, 0, [], []);
        }

        // One physical album per photo: leaving the others is implied by the move.
        var physical = db.GetAlbums().Where(a => a.IsPhysical && a.Id != album.Id).ToDictionary(a => a.Id);
        var memberships = db.AlbumsOf(records.Select(r => r.Id));
        foreach (var other in physical.Values)
        {
            var leaving = records.Where(r => memberships.TryGetValue(r.Id, out var ids) && ids.Contains(other.Id)).Select(r => r.Id).ToList();
            if (leaving.Count > 0) db.RemoveFromAlbum(other.Id, leaving);
        }

        var folder = FolderFor(album);
        var (moved, relocated, errors) = MoveInto(records.Where(r => !IsInFolder(album, r.Path)).ToList(), folder);
        var failed = new HashSet<string>(errors.Select(e => e.Path), Scanner.PathComparer);
        db.AddToAlbum(album.Id, fresh.Where(r => !failed.Contains(r.Path)).Select(r => r.Id));
        return new AlbumChange(fresh.Count(r => !failed.Contains(r.Path)), 0, moved, relocated, errors.Select(e => e.Message).ToList());
    }

    public AlbumChange Remove(Album album, IReadOnlyList<MediaRecord> records)
    {
        db.RemoveFromAlbum(album.Id, records.Select(r => r.Id));
        if (!album.IsPhysical) return new AlbumChange(0, records.Count, 0, [], []);
        var (moved, relocated, errors) = MoveHome(records.Where(r => IsInFolder(album, r.Path)).ToList());
        return new AlbumChange(0, records.Count, moved, relocated, errors.Select(e => e.Message).ToList());
    }

    /// <summary>Renames the album, and its folder when it is physical.</summary>
    public AlbumChange Rename(Album album, string newName)
    {
        newName = newName.Trim();
        if (newName.Length == 0 || newName == album.Name) return AlbumChange.None;
        var relocated = new List<(long, string)>();
        if (album.IsPhysical)
        {
            var oldDir = FolderFor(album);
            var newDir = Path.Combine(AlbumsDir, Album.FolderName(newName));
            if (!Scanner.PathComparer.Equals(oldDir, newDir) && Directory.Exists(oldDir))
            {
                if (Directory.Exists(newDir)) return new AlbumChange(0, 0, 0, [], [$"A folder called “{Path.GetFileName(newDir)}” already exists in {LibraryFolders.Albums}."]);
                Directory.Move(oldDir, newDir);
                foreach (var r in db.GetUnder(oldDir))
                {
                    var np = Path.Combine(newDir, Path.GetRelativePath(oldDir, r.Path));
                    db.UpdatePath(r.Path, np);
                    relocated.Add((r.Id, np));
                }
            }
        }
        album.Name = newName;
        db.UpdateAlbum(album);
        return new AlbumChange(0, 0, relocated.Count, relocated, []);
    }

    /// <summary>Switches an album between virtual and physical, moving its members in or out of the folder.</summary>
    public AlbumChange SetKind(Album album, AlbumKind kind)
    {
        if (album.Kind == kind) return AlbumChange.None;
        if (album.IsSecure || kind == AlbumKind.Secure)
            throw new InvalidOperationException("Use MakeSecure to turn an album into a vault; a vault is left by deleting it, which restores its photos.");
        var members = db.GetAlbumItems(album.Id);
        album.Kind = kind;
        db.UpdateAlbum(album);
        if (kind == AlbumKind.Virtual)
        {
            var (moved, relocated, errors) = MoveHome(members.Where(r => IsUnder(r.Path, AlbumsDir)).ToList());
            TryDeleteEmpty(FolderFor(album));
            return new AlbumChange(0, 0, moved, relocated, errors.Select(e => e.Message).ToList());
        }
        // Now physical: pull the members in (they leave any other physical album by moving).
        var change = Add(album, members);
        Directory.CreateDirectory(FolderFor(album));
        return change;
    }

    /// <summary>Deletes the album. A physical album's files go back to their dated folders first; nothing is deleted from disk.</summary>
    public AlbumChange Delete(Album album)
    {
        if (album.IsSecure) throw new InvalidOperationException("A secure album is deleted with DeleteVault, which needs its key.");
        var change = AlbumChange.None;
        if (album.IsPhysical)
        {
            var members = db.GetAlbumItems(album.Id).Where(r => IsInFolder(album, r.Path)).ToList();
            var (moved, relocated, errors) = MoveHome(members);
            change = new AlbumChange(0, members.Count, moved, relocated, errors.Select(e => e.Message).ToList());
            TryDeleteEmpty(FolderFor(album));
        }
        db.DeleteAlbum(album.Id);
        return change;
    }

    // ---- secure albums (vaults)

    /// <summary>What moving photos into a vault did. <see cref="Vaulted"/> are gone from the library and its indexes.</summary>
    public sealed record VaultChange(List<MediaRecord> Vaulted, List<string> Errors);

    /// <summary>
    /// Encrypts each photo into the album's vault, proves the encrypted copy decrypts to the same bytes, then
    /// deletes the original for good (not to the Recycle Bin) and erases its rows from the library database.
    /// The caller removes AI rows and cached thumbnails, which live outside Core.
    /// </summary>
    public VaultChange AddToVault(Album album, byte[] key, IReadOnlyList<MediaRecord> records, IProgress<(int Done, int Total, string Name)>? progress = null, CancellationToken ct = default)
    {
        if (!album.IsSecure) throw new InvalidOperationException("Not a secure album.");
        var dir = FolderFor(album);
        var vaulted = new List<MediaRecord>();
        var errors = new List<string>();
        var touched = new HashSet<string>(Scanner.PathComparer);
        for (var i = 0; i < records.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var r = records[i];
            progress?.Report((i, records.Count, r.FileName));
            VaultItem? item = null;
            try
            {
                item = VaultStore.Add(dir, key, r.Path, VaultItemInfo.From(r));
                File.Delete(r.Path);
                touched.Add(Path.GetDirectoryName(r.Path)!);
                vaulted.Add(r);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or VaultAuthenticationException)
            {
                // Never leave both copies: if the original couldn't be deleted, drop the encrypted one.
                if (item is not null && File.Exists(r.Path)) try { VaultStore.Delete(item); } catch (IOException) { }
                errors.Add($"{r.FileName}: {ex.Message}");
            }
        }
        if (vaulted.Count > 0) db.ForgetFiles(vaulted);
        Organizer.PruneEmptyDirs(Root, touched.Where(d => IsUnder(d, Path.Combine(Root, LibraryFolders.Albums))
            || IsUnder(d, Path.Combine(Root, LibraryFolders.Undated))));
        progress?.Report((records.Count, records.Count, ""));
        return new VaultChange(vaulted, errors);
    }

    /// <summary>Turns an existing album into a vault: creates it, then encrypts the members in.</summary>
    public VaultChange MakeSecure(Album album, string password, byte[]? keyOut = null, IProgress<(int, int, string)>? progress = null, CancellationToken ct = default)
    {
        if (album.IsSecure) return new VaultChange([], []);
        var members = db.GetAlbumItems(album.Id);
        var oldFolder = album.IsPhysical ? FolderFor(album) : null;
        album.VaultId = VaultStore.Create(Root, password);
        album.Kind = AlbumKind.Secure;
        album.CoverFileId = null;
        db.UpdateAlbum(album);
        var key = VaultStore.Unlock(FolderFor(album), password);
        try
        {
            var change = AddToVault(album, key, members, progress, ct);
            if (oldFolder is not null) TryDeleteEmpty(oldFolder);
            if (keyOut is not null) key.CopyTo(keyOut, 0);
            return change;
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(key); }
    }

    /// <summary>Decrypts vault items back into the library's dated folders and removes them from the vault.</summary>
    public (List<string> Restored, List<string> Errors) RestoreFromVault(byte[] key, IReadOnlyList<VaultItem> items)
    {
        var alloc = new DestAllocator();
        var restored = new List<string>();
        var errors = new List<string>();
        foreach (var item in items)
        {
            try
            {
                var dest = alloc.Claim(HomeFor(item.Info.Taken, item.Info.OriginalName));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                VaultStore.Export(item, key, dest);
                VaultStore.Delete(item);
                restored.Add(dest);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or VaultAuthenticationException)
            {
                errors.Add($"{item.Info.OriginalName}: {ex.Message}");
            }
        }
        return (restored, errors);
    }

    /// <summary>
    /// Deletes a secure album: every photo is decrypted back into the library first, then the vault folder and its
    /// key file go. If any photo can't be restored the vault is kept, so nothing is ever lost.
    /// </summary>
    public (List<string> Restored, List<string> Errors) DeleteVault(Album album, byte[] key)
    {
        if (!album.IsSecure) throw new InvalidOperationException("Not a secure album.");
        var dir = FolderFor(album);
        var damaged = new List<string>();
        var items = Directory.Exists(dir) ? VaultStore.List(dir, key, damaged) : [];
        var (restored, errors) = RestoreFromVault(key, items);
        errors.AddRange(damaged.Select(d => $"{d}: could not be decrypted"));
        if (errors.Count > 0) return (restored, errors);
        try
        {
            File.Delete(Path.Combine(dir, VaultStore.KeyFileName));
            TryDeleteEmpty(dir);
            TryDeleteEmpty(VaultStore.VaultsDir(Root));
        }
        catch (IOException ex) { errors.Add(ex.Message); }
        db.DeleteAlbum(album.Id);
        return (restored, errors);
    }

    /// <summary>
    /// After a rescan: every file sitting in a physical album's folder is a member (the user may have dropped
    /// it there in Explorer), members that left the folder are not, and a folder under _Albums with no album
    /// row becomes a physical album. Returns true when anything changed.
    /// </summary>
    public bool SyncPhysical(IReadOnlyList<MediaRecord> library)
    {
        var changed = false;
        var albums = db.GetAlbums();
        if (Directory.Exists(AlbumsDir))
        {
            var known = new HashSet<string>(albums.Where(a => a.IsPhysical).Select(a => Album.FolderName(a.Name)), Scanner.PathComparer);
            foreach (var dir in Directory.EnumerateDirectories(AlbumsDir))
            {
                var name = Path.GetFileName(dir);
                if (known.Contains(name)) continue;
                albums.Add(db.CreateAlbum(name, AlbumKind.Physical));
                changed = true;
            }
        }
        var vaultsDir = VaultStore.VaultsDir(Root);
        if (Directory.Exists(vaultsDir))
        {
            var knownVaults = new HashSet<string>(albums.Where(a => a.VaultId is not null).Select(a => a.VaultId!), StringComparer.OrdinalIgnoreCase);
            foreach (var dir in Directory.EnumerateDirectories(vaultsDir))
            {
                var id = Path.GetFileName(dir);
                if (knownVaults.Contains(id) || !File.Exists(Path.Combine(dir, VaultStore.KeyFileName))) continue;
                albums.Add(db.CreateAlbum($"Recovered vault {id[..Math.Min(6, id.Length)]}", AlbumKind.Secure, vaultId: id));
                changed = true;
            }
        }
        foreach (var album in albums.Where(a => a.IsPhysical))
        {
            var inFolder = library.Where(r => IsInFolder(album, r.Path)).Select(r => r.Id).ToHashSet();
            var members = db.GetAlbumItemIds(album.Id);
            var add = inFolder.Where(id => !members.Contains(id)).ToList();
            var byId = library.ToDictionary(r => r.Id);
            var drop = members.Where(id => !inFolder.Contains(id) && byId.ContainsKey(id)).ToList();
            if (add.Count > 0) db.AddToAlbum(album.Id, add);
            if (drop.Count > 0) db.RemoveFromAlbum(album.Id, drop);
            changed |= add.Count > 0 || drop.Count > 0;
        }
        return changed;
    }

    // ---- moving

    private (int Moved, List<(long, string)> Relocated, List<(string Path, string Message)> Errors) MoveInto(List<MediaRecord> records, string folder)
    {
        var alloc = new DestAllocator();
        return MoveEach(records, r => alloc.Claim(Path.Combine(folder, r.FileName)));
    }

    private (int Moved, List<(long, string)> Relocated, List<(string Path, string Message)> Errors) MoveHome(List<MediaRecord> records)
    {
        var alloc = new DestAllocator();
        var touched = new HashSet<string>(Scanner.PathComparer);
        var result = MoveEach(records, r => { touched.Add(Path.GetDirectoryName(r.Path)!); return alloc.Claim(HomeFor(r)); });
        Organizer.PruneEmptyDirs(AlbumsDir, touched.Where(d => IsUnder(d, AlbumsDir)));
        return result;
    }

    private (int Moved, List<(long, string)> Relocated, List<(string Path, string Message)> Errors) MoveEach(List<MediaRecord> records, Func<MediaRecord, string> dest)
    {
        var moved = 0;
        var relocated = new List<(long, string)>();
        var errors = new List<(string, string)>();
        foreach (var r in records)
        {
            var target = dest(r);
            if (Scanner.PathComparer.Equals(target, r.Path)) continue;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(r.Path, target);
                db.UpdatePath(r.Path, target);
                r.Path = target;
                relocated.Add((r.Id, target));
                moved++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add((r.Path, $"{r.FileName}: {ex.Message}"));
            }
        }
        return (moved, relocated, errors);
    }

    private static void TryDeleteEmpty(string dir)
    {
        try
        {
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
