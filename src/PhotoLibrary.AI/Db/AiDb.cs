using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using PhotoLibrary.Core.Db;

namespace PhotoLibrary.AI.Db;

public sealed record TagCount(string Tag, int Count);
public sealed record IndexStats(string Model, int Items, int Dimension, long VectorBytes, int Tags);
public sealed record PhotoEvent(long Id, string Name, DateTime Start, DateTime End, int Count);

/// <summary>
/// AI tables in the same SQLite file as the index, keyed by files.id so a photo keeps its embedding when
/// Organise moves it. Every method runs under LibraryDb's connection lock via Run().
/// </summary>
public static class AiDb
{
    private const string SchemaKey = "ai_schema";
    private const string SchemaVersion = "3";
    private const string V2ModelId = "clip-vit-base-patch32-q8";

    public static void EnsureAiSchema(this LibraryDb db)
    {
        var version = db.GetSetting(SchemaKey);
        if (version == "2")
        {
            // v3 keys embeddings and tags by model too, so several models can be indexed side by side.
            db.Exec($"""
                ALTER TABLE ai_embeddings RENAME TO ai_embeddings_v2;
                CREATE TABLE ai_embeddings(
                    file_id INTEGER NOT NULL, model TEXT NOT NULL, dim INTEGER NOT NULL, vector BLOB NOT NULL, PRIMARY KEY(file_id, model));
                INSERT INTO ai_embeddings SELECT file_id, model, dim, vector FROM ai_embeddings_v2;
                DROP TABLE ai_embeddings_v2;
                ALTER TABLE ai_tags RENAME TO ai_tags_v2;
                CREATE TABLE ai_tags(
                    file_id INTEGER NOT NULL, model TEXT NOT NULL, tag TEXT NOT NULL, score REAL NOT NULL, PRIMARY KEY(file_id, model, tag));
                INSERT INTO ai_tags SELECT t.file_id, COALESCE(e.model, '{V2ModelId}'), t.tag, t.score
                    FROM ai_tags_v2 t LEFT JOIN ai_embeddings e ON e.file_id = t.file_id;
                DROP TABLE ai_tags_v2;
                """);
        }
        else if (version != SchemaVersion)
        {
            // Earlier schemas were never populated by working code; start clean.
            db.Exec("""
                DROP TABLE IF EXISTS ai_tags; DROP TABLE IF EXISTS ai_embeddings; DROP TABLE IF EXISTS ai_groups;
                DROP TABLE IF EXISTS ai_group_members; DROP TABLE IF EXISTS ai_colors; DROP TABLE IF EXISTS ai_scenes;
                """);
        }
        db.Exec("""
            CREATE TABLE IF NOT EXISTS ai_embeddings(
                file_id INTEGER NOT NULL, model TEXT NOT NULL, dim INTEGER NOT NULL, vector BLOB NOT NULL, PRIMARY KEY(file_id, model));
            CREATE TABLE IF NOT EXISTS ai_tags(
                file_id INTEGER NOT NULL, model TEXT NOT NULL, tag TEXT NOT NULL, score REAL NOT NULL, PRIMARY KEY(file_id, model, tag));
            CREATE INDEX IF NOT EXISTS ix_ai_tags_model_tag ON ai_tags(model, tag);
            CREATE TABLE IF NOT EXISTS ai_events(
                id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, start TEXT NOT NULL, end TEXT NOT NULL, count INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS ai_event_members(
                event_id INTEGER NOT NULL, file_id INTEGER NOT NULL, PRIMARY KEY(event_id, file_id));
            CREATE TABLE IF NOT EXISTS ai_quality(
                file_id INTEGER PRIMARY KEY, version INTEGER NOT NULL, brightness REAL NOT NULL, contrast REAL NOT NULL, sharpness REAL NOT NULL);
            CREATE TABLE IF NOT EXISTS ai_video_frames(
                file_id INTEGER NOT NULL, model TEXT NOT NULL, ms INTEGER NOT NULL, vector BLOB NOT NULL, PRIMARY KEY(file_id, model, ms));
            """);
        db.SetSetting(SchemaKey, SchemaVersion);
    }

    /// <summary>Drop rows for files that no longer exist in the index.</summary>
    public static void PruneOrphans(this LibraryDb db) => db.Exec("""
        DELETE FROM ai_embeddings WHERE file_id NOT IN (SELECT id FROM files);
        DELETE FROM ai_tags WHERE file_id NOT IN (SELECT id FROM files);
        DELETE FROM ai_event_members WHERE file_id NOT IN (SELECT id FROM files);
        DELETE FROM ai_quality WHERE file_id NOT IN (SELECT id FROM files);
        DELETE FROM ai_video_frames WHERE file_id NOT IN (SELECT id FROM files);
        """);

    // ---- video frames: many vectors per video, one per sampled moment

    public static void StoreVideoFrames(this LibraryDb db, string model, long fileId, IReadOnlyList<(int Ms, float[] Vector)> frames) => db.Run(conn =>
    {
        using var tx = conn.BeginTransaction();
        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM ai_video_frames WHERE file_id=@f AND model=@m";
            del.Parameters.AddWithValue("@f", fileId);
            del.Parameters.AddWithValue("@m", model);
            del.ExecuteNonQuery();
        }
        using var c = conn.CreateCommand();
        c.Transaction = tx;
        c.CommandText = "INSERT OR REPLACE INTO ai_video_frames(file_id, model, ms, vector) VALUES(@f, @m, @t, @v)";
        c.Parameters.AddWithValue("@f", fileId);
        c.Parameters.AddWithValue("@m", model);
        var pt = c.Parameters.Add("@t", SqliteType.Integer);
        var pv = c.Parameters.Add("@v", SqliteType.Blob);
        foreach (var (ms, v) in frames)
        {
            pt.Value = ms;
            pv.Value = MemoryMarshal.AsBytes(v.AsSpan()).ToArray();
            c.ExecuteNonQuery();
        }
        tx.Commit();
    });

    /// <summary>Frames of videos that are in the library.</summary>
    public static List<(long FileId, int Ms, float[] Vector)> LoadVideoFrames(this LibraryDb db, string model) => db.Run(conn =>
    {
        using var c = conn.CreateCommand();
        c.CommandText = "SELECT v.file_id, v.ms, v.vector FROM ai_video_frames v JOIN files f ON f.id=v.file_id WHERE v.model=@m AND f.in_library=1 ORDER BY v.file_id, v.ms";
        c.Parameters.AddWithValue("@m", model);
        using var r = c.ExecuteReader();
        var list = new List<(long, int, float[])>();
        while (r.Read()) list.Add((r.GetInt64(0), r.GetInt32(1), MemoryMarshal.Cast<byte, float>((byte[])r.GetValue(2)).ToArray()));
        return list;
    });

    public static HashSet<long> VideoFrameFileIds(this LibraryDb db, string model) => db.Run(conn =>
    {
        using var c = conn.CreateCommand();
        c.CommandText = "SELECT DISTINCT file_id FROM ai_video_frames WHERE model=@m";
        c.Parameters.AddWithValue("@m", model);
        using var r = c.ExecuteReader();
        var set = new HashSet<long>();
        while (r.Read()) set.Add(r.GetInt64(0));
        return set;
    });

    /// <summary>Drop every video's vectors for one model, so the next index samples them again.</summary>
    public static int ForgetVideoIndex(this LibraryDb db, string model) => db.Run(conn =>
    {
        using var c = conn.CreateCommand();
        c.CommandText = """
            DELETE FROM ai_video_frames WHERE model=@m;
            DELETE FROM ai_tags WHERE model=@m AND file_id IN (SELECT id FROM files WHERE kind='video');
            DELETE FROM ai_embeddings WHERE model=@m AND file_id IN (SELECT id FROM files WHERE kind='video');
            """;
        c.Parameters.AddWithValue("@m", model);
        return c.ExecuteNonQuery();
    });

    /// <summary>Size and count of every model's stored vectors, including rows for files no longer in the library.</summary>
    public static List<IndexStats> IndexStats(this LibraryDb db) => db.Run(conn =>
    {
        using var c = conn.CreateCommand();
        c.CommandText = """
            SELECT e.model, COUNT(*), MAX(e.dim), SUM(LENGTH(e.vector)),
                   (SELECT COUNT(*) FROM ai_tags t WHERE t.model = e.model)
            FROM ai_embeddings e GROUP BY e.model ORDER BY e.model
            """;
        using var r = c.ExecuteReader();
        var list = new List<IndexStats>();
        while (r.Read()) list.Add(new IndexStats(r.GetString(0), r.GetInt32(1), r.GetInt32(2), r.GetInt64(3), r.GetInt32(4)));
        return list;
    });

    /// <summary>Drop every model's rows for the given files so they are analysed again from fresh thumbnails.</summary>
    public static void ForgetAnalysis(this LibraryDb db, IEnumerable<long> fileIds) => db.Run(conn =>
    {
        using var tx = conn.BeginTransaction();
        using var c = conn.CreateCommand();
        c.Transaction = tx;
        c.CommandText = "DELETE FROM ai_embeddings WHERE file_id=@f; DELETE FROM ai_tags WHERE file_id=@f; DELETE FROM ai_quality WHERE file_id=@f; DELETE FROM ai_video_frames WHERE file_id=@f";
        var p = c.Parameters.Add("@f", SqliteType.Integer);
        foreach (var id in fileIds) { p.Value = id; c.ExecuteNonQuery(); }
        tx.Commit();
    });

    /// <summary>Drop one model's vectors and tags, e.g. after the model itself was removed.</summary>
    public static void DeleteIndex(this LibraryDb db, string model) => db.Run(conn =>
    {
        using var c = conn.CreateCommand();
        c.CommandText = "DELETE FROM ai_embeddings WHERE model=@m; DELETE FROM ai_tags WHERE model=@m; DELETE FROM ai_video_frames WHERE model=@m";
        c.Parameters.AddWithValue("@m", model);
        c.ExecuteNonQuery();
    });

    // ---- quality measurements

    public static HashSet<long> QualityDone(this LibraryDb db, int version) => db.Run(conn =>
    {
        using var c = conn.CreateCommand();
        c.CommandText = "SELECT file_id FROM ai_quality WHERE version=@v";
        c.Parameters.AddWithValue("@v", version);
        using var r = c.ExecuteReader();
        var set = new HashSet<long>();
        while (r.Read()) set.Add(r.GetInt64(0));
        return set;
    });

    public static void StoreQuality(this LibraryDb db, int version, IReadOnlyList<(long FileId, float Brightness, float Contrast, float Sharpness)> rows) => db.Run(conn =>
    {
        using var tx = conn.BeginTransaction();
        using var c = conn.CreateCommand();
        c.Transaction = tx;
        c.CommandText = "INSERT OR REPLACE INTO ai_quality(file_id, version, brightness, contrast, sharpness) VALUES(@f, @v, @b, @c, @s)";
        var pf = c.Parameters.Add("@f", SqliteType.Integer);
        c.Parameters.AddWithValue("@v", version);
        var pb = c.Parameters.Add("@b", SqliteType.Real);
        var pc = c.Parameters.Add("@c", SqliteType.Real);
        var ps = c.Parameters.Add("@s", SqliteType.Real);
        foreach (var (id, b, con, s) in rows)
        {
            pf.Value = id; pb.Value = b; pc.Value = con; ps.Value = s;
            c.ExecuteNonQuery();
        }
        tx.Commit();
    });

    /// <summary>Measurements for in-library files at the given version.</summary>
    public static List<(long FileId, float Brightness, float Contrast, float Sharpness)> LoadQuality(this LibraryDb db, int version) => db.Run(conn =>
    {
        using var c = conn.CreateCommand();
        c.CommandText = "SELECT q.file_id, q.brightness, q.contrast, q.sharpness FROM ai_quality q JOIN files f ON f.id=q.file_id WHERE q.version=@v AND f.in_library=1";
        c.Parameters.AddWithValue("@v", version);
        using var r = c.ExecuteReader();
        var list = new List<(long, float, float, float)>();
        while (r.Read()) list.Add((r.GetInt64(0), r.GetFloat(1), r.GetFloat(2), r.GetFloat(3)));
        return list;
    });

    public static int CountQuality(this LibraryDb db, int version) => db.Run(conn =>
    {
        using var c = conn.CreateCommand();
        c.CommandText = "SELECT COUNT(*) FROM ai_quality q JOIN files f ON f.id=q.file_id WHERE q.version=@v AND f.in_library=1";
        c.Parameters.AddWithValue("@v", version);
        return Convert.ToInt32(c.ExecuteScalar());
    });

    public static HashSet<long> EmbeddedFileIds(this LibraryDb db, string model) => db.Run(conn =>
    {
        using var c = conn.CreateCommand();
        c.CommandText = "SELECT file_id FROM ai_embeddings WHERE model=@m";
        c.Parameters.AddWithValue("@m", model);
        using var r = c.ExecuteReader();
        var set = new HashSet<long>();
        while (r.Read()) set.Add(r.GetInt64(0));
        return set;
    });

    public static int CountEmbedded(this LibraryDb db, string model) => db.Run(conn =>
    {
        using var c = conn.CreateCommand();
        c.CommandText = "SELECT COUNT(*) FROM ai_embeddings e JOIN files f ON f.id=e.file_id WHERE e.model=@m AND f.in_library=1";
        c.Parameters.AddWithValue("@m", model);
        return Convert.ToInt32(c.ExecuteScalar());
    });

    /// <summary>Store embeddings and tags for a batch in one transaction.</summary>
    public static void StoreBatch(this LibraryDb db, string model,
        IReadOnlyList<(long FileId, float[] Vector, IReadOnlyList<(string Tag, float Score)> Tags)> items) => db.Run(conn =>
    {
        using var tx = conn.BeginTransaction();
        using var emb = conn.CreateCommand();
        emb.Transaction = tx;
        emb.CommandText = "INSERT OR REPLACE INTO ai_embeddings(file_id, model, dim, vector) VALUES(@f, @m, @d, @v)";
        var pf = emb.Parameters.Add("@f", SqliteType.Integer);
        emb.Parameters.AddWithValue("@m", model);
        var pd = emb.Parameters.Add("@d", SqliteType.Integer);
        var pv = emb.Parameters.Add("@v", SqliteType.Blob);
        using var delTags = conn.CreateCommand();
        delTags.Transaction = tx;
        delTags.CommandText = "DELETE FROM ai_tags WHERE file_id=@f AND model=@m";
        var pdf = delTags.Parameters.Add("@f", SqliteType.Integer);
        delTags.Parameters.AddWithValue("@m", model);
        using var insTag = conn.CreateCommand();
        insTag.Transaction = tx;
        insTag.CommandText = "INSERT OR REPLACE INTO ai_tags(file_id, model, tag, score) VALUES(@f, @m, @t, @s)";
        var ptf = insTag.Parameters.Add("@f", SqliteType.Integer);
        insTag.Parameters.AddWithValue("@m", model);
        var ptt = insTag.Parameters.Add("@t", SqliteType.Text);
        var pts = insTag.Parameters.Add("@s", SqliteType.Real);

        foreach (var (fileId, vector, tags) in items)
        {
            pf.Value = fileId;
            pd.Value = vector.Length;
            pv.Value = MemoryMarshal.AsBytes(vector.AsSpan()).ToArray();
            emb.ExecuteNonQuery();
            pdf.Value = fileId;
            delTags.ExecuteNonQuery();
            foreach (var (tag, score) in tags)
            {
                ptf.Value = fileId;
                ptt.Value = tag;
                pts.Value = score;
                insTag.ExecuteNonQuery();
            }
        }
        tx.Commit();
    });

    /// <summary>Every stored vector for the model, for in-library files only.</summary>
    public static List<(long FileId, float[] Vector)> LoadEmbeddings(this LibraryDb db, string model) => db.Run(conn =>
    {
        using var c = conn.CreateCommand();
        c.CommandText = "SELECT e.file_id, e.vector FROM ai_embeddings e JOIN files f ON f.id=e.file_id WHERE e.model=@m AND f.in_library=1";
        c.Parameters.AddWithValue("@m", model);
        using var r = c.ExecuteReader();
        var list = new List<(long, float[])>();
        while (r.Read())
        {
            var bytes = (byte[])r[1];
            list.Add((r.GetInt64(0), MemoryMarshal.Cast<byte, float>(bytes).ToArray()));
        }
        return list;
    });

    public static List<TagCount> TagCounts(this LibraryDb db, string model, int top) => db.Run(conn =>
    {
        using var c = conn.CreateCommand();
        c.CommandText = """
            SELECT t.tag, COUNT(*) FROM ai_tags t JOIN files f ON f.id=t.file_id
            WHERE t.model=@m AND f.in_library=1 GROUP BY t.tag ORDER BY COUNT(*) DESC, t.tag LIMIT @n
            """;
        c.Parameters.AddWithValue("@m", model);
        c.Parameters.AddWithValue("@n", top);
        using var r = c.ExecuteReader();
        var list = new List<TagCount>();
        while (r.Read()) list.Add(new TagCount(r.GetString(0), r.GetInt32(1)));
        return list;
    });

    public static List<(string Tag, float Score)> TagsFor(this LibraryDb db, string model, long fileId) => db.Run(conn =>
    {
        using var c = conn.CreateCommand();
        c.CommandText = "SELECT tag, score FROM ai_tags WHERE model=@m AND file_id=@f ORDER BY score DESC";
        c.Parameters.AddWithValue("@m", model);
        c.Parameters.AddWithValue("@f", fileId);
        using var r = c.ExecuteReader();
        var list = new List<(string, float)>();
        while (r.Read()) list.Add((r.GetString(0), r.GetFloat(1)));
        return list;
    });

    /// <summary>Highest-scoring tag per in-library file, used to name events.</summary>
    public static Dictionary<long, string> TopTags(this LibraryDb db, string model) => db.Run(conn =>
    {
        using var c = conn.CreateCommand();
        c.CommandText = "SELECT file_id, tag FROM ai_tags WHERE model=@m ORDER BY file_id, score DESC";
        c.Parameters.AddWithValue("@m", model);
        using var r = c.ExecuteReader();
        var result = new Dictionary<long, string>();
        while (r.Read()) result.TryAdd(r.GetInt64(0), r.GetString(1));
        return result;
    });

    public static void ReplaceEvents(this LibraryDb db, IReadOnlyList<(string Name, DateTime Start, DateTime End, IReadOnlyList<long> FileIds)> events) => db.Run(conn =>
    {
        using var tx = conn.BeginTransaction();
        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM ai_event_members; DELETE FROM ai_events;";
            del.ExecuteNonQuery();
        }
        using var ins = conn.CreateCommand();
        ins.Transaction = tx;
        ins.CommandText = "INSERT INTO ai_events(name, start, end, count) VALUES(@n, @s, @e, @c); SELECT last_insert_rowid();";
        var pn = ins.Parameters.Add("@n", SqliteType.Text);
        var ps = ins.Parameters.Add("@s", SqliteType.Text);
        var pe = ins.Parameters.Add("@e", SqliteType.Text);
        var pc = ins.Parameters.Add("@c", SqliteType.Integer);
        using var mem = conn.CreateCommand();
        mem.Transaction = tx;
        mem.CommandText = "INSERT OR IGNORE INTO ai_event_members(event_id, file_id) VALUES(@e, @f)";
        var pme = mem.Parameters.Add("@e", SqliteType.Integer);
        var pmf = mem.Parameters.Add("@f", SqliteType.Integer);
        foreach (var (name, start, end, fileIds) in events)
        {
            pn.Value = name;
            ps.Value = start.ToString("O", CultureInfo.InvariantCulture);
            pe.Value = end.ToString("O", CultureInfo.InvariantCulture);
            pc.Value = fileIds.Count;
            var id = Convert.ToInt64(ins.ExecuteScalar());
            foreach (var f in fileIds)
            {
                pme.Value = id;
                pmf.Value = f;
                mem.ExecuteNonQuery();
            }
        }
        tx.Commit();
    });

    public static List<PhotoEvent> GetEvents(this LibraryDb db) => db.Run(conn =>
    {
        using var c = conn.CreateCommand();
        c.CommandText = "SELECT id, name, start, end, count FROM ai_events ORDER BY start DESC";
        using var r = c.ExecuteReader();
        var list = new List<PhotoEvent>();
        while (r.Read())
            list.Add(new PhotoEvent(r.GetInt64(0), r.GetString(1),
                DateTime.Parse(r.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                DateTime.Parse(r.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), r.GetInt32(4)));
        return list;
    });

    public static List<long> EventMemberIds(this LibraryDb db, long eventId) => db.Run(conn =>
    {
        using var c = conn.CreateCommand();
        c.CommandText = "SELECT file_id FROM ai_event_members WHERE event_id=@e";
        c.Parameters.AddWithValue("@e", eventId);
        using var r = c.ExecuteReader();
        var list = new List<long>();
        while (r.Read()) list.Add(r.GetInt64(0));
        return list;
    });

    /// <summary>Records by id, returned in the order the ids were given.</summary>
    public static List<MediaRecord> GetByIds(this LibraryDb db, IReadOnlyList<long> ids)
    {
        var result = new List<MediaRecord>(ids.Count);
        if (ids.Count == 0) return result;
        var byId = new Dictionary<long, MediaRecord>();
        foreach (var chunk in ids.Chunk(500))
        {
            var placeholders = string.Join(",", chunk.Select((_, i) => "@p" + i));
            foreach (var rec in db.Query($"id IN ({placeholders})", chunk.Select((id, i) => ("@p" + i, (object?)id)).ToArray()))
                byId[rec.Id] = rec;
        }
        foreach (var id in ids) if (byId.TryGetValue(id, out var rec)) result.Add(rec);
        return result;
    }
}
