using System.Collections.Concurrent;
using PhotoLibrary.Core.Db;
using PhotoLibrary.Core.Hashing;
using PhotoLibrary.Core.Metadata;

namespace PhotoLibrary.Core.Scanning;

public readonly record struct ScanProgress(string Phase, int Done, int Total, string? Current = null);

public sealed class ScanResult
{
    public required List<MediaRecord> Records { get; init; }
    /// <summary>Non-media files seen, by extension (or full name when there is none), for the report.</summary>
    public required Dictionary<string, int> OtherFiles { get; init; }
}

/// <summary>
/// Walks a tree, hashes new/changed media files and reads their metadata, reusing cached rows whose size and
/// mtime are unchanged. Re-reads metadata (without re-hashing) for rows written by an older reader version.
/// </summary>
public sealed class Scanner(LibraryDb db)
{
    public int Parallelism { get; init; } = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);

    public static IEnumerable<string> WalkMedia(string root, IReadOnlySet<string>? excludeTopLevel, Dictionary<string, int>? other = null)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        var first = true;
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            IEnumerable<string> subdirs, files;
            try
            {
                subdirs = Directory.EnumerateDirectories(dir);
                files = Directory.EnumerateFiles(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var d in subdirs)
            {
                if (first && excludeTopLevel is not null && excludeTopLevel.Contains(Path.GetFileName(d))) continue;
                stack.Push(d);
            }
            first = false;
            foreach (var f in files)
            {
                if (MediaKinds.IsMedia(f)) yield return f;
                else if (other is not null)
                {
                    var name = Path.GetFileName(f);
                    if (MediaKinds.IgnoredNames.Contains(name)) continue;
                    var key = Path.GetExtension(f);
                    if (key.Length == 0) key = name;
                    other[key] = other.GetValueOrDefault(key) + 1;
                }
            }
        }
    }

    public async Task<ScanResult> IndexTreeAsync(string root, IReadOnlySet<string>? excludeTopLevel, bool inLibrary,
        IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        root = Path.GetFullPath(root);
        progress?.Report(new ScanProgress("Listing files", 0, 0));
        var other = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var paths = await Task.Run(() => WalkMedia(root, excludeTopLevel, other).ToList(), ct);

        var cached = db.GetUnder(root).ToDictionary(r => r.Path, r => r, PathComparer);
        var records = new List<MediaRecord>(paths.Count);
        var toHash = new List<string>();
        var toReread = new List<MediaRecord>();
        foreach (var p in paths)
        {
            ct.ThrowIfCancellationRequested();
            FileInfo fi;
            try { fi = new FileInfo(p); } catch (IOException) { continue; }
            if (cached.Remove(p, out var row) && row.Hidden) continue; // removed by the user: stays out of the library
            if (row is not null && row.Size == fi.Length && row.MTime == fi.LastWriteTimeUtc.Ticks)
            {
                if (row.InLibrary != inLibrary) { row.InLibrary = inLibrary; toReread.Add(row); }
                else if (row.MetaVersion < MetadataReader.Version) toReread.Add(row);
                else records.Add(row);
            }
            else toHash.Add(p);
        }

        // Rows for files that vanished (but not ones under excluded folders we didn't walk this time).
        var gone = cached.Keys.Where(k =>
        {
            var rel = Path.GetRelativePath(root, k).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return excludeTopLevel is null || rel.Length == 0 || !excludeTopLevel.Contains(rel[0]);
        }).ToList();
        if (gone.Count > 0) db.DeleteMany(gone);

        if (toReread.Count > 0)
        {
            var done = 0;
            var batch = new ConcurrentBag<MediaRecord>();
            await Parallel.ForEachAsync(toReread, new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct }, (row, _) =>
            {
                row.Apply(MetadataReader.Read(row.Path));
                batch.Add(row);
                var n = Interlocked.Increment(ref done);
                if (n % 50 == 0 || n == toReread.Count) progress?.Report(new ScanProgress("Reading metadata", n, toReread.Count));
                return ValueTask.CompletedTask;
            });
            db.UpsertMany(batch);
            records.AddRange(batch);
        }

        if (toHash.Count > 0)
        {
            var done = 0;
            var pending = new ConcurrentQueue<MediaRecord>();
            var flushGate = new SemaphoreSlim(1, 1);

            async Task FlushAsync(bool force)
            {
                if (!force && pending.Count < 100) return;
                await flushGate.WaitAsync(ct);
                try
                {
                    var batch = new List<MediaRecord>();
                    while (pending.TryDequeue(out var r)) batch.Add(r);
                    if (batch.Count > 0)
                    {
                        db.UpsertMany(batch);
                        lock (records) records.AddRange(batch);
                    }
                }
                finally { flushGate.Release(); }
            }

            await Parallel.ForEachAsync(toHash, new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct }, async (p, token) =>
            {
                var rec = ScanFile(p, inLibrary, token);
                if (rec is not null) pending.Enqueue(rec);
                var n = Interlocked.Increment(ref done);
                if (n % 20 == 0 || n == toHash.Count)
                    progress?.Report(new ScanProgress("Hashing new files", n, toHash.Count, Path.GetFileName(p)));
                await FlushAsync(force: false);
            });
            await FlushAsync(force: true);
        }

        progress?.Report(new ScanProgress("Done", records.Count, records.Count));
        return new ScanResult { Records = records, OtherFiles = other };
    }

    public static MediaRecord? ScanFile(string path, bool inLibrary, CancellationToken ct)
    {
        try
        {
            var fi = new FileInfo(path);
            var sha = FileHasher.Sha256Hex(path, ct);
            var rec = new MediaRecord
            {
                Path = path, InLibrary = inLibrary, Size = fi.Length, MTime = fi.LastWriteTimeUtc.Ticks, Sha256 = sha,
                Added = DateTime.Now,
            };
            rec.Apply(MetadataReader.Read(path));
            return rec;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Computes visual fingerprints for image rows that don't have one yet.</summary>
    public async Task EnsureDHashAsync(IEnumerable<MediaRecord> recs, IProgress<ScanProgress>? progress, CancellationToken ct, string phase = "Fingerprinting")
    {
        var todo = recs.Where(r => r.IsImage && !r.DHashDone).ToList();
        if (todo.Count == 0) return;
        var done = 0;
        var results = new ConcurrentBag<(string, ulong?)>();
        await Parallel.ForEachAsync(todo, new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct }, (r, _) =>
        {
            var h = DHash.Compute(r.Path);
            r.DHash = h;
            r.DHashDone = true;
            results.Add((r.Path, h));
            var n = Interlocked.Increment(ref done);
            if (n % 25 == 0 || n == todo.Count) progress?.Report(new ScanProgress(phase, n, todo.Count, r.FileName));
            if (results.Count >= 200)
            {
                var batch = new List<(string, ulong?)>();
                while (results.TryTake(out var x)) batch.Add(x);
                db.UpdateDHashMany(batch);
            }
            return ValueTask.CompletedTask;
        });
        var rest = new List<(string, ulong?)>();
        while (results.TryTake(out var x)) rest.Add(x);
        if (rest.Count > 0) db.UpdateDHashMany(rest);
    }

    public static StringComparer PathComparer { get; } =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
