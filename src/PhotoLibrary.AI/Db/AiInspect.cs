using PhotoLibrary.Core.Db;

namespace PhotoLibrary.AI.Db;

/// <summary>One AI label on a photo: which model gave it, and how confident it was.</summary>
public sealed record PhotoTag(string Model, string Tag, float Score);

/// <summary>Everything the AI tables hold about one photo, for the Advanced › Database browser.</summary>
public sealed record PhotoAiData(
    IReadOnlyList<PhotoTag> Tags,
    IReadOnlyList<(string Model, int Dim)> Embeddings,
    IReadOnlyList<(string Model, int Frames)> VideoFrames,
    (int Version, float Brightness, float Contrast, float Sharpness)? Quality,
    IReadOnlyList<string> Events);

/// <summary>Read-only queries over the AI tables, used by the database browser.</summary>
public static class AiInspect
{
    public static PhotoAiData AiDataFor(this LibraryDb db, long fileId) => db.Run(conn =>
    {
        var tags = new List<PhotoTag>();
        var embeddings = new List<(string, int)>();
        var frames = new List<(string, int)>();
        (int, float, float, float)? quality = null;
        var events = new List<string>();
        using (var c = conn.CreateCommand())
        {
            c.CommandText = "SELECT model, tag, score FROM ai_tags WHERE file_id=@f ORDER BY model, score DESC";
            c.Parameters.AddWithValue("@f", fileId);
            using var r = c.ExecuteReader();
            while (r.Read()) tags.Add(new PhotoTag(r.GetString(0), r.GetString(1), r.GetFloat(2)));
        }
        using (var c = conn.CreateCommand())
        {
            c.CommandText = "SELECT model, dim FROM ai_embeddings WHERE file_id=@f ORDER BY model";
            c.Parameters.AddWithValue("@f", fileId);
            using var r = c.ExecuteReader();
            while (r.Read()) embeddings.Add((r.GetString(0), r.GetInt32(1)));
        }
        using (var c = conn.CreateCommand())
        {
            c.CommandText = "SELECT model, count(*) FROM ai_video_frames WHERE file_id=@f GROUP BY model ORDER BY model";
            c.Parameters.AddWithValue("@f", fileId);
            using var r = c.ExecuteReader();
            while (r.Read()) frames.Add((r.GetString(0), r.GetInt32(1)));
        }
        using (var c = conn.CreateCommand())
        {
            c.CommandText = "SELECT version, brightness, contrast, sharpness FROM ai_quality WHERE file_id=@f";
            c.Parameters.AddWithValue("@f", fileId);
            using var r = c.ExecuteReader();
            if (r.Read()) quality = (r.GetInt32(0), r.GetFloat(1), r.GetFloat(2), r.GetFloat(3));
        }
        using (var c = conn.CreateCommand())
        {
            c.CommandText = "SELECT e.name FROM ai_events e JOIN ai_event_members m ON m.event_id=e.id WHERE m.file_id=@f ORDER BY e.start";
            c.Parameters.AddWithValue("@f", fileId);
            using var r = c.ExecuteReader();
            while (r.Read()) events.Add(r.GetString(0));
        }
        return new PhotoAiData(tags, embeddings, frames, quality, events);
    });

    /// <summary>The models that have applied tags, most tags first.</summary>
    public static List<string> TagModels(this LibraryDb db) => db.Run(conn =>
    {
        using var c = conn.CreateCommand();
        c.CommandText = "SELECT model FROM ai_tags GROUP BY model ORDER BY count(*) DESC";
        using var r = c.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    });

    /// <summary>In-library photos carrying a tag from a model, most confident first.</summary>
    public static List<(long FileId, float Score)> FilesWithTag(this LibraryDb db, string model, string tag, int limit) => db.Run(conn =>
    {
        using var c = conn.CreateCommand();
        c.CommandText = """
            SELECT t.file_id, t.score FROM ai_tags t JOIN files f ON f.id=t.file_id
            WHERE t.model=@m AND t.tag=@t AND f.in_library=1 ORDER BY t.score DESC LIMIT @n
            """;
        c.Parameters.AddWithValue("@m", model);
        c.Parameters.AddWithValue("@t", tag);
        c.Parameters.AddWithValue("@n", limit);
        using var r = c.ExecuteReader();
        var list = new List<(long, float)>();
        while (r.Read()) list.Add((r.GetInt64(0), r.GetFloat(1)));
        return list;
    });
}
