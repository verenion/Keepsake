using System.Globalization;
using Microsoft.Data.Sqlite;

namespace PhotoLibrary.Core.Db;

public sealed record DayCount(DateOnly Day, int Count);

/// <summary>
/// SQLite store for the index. One connection guarded by a lock: scanning runs on worker threads but writes
/// are batched through here, and SQLite is fast enough single-writer for this size of library.
/// </summary>
public sealed partial class LibraryDb : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly object _gate = new();

    public string Path { get; }

    public LibraryDb(string path)
    {
        Path = path;
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        _conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _conn.Open();
        // secure_delete: deleted rows are overwritten, so a photo moved into a vault leaves no readable remnant.
        Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA temp_store=MEMORY; PRAGMA secure_delete=ON;");
        Exec("""
            CREATE TABLE IF NOT EXISTS files(
                id INTEGER PRIMARY KEY, path TEXT NOT NULL UNIQUE, in_library INTEGER NOT NULL DEFAULT 0,
                size INTEGER NOT NULL, mtime INTEGER NOT NULL, sha256 TEXT NOT NULL, kind TEXT NOT NULL,
                taken TEXT, taken_source TEXT, make TEXT, model TEXT, width INTEGER, height INTEGER,
                orientation INTEGER, lat REAL, lon REAL, duration_ms INTEGER, dhash INTEGER,
                dhash_done INTEGER NOT NULL DEFAULT 0, description TEXT, sidecar TEXT,
                meta_version INTEGER NOT NULL DEFAULT 0, added TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_files_sha ON files(sha256);
            CREATE INDEX IF NOT EXISTS ix_files_lib_taken ON files(in_library, taken);
            CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY, value TEXT);
            CREATE TABLE IF NOT EXISTS organise_runs(id INTEGER PRIMARY KEY, started TEXT NOT NULL, finished TEXT,
                library TEXT NOT NULL, manifest TEXT, moved INTEGER DEFAULT 0, errors INTEGER DEFAULT 0, undone INTEGER DEFAULT 0);
            CREATE TABLE IF NOT EXISTS organise_items(run_id INTEGER NOT NULL, action TEXT NOT NULL,
                original_path TEXT NOT NULL, new_path TEXT NOT NULL, status TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_org_items_run ON organise_items(run_id);
            CREATE TABLE IF NOT EXISTS import_runs(id INTEGER PRIMARY KEY, started TEXT NOT NULL, finished TEXT,
                folder TEXT NOT NULL, checked INTEGER DEFAULT 0, present INTEGER DEFAULT 0, similar INTEGER DEFAULT 0,
                missing INTEGER DEFAULT 0, imported INTEGER DEFAULT 0);
            CREATE TABLE IF NOT EXISTS import_items(run_id INTEGER NOT NULL, source_path TEXT NOT NULL,
                status TEXT NOT NULL, library_path TEXT, how TEXT);
            """);
        if (!ColumnExists("files", "hidden")) Exec("ALTER TABLE files ADD COLUMN hidden INTEGER NOT NULL DEFAULT 0");
        EnsureAlbumSchema();
        EnsureAlbumColumns();
    }

    private bool ColumnExists(string table, string column)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"PRAGMA table_info({table})";
            using var r = cmd.ExecuteReader();
            while (r.Read()) if (string.Equals(r.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }

    /// <summary>Writes a consistent copy of the whole database to <paramref name="path"/> (SQLite's online backup).</summary>
    public void SnapshotTo(string path)
    {
        using var dest = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        dest.Open();
        lock (_gate) _conn.BackupDatabase(dest);
    }

    public void Dispose()
    {
        lock (_gate) _conn.Dispose();
    }

    // ---- settings

    public string? GetSetting(string key)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM settings WHERE key=@k";
            cmd.Parameters.AddWithValue("@k", key);
            return cmd.ExecuteScalar() as string;
        }
    }

    public void SetSetting(string key, string? value)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "INSERT INTO settings(key,value) VALUES(@k,@v) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
            cmd.Parameters.AddWithValue("@k", key);
            cmd.Parameters.AddWithValue("@v", (object?)value ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    // ---- files

    private const string Cols = "id,path,in_library,size,mtime,sha256,kind,taken,taken_source,make,model,width,height," +
                                "orientation,lat,lon,duration_ms,dhash,dhash_done,description,sidecar,meta_version,added,hidden";

    private static MediaRecord Map(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        Path = r.GetString(1),
        InLibrary = r.GetInt64(2) != 0,
        Size = r.GetInt64(3),
        MTime = r.GetInt64(4),
        Sha256 = r.GetString(5),
        Kind = r.GetString(6) == "video" ? MediaKind.Video : MediaKind.Image,
        Taken = r.IsDBNull(7) ? null : DateTime.ParseExact(r.GetString(7), "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        TakenSource = r.IsDBNull(8) ? null : r.GetString(8),
        Make = r.IsDBNull(9) ? null : r.GetString(9),
        Model = r.IsDBNull(10) ? null : r.GetString(10),
        Width = r.IsDBNull(11) ? null : r.GetInt32(11),
        Height = r.IsDBNull(12) ? null : r.GetInt32(12),
        Orientation = r.IsDBNull(13) ? null : r.GetInt32(13),
        Lat = r.IsDBNull(14) ? null : r.GetDouble(14),
        Lon = r.IsDBNull(15) ? null : r.GetDouble(15),
        DurationMs = r.IsDBNull(16) ? null : r.GetInt64(16),
        DHash = r.IsDBNull(17) ? null : unchecked((ulong)r.GetInt64(17)),
        DHashDone = r.GetInt64(18) != 0,
        Description = r.IsDBNull(19) ? null : r.GetString(19),
        SidecarPath = r.IsDBNull(20) ? null : r.GetString(20),
        MetaVersion = r.GetInt32(21),
        Added = DateTime.ParseExact(r.GetString(22), "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        Hidden = r.GetInt64(23) != 0,
    };

    private static string Iso(DateTime d) => d.ToString("O", CultureInfo.InvariantCulture);

    public List<MediaRecord> Query(string where, params (string Name, object? Value)[] args)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"SELECT {Cols} FROM files WHERE {where}";
            foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
            using var r = cmd.ExecuteReader();
            var list = new List<MediaRecord>();
            while (r.Read()) list.Add(Map(r));
            return list;
        }
    }

    public MediaRecord? GetByPath(string path) => Query("path=@p", ("@p", path)).FirstOrDefault();

    /// <summary>All rows whose path lies under root (checked case-insensitively on Windows).</summary>
    public List<MediaRecord> GetUnder(string root)
    {
        var prefix = root.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        var escaped = prefix.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return Query("path LIKE @p ESCAPE '\\'", ("@p", escaped + "%"))
            .Where(r => r.Path.StartsWith(prefix, cmp)).ToList();
    }

    public List<MediaRecord> GetLibrary() => Query("in_library=1");

    public List<MediaRecord> GetLibraryRange(DateTime fromInclusive, DateTime toExclusive) =>
        Query("in_library=1 AND taken>=@a AND taken<@b ORDER BY taken, path", ("@a", Iso(fromInclusive)), ("@b", Iso(toExclusive)));

    public List<MediaRecord> GetLibraryUndated() => Query("in_library=1 AND taken IS NULL ORDER BY path");

    public List<MediaRecord> GetBySha(string sha) => Query("sha256=@s", ("@s", sha));

    public List<DayCount> GetDayCounts()
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT substr(taken,1,10), count(*) FROM files WHERE in_library=1 AND taken IS NOT NULL GROUP BY 1 ORDER BY 1";
            using var r = cmd.ExecuteReader();
            var list = new List<DayCount>();
            while (r.Read())
                list.Add(new DayCount(DateOnly.ParseExact(r.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture), r.GetInt32(1)));
            return list;
        }
    }

    public int CountUndated()
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM files WHERE in_library=1 AND taken IS NULL";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    public void Upsert(MediaRecord rec)
    {
        lock (_gate) UpsertNoLock(rec);
    }

    public void UpsertMany(IEnumerable<MediaRecord> recs)
    {
        lock (_gate)
        {
            using var tx = _conn.BeginTransaction();
            foreach (var r in recs) UpsertNoLock(r);
            tx.Commit();
        }
    }

    private void UpsertNoLock(MediaRecord rec)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO files(path,in_library,size,mtime,sha256,kind,taken,taken_source,make,model,width,height,orientation,
                lat,lon,duration_ms,dhash,dhash_done,description,sidecar,meta_version,added)
            VALUES(@path,@lib,@size,@mtime,@sha,@kind,@taken,@src,@make,@model,@w,@h,@ori,@lat,@lon,@dur,@dh,@dhd,@desc,@side,@mv,@added)
            ON CONFLICT(path) DO UPDATE SET in_library=CASE WHEN files.hidden=1 THEN 0 ELSE excluded.in_library END,
                size=excluded.size,mtime=excluded.mtime,
                sha256=excluded.sha256,kind=excluded.kind,taken=excluded.taken,taken_source=excluded.taken_source,
                make=excluded.make,model=excluded.model,width=excluded.width,height=excluded.height,
                orientation=excluded.orientation,lat=excluded.lat,lon=excluded.lon,duration_ms=excluded.duration_ms,
                dhash=excluded.dhash,dhash_done=excluded.dhash_done,description=excluded.description,
                sidecar=excluded.sidecar,meta_version=excluded.meta_version
            """;
        var p = cmd.Parameters;
        p.AddWithValue("@path", rec.Path);
        p.AddWithValue("@lib", rec.InLibrary ? 1 : 0);
        p.AddWithValue("@size", rec.Size);
        p.AddWithValue("@mtime", rec.MTime);
        p.AddWithValue("@sha", rec.Sha256);
        p.AddWithValue("@kind", rec.Kind == MediaKind.Video ? "video" : "image");
        p.AddWithValue("@taken", rec.Taken is { } t ? Iso(t) : DBNull.Value);
        p.AddWithValue("@src", (object?)rec.TakenSource ?? DBNull.Value);
        p.AddWithValue("@make", (object?)rec.Make ?? DBNull.Value);
        p.AddWithValue("@model", (object?)rec.Model ?? DBNull.Value);
        p.AddWithValue("@w", (object?)rec.Width ?? DBNull.Value);
        p.AddWithValue("@h", (object?)rec.Height ?? DBNull.Value);
        p.AddWithValue("@ori", (object?)rec.Orientation ?? DBNull.Value);
        p.AddWithValue("@lat", (object?)rec.Lat ?? DBNull.Value);
        p.AddWithValue("@lon", (object?)rec.Lon ?? DBNull.Value);
        p.AddWithValue("@dur", (object?)rec.DurationMs ?? DBNull.Value);
        p.AddWithValue("@dh", rec.DHash is { } dh ? unchecked((long)dh) : DBNull.Value);
        p.AddWithValue("@dhd", rec.DHashDone ? 1 : 0);
        p.AddWithValue("@desc", (object?)rec.Description ?? DBNull.Value);
        p.AddWithValue("@side", (object?)rec.SidecarPath ?? DBNull.Value);
        p.AddWithValue("@mv", rec.MetaVersion);
        p.AddWithValue("@added", Iso(rec.Added == default ? DateTime.Now : rec.Added));
        cmd.ExecuteNonQuery();
        if (rec.Id == 0)
        {
            using var idCmd = _conn.CreateCommand();
            idCmd.CommandText = "SELECT id FROM files WHERE path=@p";
            idCmd.Parameters.AddWithValue("@p", rec.Path);
            rec.Id = Convert.ToInt64(idCmd.ExecuteScalar());
        }
    }

    public void UpdateDHash(string path, ulong? hash)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE files SET dhash=@h, dhash_done=1 WHERE path=@p";
            cmd.Parameters.AddWithValue("@h", hash is { } h ? unchecked((long)h) : DBNull.Value);
            cmd.Parameters.AddWithValue("@p", path);
            cmd.ExecuteNonQuery();
        }
    }

    public void UpdateDHashMany(IEnumerable<(string Path, ulong? Hash)> items)
    {
        lock (_gate)
        {
            using var tx = _conn.BeginTransaction();
            foreach (var (path, hash) in items)
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "UPDATE files SET dhash=@h, dhash_done=1 WHERE path=@p";
                cmd.Parameters.AddWithValue("@h", hash is { } h ? unchecked((long)h) : DBNull.Value);
                cmd.Parameters.AddWithValue("@p", path);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>Mark rows so the next scan re-reads their metadata (size, orientation, kind) without re-hashing.</summary>
    public void ResetMetadata(IEnumerable<long> ids)
    {
        lock (_gate)
        {
            using var tx = _conn.BeginTransaction();
            using var cmd = _conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE files SET meta_version=0 WHERE id=@id";
            var p = cmd.Parameters.Add("@id", Microsoft.Data.Sqlite.SqliteType.Integer);
            foreach (var id in ids) { p.Value = id; cmd.ExecuteNonQuery(); }
            tx.Commit();
        }
    }

    /// <summary>Forget the visual fingerprint so it is recomputed, e.g. after the preview decoder improved.</summary>
    public void ResetDHash(IEnumerable<long> ids)
    {
        lock (_gate)
        {
            using var tx = _conn.BeginTransaction();
            foreach (var id in ids)
            {
                using var cmd = _conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE files SET dhash=NULL, dhash_done=0 WHERE id=@id";
                cmd.Parameters.AddWithValue("@id", id);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public void UpdatePath(string oldPath, string newPath, bool? inLibrary = null)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = inLibrary is null
                ? "UPDATE files SET path=@n WHERE path=@o"
                : "UPDATE files SET path=@n, in_library=@l WHERE path=@o";
            cmd.Parameters.AddWithValue("@n", newPath);
            cmd.Parameters.AddWithValue("@o", oldPath);
            if (inLibrary is { } l) cmd.Parameters.AddWithValue("@l", l ? 1 : 0);
            cmd.ExecuteNonQuery();
        }
    }

    public void DeleteByPath(string path)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM files WHERE path=@p";
            cmd.Parameters.AddWithValue("@p", path);
            cmd.ExecuteNonQuery();
        }
    }

    public void DeleteMany(IEnumerable<string> paths)
    {
        lock (_gate)
        {
            using var tx = _conn.BeginTransaction();
            foreach (var p in paths)
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "DELETE FROM files WHERE path=@p";
                cmd.Parameters.AddWithValue("@p", p);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    // ---- removing items

    /// <summary>Soft delete: drops the rows out of the library and remembers them so they are never re-imported.</summary>
    public void Hide(IEnumerable<long> ids)
    {
        lock (_gate)
        {
            using var tx = _conn.BeginTransaction();
            foreach (var id in ids)
            {
                using var cmd = _conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE files SET in_library=0, hidden=1 WHERE id=@id";
                cmd.Parameters.AddWithValue("@id", id);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>Puts every hidden file that still exists on disk back into the library. Returns how many.</summary>
    public int RestoreHidden()
    {
        var rows = Query("hidden=1").Where(r => File.Exists(r.Path)).ToList();
        lock (_gate)
        {
            using var tx = _conn.BeginTransaction();
            foreach (var r in rows)
            {
                using var cmd = _conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE files SET in_library=1, hidden=0 WHERE id=@id";
                cmd.Parameters.AddWithValue("@id", r.Id);
                cmd.ExecuteNonQuery();
            }
            using (var gone = _conn.CreateCommand())
            {
                gone.Transaction = tx;
                gone.CommandText = "DELETE FROM files WHERE hidden=1 AND in_library=0 AND id NOT IN (" + string.Join(",", rows.Select(r => r.Id)) + ")";
                if (rows.Count == 0) gone.CommandText = "DELETE FROM files WHERE hidden=1";
                gone.ExecuteNonQuery();
            }
            tx.Commit();
        }
        return rows.Count;
    }

    public int CountHidden() => (int)Count("hidden=1");

    public HashSet<string> GetHiddenShas()
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT sha256 FROM files WHERE hidden=1";
            using var r = cmd.ExecuteReader();
            var set = new HashSet<string>();
            while (r.Read()) set.Add(r.GetString(0));
            return set;
        }
    }

    public void DeleteByIds(IEnumerable<long> ids)
    {
        lock (_gate)
        {
            using var tx = _conn.BeginTransaction();
            foreach (var id in ids)
            {
                using var cmd = _conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM files WHERE id=@id";
                cmd.Parameters.AddWithValue("@id", id);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>Marks rows under root as in-library (or not) in bulk, e.g. after the library path changes.</summary>
    public void SetInLibraryUnder(string root, bool inLibrary)
    {
        var rows = GetUnder(root);
        lock (_gate)
        {
            using var tx = _conn.BeginTransaction();
            foreach (var r in rows)
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "UPDATE files SET in_library=@l WHERE id=@id";
                cmd.Parameters.AddWithValue("@l", inLibrary ? 1 : 0);
                cmd.Parameters.AddWithValue("@id", r.Id);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    // ---- organise runs

    public long BeginOrganiseRun(string library)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "INSERT INTO organise_runs(started,library) VALUES(@s,@l); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("@s", Iso(DateTime.Now));
            cmd.Parameters.AddWithValue("@l", library);
            return Convert.ToInt64(cmd.ExecuteScalar());
        }
    }

    public void AddOrganiseItems(long runId, IEnumerable<(string Action, string Original, string New, string Status)> items)
    {
        lock (_gate)
        {
            using var tx = _conn.BeginTransaction();
            foreach (var (a, o, n, s) in items)
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "INSERT INTO organise_items(run_id,action,original_path,new_path,status) VALUES(@r,@a,@o,@n,@s)";
                cmd.Parameters.AddWithValue("@r", runId);
                cmd.Parameters.AddWithValue("@a", a);
                cmd.Parameters.AddWithValue("@o", o);
                cmd.Parameters.AddWithValue("@n", n);
                cmd.Parameters.AddWithValue("@s", s);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public void FinishOrganiseRun(long runId, string manifest, int moved, int errors)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE organise_runs SET finished=@f, manifest=@m, moved=@mv, errors=@e WHERE id=@id";
            cmd.Parameters.AddWithValue("@f", Iso(DateTime.Now));
            cmd.Parameters.AddWithValue("@m", manifest);
            cmd.Parameters.AddWithValue("@mv", moved);
            cmd.Parameters.AddWithValue("@e", errors);
            cmd.Parameters.AddWithValue("@id", runId);
            cmd.ExecuteNonQuery();
        }
    }

    public sealed record OrganiseRun(long Id, DateTime Started, DateTime? Finished, string Library, string? Manifest, int Moved, int Errors, bool Undone);

    public List<OrganiseRun> GetOrganiseRuns()
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT id,started,finished,library,manifest,moved,errors,undone FROM organise_runs ORDER BY id DESC";
            using var r = cmd.ExecuteReader();
            var list = new List<OrganiseRun>();
            while (r.Read())
                list.Add(new OrganiseRun(r.GetInt64(0),
                    DateTime.Parse(r.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    r.IsDBNull(2) ? null : DateTime.Parse(r.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetInt32(5), r.GetInt32(6), r.GetInt64(7) != 0));
            return list;
        }
    }

    public List<(string Action, string Original, string New, string Status)> GetOrganiseItems(long runId)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT action,original_path,new_path,status FROM organise_items WHERE run_id=@r ORDER BY rowid";
            cmd.Parameters.AddWithValue("@r", runId);
            using var r = cmd.ExecuteReader();
            var list = new List<(string, string, string, string)>();
            while (r.Read()) list.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)));
            return list;
        }
    }

    public void MarkOrganiseUndone(long runId)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE organise_runs SET undone=1 WHERE id=@id";
            cmd.Parameters.AddWithValue("@id", runId);
            cmd.ExecuteNonQuery();
        }
    }

    // ---- import runs

    public long BeginImportRun(string folder)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "INSERT INTO import_runs(started,folder) VALUES(@s,@f); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("@s", Iso(DateTime.Now));
            cmd.Parameters.AddWithValue("@f", folder);
            return Convert.ToInt64(cmd.ExecuteScalar());
        }
    }

    public void AddImportItems(long runId, IEnumerable<(string Source, string Status, string? LibraryPath, string? How)> items)
    {
        lock (_gate)
        {
            using var tx = _conn.BeginTransaction();
            foreach (var (s, st, lp, how) in items)
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "INSERT INTO import_items(run_id,source_path,status,library_path,how) VALUES(@r,@s,@st,@lp,@h)";
                cmd.Parameters.AddWithValue("@r", runId);
                cmd.Parameters.AddWithValue("@s", s);
                cmd.Parameters.AddWithValue("@st", st);
                cmd.Parameters.AddWithValue("@lp", (object?)lp ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@h", (object?)how ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public void FinishImportRun(long runId, int checkedCount, int present, int similar, int missing, int imported)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE import_runs SET finished=@f, checked=@c, present=@p, similar=@s, missing=@m, imported=@i WHERE id=@id";
            cmd.Parameters.AddWithValue("@f", Iso(DateTime.Now));
            cmd.Parameters.AddWithValue("@c", checkedCount);
            cmd.Parameters.AddWithValue("@p", present);
            cmd.Parameters.AddWithValue("@s", similar);
            cmd.Parameters.AddWithValue("@m", missing);
            cmd.Parameters.AddWithValue("@i", imported);
            cmd.Parameters.AddWithValue("@id", runId);
            cmd.ExecuteNonQuery();
        }
    }

    public void Exec(string sql)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Runs work directly on the connection while holding the lock, for extension methods in other projects.</summary>
    public T Run<T>(Func<SqliteConnection, T> work)
    {
        lock (_gate) return work(_conn);
    }

    public void Run(Action<SqliteConnection> work)
    {
        lock (_gate) work(_conn);
    }

    public long Count(string where = "1=1")
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"SELECT count(*) FROM files WHERE {where}";
            return Convert.ToInt64(cmd.ExecuteScalar());
        }
    }
}
