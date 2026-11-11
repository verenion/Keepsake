using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace PhotoLibrary.Core.Db;

/// <summary>The outcome of a query run by <see cref="DatabaseInspector"/>; cells are already formatted for display.</summary>
public sealed record QueryResult(IReadOnlyList<string> Columns, IReadOnlyList<string[]> Rows, bool Truncated, TimeSpan Elapsed, string? Error)
{
    public static QueryResult Failed(string error) => new([], [], false, TimeSpan.Zero, error);
}

/// <summary>
/// Read-only access to the library database for the Advanced › Database browser. Every query runs on its own
/// connection opened with <c>Mode=ReadOnly</c> and <c>query_only</c>, so nothing typed into the browser can change
/// the library, whatever the SQL says.
/// </summary>
public static class DatabaseInspector
{
    public const int DefaultMaxRows = 1000;

    private static SqliteConnection Open(string dbPath)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false,
        }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA query_only=ON;";
        cmd.ExecuteNonQuery();
        return conn;
    }

    /// <summary>Every table with its row count, alphabetically.</summary>
    public static List<(string Table, long Rows)> Tables(string dbPath)
    {
        using var conn = Open(dbPath);
        var names = new List<string>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
            using var r = cmd.ExecuteReader();
            while (r.Read()) names.Add(r.GetString(0));
        }
        var list = new List<(string, long)>();
        foreach (var n in names)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT count(*) FROM \"{n.Replace("\"", "\"\"")}\"";
            list.Add((n, Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture)));
        }
        return list;
    }

    /// <summary>Runs one statement and returns up to <paramref name="maxRows"/> rows. Errors come back in the result.</summary>
    public static QueryResult Run(string dbPath, string sql, int maxRows = DefaultMaxRows, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sql)) return QueryResult.Failed("Type a query first.");
        var watch = Stopwatch.StartNew();
        try
        {
            using var conn = Open(dbPath);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            using var reg = ct.Register(() => { try { cmd.Cancel(); } catch (InvalidOperationException) { } });
            using var r = cmd.ExecuteReader();
            var columns = Enumerable.Range(0, r.FieldCount).Select(r.GetName).ToList();
            var rows = new List<string[]>();
            var truncated = false;
            while (r.Read())
            {
                ct.ThrowIfCancellationRequested();
                if (rows.Count >= maxRows) { truncated = true; break; }
                var row = new string[r.FieldCount];
                for (var i = 0; i < row.Length; i++) row[i] = Format(r.GetValue(i));
                rows.Add(row);
            }
            return new QueryResult(columns, rows, truncated, watch.Elapsed, null);
        }
        catch (SqliteException ex)
        {
            return QueryResult.Failed(ex.SqliteErrorCode == 8 /* SQLITE_READONLY */
                ? "The database browser is read-only: only queries that read data can run here."
                : ex.Message);
        }
    }

    /// <summary>One row of a table as column/value pairs, e.g. a file's row by id.</summary>
    public static List<(string Column, string Value)> Row(string dbPath, string table, long id)
    {
        var result = Run(dbPath, $"SELECT * FROM \"{table.Replace("\"", "\"\"")}\" WHERE id={id}", 1);
        if (result.Rows.Count == 0) return [];
        return result.Columns.Select((c, i) => (c, result.Rows[0][i])).ToList();
    }

    public static string Format(object? value) => value switch
    {
        null or DBNull => "NULL",
        byte[] b => $"‹blob, {b.Length:N0} bytes›",
        double d => d.ToString("0.######", CultureInfo.InvariantCulture),
        float f => f.ToString("0.######", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };
}
