using PhotoLibrary.AI.Db;
using PhotoLibrary.AI.Index;
using PhotoLibrary.Core;
using PhotoLibrary.Core.Db;
using Xunit;

namespace PhotoLibrary.Tests;

public class AiDbTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "photolib-ai-" + Guid.NewGuid().ToString("N") + ".sqlite");
    private readonly LibraryDb _db;

    public AiDbTests()
    {
        _db = new LibraryDb(_path);
        _db.EnsureAiSchema();
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_path); } catch (IOException) { }
    }

    private MediaRecord AddFile(string name, bool inLibrary = true, DateTime? taken = null)
    {
        var r = new MediaRecord { Path = @"C:\lib\" + name, Sha256 = name.PadLeft(64, '0'), Size = 1, MTime = 1, Kind = MediaKind.Image, InLibrary = inLibrary, Taken = taken };
        _db.Upsert(r);
        Assert.NotEqual(0, r.Id);
        return r;
    }

    [Fact]
    public void EmbeddingsAndTagsRoundTrip()
    {
        var a = AddFile("a.jpg");
        var b = AddFile("b.jpg");
        var hidden = AddFile("c.jpg", inLibrary: false);
        var va = new float[] { 1, 0, 0, 0 };
        var vb = new float[] { 0, 1, 0, 0 };
        _db.StoreBatch("m1", [(a.Id, va, [("beach", 0.8f), ("sea", 0.1f)]), (b.Id, vb, [("beach", 0.5f)]), (hidden.Id, vb, [("dog", 0.9f)])]);

        Assert.Equal(2, _db.CountEmbedded("m1"));
        Assert.Equal(0, _db.CountEmbedded("other-model"));
        Assert.Equal(new HashSet<long> { a.Id, b.Id, hidden.Id }, _db.EmbeddedFileIds("m1"));

        var rows = _db.LoadEmbeddings("m1").OrderBy(r => r.FileId).ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal(va, rows[0].Vector);
        Assert.Equal(vb, rows[1].Vector);

        var counts = _db.TagCounts("m1", 10);
        Assert.Equal(("beach", 2), (counts[0].Tag, counts[0].Count));
        Assert.Equal(("sea", 1), (counts[1].Tag, counts[1].Count));
        Assert.DoesNotContain(counts, c => c.Tag == "dog");
        Assert.Equal("beach", _db.TopTags("m1")[a.Id]);
        Assert.Equal([("beach", 0.8f), ("sea", 0.1f)], _db.TagsFor("m1", a.Id));

        // Re-storing replaces rather than accumulates.
        _db.StoreBatch("m1", [(a.Id, vb, [("mountains", 1f)])]);
        Assert.Equal([("mountains", 1f)], _db.TagsFor("m1", a.Id));
        Assert.Equal(vb, _db.LoadEmbeddings("m1").Single(r => r.FileId == a.Id).Vector);
    }

    [Fact]
    public void PruneRemovesRowsForDeletedFiles()
    {
        var a = AddFile("a.jpg");
        var b = AddFile("b.jpg");
        _db.StoreBatch("m1", [(a.Id, [1f], [("x", 1f)]), (b.Id, [1f], [("x", 1f)])]);
        _db.DeleteByPath(b.Path);
        _db.PruneOrphans();
        Assert.Equal(new HashSet<long> { a.Id }, _db.EmbeddedFileIds("m1"));
        Assert.Single(_db.TagCounts("m1", 10));
    }

    [Fact]
    public void EventsReplaceAndListMembers()
    {
        var a = AddFile("a.jpg", taken: new DateTime(2020, 3, 1, 10, 0, 0));
        var b = AddFile("b.jpg", taken: new DateTime(2020, 3, 1, 11, 0, 0));
        var c = AddFile("c.jpg", taken: new DateTime(2021, 7, 4, 9, 0, 0));
        _db.ReplaceEvents([("March", a.Taken!.Value, b.Taken!.Value, [a.Id, b.Id]), ("July", c.Taken!.Value, c.Taken.Value, [c.Id])]);

        var events = _db.GetEvents();
        Assert.Equal(["July", "March"], events.Select(e => e.Name));
        Assert.Equal(2, events[1].Count);
        Assert.Equal([a.Id, b.Id], _db.EventMemberIds(events[1].Id).Order());
        Assert.Equal(["a.jpg", "b.jpg"], _db.GetByIds([a.Id, b.Id]).Select(r => r.FileName));
        Assert.Equal(["b.jpg", "a.jpg"], _db.GetByIds([b.Id, a.Id]).Select(r => r.FileName));

        _db.ReplaceEvents([("Only", c.Taken.Value, c.Taken.Value, [c.Id])]);
        var only = Assert.Single(_db.GetEvents());
        Assert.True(only.Id > events[0].Id, "event ids are never reused, so a stale selection can't point at a new event");
        Assert.Empty(_db.EventMemberIds(events[1].Id));
        Assert.Equal([c.Id], _db.EventMemberIds(only.Id));
    }

    [Fact]
    public void SchemaV2RowsSurviveUpgrade()
    {
        var a = AddFile("a.jpg");
        _db.Exec("""
            DROP TABLE ai_tags; DROP TABLE ai_embeddings;
            CREATE TABLE ai_embeddings(file_id INTEGER PRIMARY KEY, model TEXT NOT NULL, dim INTEGER NOT NULL, vector BLOB NOT NULL);
            CREATE TABLE ai_tags(file_id INTEGER NOT NULL, tag TEXT NOT NULL, score REAL NOT NULL, PRIMARY KEY(file_id, tag));
            """);
        _db.SetSetting("ai_schema", "2");
        _db.Run(conn =>
        {
            using var c = conn.CreateCommand();
            c.CommandText = "INSERT INTO ai_embeddings VALUES(@f, 'clip-vit-base-patch32-q8', 1, @v); INSERT INTO ai_tags VALUES(@f, 'beach', 0.5)";
            c.Parameters.AddWithValue("@f", a.Id);
            c.Parameters.AddWithValue("@v", new byte[] { 0, 0, 128, 63 });
            c.ExecuteNonQuery();
        });

        _db.EnsureAiSchema();
        Assert.Equal([1f], _db.LoadEmbeddings("clip-vit-base-patch32-q8").Single().Vector);
        Assert.Equal([("beach", 0.5f)], _db.TagsFor("clip-vit-base-patch32-q8", a.Id));
        // Two models can now hold rows for the same file.
        _db.StoreBatch("other", [(a.Id, [2f], [("sea", 1f)])]);
        Assert.Equal(1, _db.CountEmbedded("clip-vit-base-patch32-q8"));
        Assert.Equal(1, _db.CountEmbedded("other"));
        Assert.Equal([("beach", 0.5f)], _db.TagsFor("clip-vit-base-patch32-q8", a.Id));
    }

    [Fact]
    public void SchemaUpgradeDropsOldTables()
    {
        _db.Exec("CREATE TABLE ai_scenes(x INTEGER)");
        _db.SetSetting("ai_schema", "1");
        _db.EnsureAiSchema();
        Assert.Equal(0L, _db.Run(conn =>
        {
            using var c = conn.CreateCommand();
            c.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name='ai_scenes'";
            return (long)c.ExecuteScalar()!;
        }));
    }

    [Fact]
    public void IndexSearchRanksByCosine()
    {
        var index = new EmbeddingIndex(3, capacity: 1);
        index.Add(1, [1, 0, 0]);
        index.Add(2, [0.8f, 0.6f, 0]);
        index.Add(3, [0, 0, 1]);
        index.Add(2, [0.6f, 0.8f, 0]); // overwrite
        Assert.Equal(3, index.Count);
        Assert.Equal([0.6f, 0.8f, 0], index.Get(2)!);
        Assert.Null(index.Get(99));

        var hits = index.Search([1, 0, 0], top: 10);
        Assert.Equal([1L, 2L, 3L], hits.Select(h => h.FileId));
        Assert.Equal(1f, hits[0].Score, 0.0001);
        Assert.Equal(0.6f, hits[1].Score, 0.0001);

        Assert.Equal([2L], index.Search([1, 0, 0], top: 10, minScore: 0.5f, excludeFileId: 1).Select(h => h.FileId));
        Assert.Single(index.Search([1, 0, 0], top: 1));
    }
}
