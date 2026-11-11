using PhotoLibrary.AI.Db;
using PhotoLibrary.Core;
using PhotoLibrary.Core.Db;
using Xunit;

namespace PhotoLibrary.Tests;

/// <summary>The Advanced › Database browser can read everything and change nothing.</summary>
public class DatabaseInspectorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "photolib-inspect-" + Guid.NewGuid().ToString("N"));
    private readonly LibraryDb _db;
    private readonly MediaRecord _photo;

    public DatabaseInspectorTests()
    {
        _db = new LibraryDb(Path.Combine(_dir, "test.sqlite"));
        _db.EnsureAiSchema();
        _photo = new MediaRecord { Path = @"C:\L\a.jpg", Sha256 = new string('a', 64), Size = 1, MTime = 1, Kind = MediaKind.Image, InLibrary = true, Added = DateTime.Now };
        _db.Upsert(_photo);
        _db.Exec($"""
            INSERT INTO ai_tags(file_id, model, tag, score) VALUES({_photo.Id}, 'm1', 'beach', 0.81), ({_photo.Id}, 'm1', 'dog', 0.42);
            INSERT INTO ai_embeddings(file_id, model, dim, vector) VALUES({_photo.Id}, 'm1', 2, x'0000803F00000040');
            """);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void ReadsTablesRowsAndBlobs()
    {
        Assert.Contains(DatabaseInspector.Tables(_db.Path), t => t.Table == "ai_tags" && t.Rows == 2);
        var result = DatabaseInspector.Run(_db.Path, "SELECT tag, score, NULL AS empty FROM ai_tags ORDER BY score DESC");
        Assert.Null(result.Error);
        Assert.Equal(["tag", "score", "empty"], result.Columns);
        Assert.Equal(["beach", "0.81", "NULL"], result.Rows[0]);
        Assert.Equal("‹blob, 8 bytes›", DatabaseInspector.Run(_db.Path, "SELECT vector FROM ai_embeddings").Rows[0][0]);
        Assert.True(DatabaseInspector.Run(_db.Path, "SELECT * FROM ai_tags", maxRows: 1).Truncated);
        Assert.Contains(DatabaseInspector.Row(_db.Path, "files", _photo.Id), f => f.Column == "path" && f.Value == @"C:\L\a.jpg");
    }

    [Theory]
    [InlineData("DELETE FROM ai_tags")]
    [InlineData("UPDATE files SET path='x'")]
    [InlineData("DROP TABLE files")]
    [InlineData("SELECT 1; DELETE FROM ai_tags")]
    public void NeverWrites(string sql)
    {
        var result = DatabaseInspector.Run(_db.Path, sql);
        Assert.Equal(2, _db.Run(c => { using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT count(*) FROM ai_tags"; return Convert.ToInt32(cmd.ExecuteScalar()); }));
        Assert.Single(_db.Query("1=1"));
        if (!sql.StartsWith("SELECT", StringComparison.Ordinal)) Assert.NotNull(result.Error);
    }

    [Fact]
    public void GathersAPhotosAiData()
    {
        var data = _db.AiDataFor(_photo.Id);
        Assert.Equal(["beach", "dog"], data.Tags.Select(t => t.Tag));
        Assert.Equal(("m1", 2), Assert.Single(data.Embeddings));
        Assert.Equal(["m1"], _db.TagModels());
        Assert.Equal(_photo.Id, Assert.Single(_db.FilesWithTag("m1", "dog", 10)).FileId);
    }
}
