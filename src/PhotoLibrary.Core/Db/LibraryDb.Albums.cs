using System.Globalization;
using Microsoft.Data.Sqlite;
using PhotoLibrary.Core.Albums;

namespace PhotoLibrary.Core.Db;

/// <summary>Album rows and membership. Membership is keyed by file id, so Organise moving a file keeps it in its albums.</summary>
public sealed partial class LibraryDb
{
    private void EnsureAlbumSchema() => Exec("""
        CREATE TABLE IF NOT EXISTS albums(id INTEGER PRIMARY KEY, name TEXT NOT NULL, colour TEXT NOT NULL,
            kind TEXT NOT NULL, cover_file_id INTEGER, created TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS album_items(album_id INTEGER NOT NULL, file_id INTEGER NOT NULL, added TEXT NOT NULL,
            PRIMARY KEY(album_id, file_id));
        CREATE INDEX IF NOT EXISTS ix_album_items_file ON album_items(file_id);
        CREATE TABLE IF NOT EXISTS date_folders(name TEXT PRIMARY KEY COLLATE NOCASE);
        """);

    /// <summary>Top-level folders that hold dated folders: ones Keepsake created, or the user confirmed.</summary>
    public List<string> GetDateFolders()
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM date_folders";
            using var r = cmd.ExecuteReader();
            var list = new List<string>();
            while (r.Read()) list.Add(r.GetString(0));
            return list;
        }
    }

    public void AddDateFolders(IEnumerable<string> names) => EachName("INSERT OR IGNORE INTO date_folders(name) VALUES(@n)", names);

    public void RemoveDateFolders(IEnumerable<string> names) => EachName("DELETE FROM date_folders WHERE name=@n", names);

    private void EachName(string sql, IEnumerable<string> names)
    {
        lock (_gate)
        {
            using var tx = _conn.BeginTransaction();
            using var cmd = _conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            var p = cmd.Parameters.Add("@n", SqliteType.Text);
            foreach (var n in names) { p.Value = n; cmd.ExecuteNonQuery(); }
            tx.Commit();
        }
    }

    private void EnsureAlbumColumns()
    {
        if (!ColumnExists("albums", "vault_id")) Exec("ALTER TABLE albums ADD COLUMN vault_id TEXT");
        if (!ColumnExists("albums", "organise")) Exec("ALTER TABLE albums ADD COLUMN organise INTEGER NOT NULL DEFAULT 0");
    }

    private static Album MapAlbum(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        Name = r.GetString(1),
        Colour = r.GetString(2),
        Kind = Album.KindFromDb(r.GetString(3)),
        CoverFileId = r.IsDBNull(4) ? null : r.GetInt64(4),
        Created = DateTime.ParseExact(r.GetString(5), "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        Count = r.GetInt32(6),
        VaultId = r.IsDBNull(7) ? null : r.GetString(7),
        Organise = r.GetInt64(8) != 0,
    };

    private const string AlbumSelect = """
        SELECT a.id, a.name, a.colour, a.kind, a.cover_file_id, a.created,
               (SELECT count(*) FROM album_items i JOIN files f ON f.id=i.file_id WHERE i.album_id=a.id AND f.in_library=1 AND f.pair_of IS NULL),
               a.vault_id, a.organise
        FROM albums a
        """;

    /// <summary>Every album with its live member count, alphabetically.</summary>
    public List<Album> GetAlbums()
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = AlbumSelect + " ORDER BY a.name COLLATE NOCASE, a.id";
            using var r = cmd.ExecuteReader();
            var list = new List<Album>();
            while (r.Read()) list.Add(MapAlbum(r));
            return list;
        }
    }

    public Album? GetAlbum(long id)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = AlbumSelect + " WHERE a.id=@id";
            cmd.Parameters.AddWithValue("@id", id);
            using var r = cmd.ExecuteReader();
            return r.Read() ? MapAlbum(r) : null;
        }
    }

    public Album CreateAlbum(string name, AlbumKind kind, string? colour = null, string? vaultId = null)
    {
        var album = new Album
        {
            Name = name.Trim(), Kind = kind, Created = DateTime.Now, VaultId = vaultId,
            Colour = Album.IsValidColour(colour) ? colour! : Album.Palette[(int)(Count("1=1", "albums") % Album.Palette.Length)],
        };
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "INSERT INTO albums(name,colour,kind,cover_file_id,created,vault_id) VALUES(@n,@c,@k,NULL,@d,@v); SELECT last_insert_rowid()";
            cmd.Parameters.AddWithValue("@v", (object?)vaultId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@n", album.Name);
            cmd.Parameters.AddWithValue("@c", album.Colour);
            cmd.Parameters.AddWithValue("@k", Album.KindToDb(kind));
            cmd.Parameters.AddWithValue("@d", Iso(album.Created));
            album.Id = (long)cmd.ExecuteScalar()!;
        }
        return album;
    }

    /// <summary>Saves name, colour, kind and cover. Moving files for a kind change is the caller's job.</summary>
    public void UpdateAlbum(Album album)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE albums SET name=@n, colour=@c, kind=@k, cover_file_id=@cov, vault_id=@v, organise=@o WHERE id=@id";
            cmd.Parameters.AddWithValue("@o", album.Organise ? 1 : 0);
            cmd.Parameters.AddWithValue("@v", (object?)album.VaultId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@n", album.Name.Trim());
            cmd.Parameters.AddWithValue("@c", album.Colour);
            cmd.Parameters.AddWithValue("@k", Album.KindToDb(album.Kind));
            cmd.Parameters.AddWithValue("@cov", (object?)album.CoverFileId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@id", album.Id);
            cmd.ExecuteNonQuery();
        }
    }

    public void DeleteAlbum(long id)
    {
        lock (_gate)
        {
            using var tx = _conn.BeginTransaction();
            using var cmd = _conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM album_items WHERE album_id=@id; DELETE FROM albums WHERE id=@id";
            cmd.Parameters.AddWithValue("@id", id);
            cmd.ExecuteNonQuery();
            tx.Commit();
        }
    }

    /// <summary>Members that are in the library, oldest first (undated last).</summary>
    public List<MediaRecord> GetAlbumItems(long albumId) =>
        Query("in_library=1 AND pair_of IS NULL AND id IN (SELECT file_id FROM album_items WHERE album_id=@a) ORDER BY taken IS NULL, taken, path", ("@a", albumId));

    public HashSet<long> GetAlbumItemIds(long albumId)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT file_id FROM album_items WHERE album_id=@a";
            cmd.Parameters.AddWithValue("@a", albumId);
            using var r = cmd.ExecuteReader();
            var set = new HashSet<long>();
            while (r.Read()) set.Add(r.GetInt64(0));
            return set;
        }
    }

    public void AddToAlbum(long albumId, IEnumerable<long> fileIds)
    {
        lock (_gate)
        {
            using var tx = _conn.BeginTransaction();
            var now = Iso(DateTime.Now);
            foreach (var id in fileIds)
            {
                using var cmd = _conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT OR IGNORE INTO album_items(album_id,file_id,added) VALUES(@a,@f,@d)";
                cmd.Parameters.AddWithValue("@a", albumId);
                cmd.Parameters.AddWithValue("@f", id);
                cmd.Parameters.AddWithValue("@d", now);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public void RemoveFromAlbum(long albumId, IEnumerable<long> fileIds)
    {
        lock (_gate)
        {
            using var tx = _conn.BeginTransaction();
            foreach (var id in fileIds)
            {
                using var cmd = _conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM album_items WHERE album_id=@a AND file_id=@f";
                cmd.Parameters.AddWithValue("@a", albumId);
                cmd.Parameters.AddWithValue("@f", id);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>Album ids each of the given files belongs to (files in no album are absent).</summary>
    public Dictionary<long, List<long>> AlbumsOf(IEnumerable<long> fileIds)
    {
        var result = new Dictionary<long, List<long>>();
        lock (_gate)
        {
            foreach (var chunk in fileIds.Chunk(500))
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = $"SELECT file_id, album_id FROM album_items WHERE file_id IN ({string.Join(",", chunk)})";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    if (!result.TryGetValue(r.GetInt64(0), out var list)) result[r.GetInt64(0)] = list = [];
                    list.Add(r.GetInt64(1));
                }
            }
        }
        return result;
    }

    /// <summary>Every membership at once (file id → album ids), for badges and filters over the whole library.</summary>
    public Dictionary<long, List<long>> AllMemberships()
    {
        var result = new Dictionary<long, List<long>>();
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT file_id, album_id FROM album_items";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (!result.TryGetValue(r.GetInt64(0), out var list)) result[r.GetInt64(0)] = list = [];
                list.Add(r.GetInt64(1));
            }
        }
        return result;
    }

    /// <summary>
    /// Removes every trace of files that went into a vault: the rows, album memberships, and the import and
    /// organise log rows that name them. Deleted content is overwritten (secure_delete) and the WAL is folded back
    /// in, so the old rows don't linger in the database file. AI rows are cleaned by the AI layer.
    /// </summary>
    public void ForgetFiles(IReadOnlyCollection<MediaRecord> records)
    {
        lock (_gate)
        {
            using (var tx = _conn.BeginTransaction())
            {
                foreach (var r in records)
                {
                    using var cmd = _conn.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = """
                        DELETE FROM album_items WHERE file_id=@id;
                        DELETE FROM files WHERE id=@id;
                        DELETE FROM organise_items WHERE original_path=@p OR new_path=@p;
                        DELETE FROM import_items WHERE source_path=@p OR library_path=@p;
                        UPDATE albums SET cover_file_id=NULL WHERE cover_file_id=@id;
                        """;
                    cmd.Parameters.AddWithValue("@id", r.Id);
                    cmd.Parameters.AddWithValue("@p", r.Path);
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }
            using var cp = _conn.CreateCommand();
            cp.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            cp.ExecuteNonQuery();
        }
    }

    private long Count(string where, string table)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"SELECT count(*) FROM {table} WHERE {where}";
            return Convert.ToInt64(cmd.ExecuteScalar());
        }
    }
}
