using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PhotoLibrary.Core.Db;
using PhotoLibrary.Core.Scanning;

namespace PhotoLibrary.Core.Organise;

public enum OrganiseAction { Move, Duplicate, Undated }

public sealed record OrganiseItem(OrganiseAction Action, MediaRecord Record, string Dest, string? DuplicateOf, string? DateSource);

public sealed class OrganisePlan
{
    public required string Root { get; init; }
    public required List<OrganiseItem> Items { get; init; }
    public required int Total { get; init; }
    public required int Settled { get; init; }
    public required Dictionary<string, int> OtherFiles { get; init; }
    public int Moves => Items.Count(i => i.Action == OrganiseAction.Move);
    public int Duplicates => Items.Count(i => i.Action == OrganiseAction.Duplicate);
    public int Undated => Items.Count(i => i.Action == OrganiseAction.Undated);
    public Dictionary<string, int> BySource => Items.Where(i => i.Action == OrganiseAction.Move)
        .GroupBy(i => i.DateSource ?? "?").ToDictionary(g => g.Key, g => g.Count());
    /// <summary>Files that end up renamed because another file with the same name lands in the same day.</summary>
    public int Renamed => Items.Count(i => i.Action == OrganiseAction.Move && !string.Equals(Path.GetFileName(i.Dest), i.Record.FileName, StringComparison.Ordinal));
}

public sealed record OrganiseOutcome(long RunId, string Manifest, int Moved, int Errors, int EmptyDirsRemoved, List<string> ErrorMessages);

/// <summary>
/// Re-files a library into YYYY/YYYY_MM/YYYY_MM_DD. Byte-identical copies go to _Duplicates, files with no
/// date to _Undated (keeping their old sub-folders), and every move is logged so it can be undone. Files in
/// physical albums (_Albums) stay put.
/// </summary>
public static partial class Organizer
{
    [GeneratedRegex(@"^(\d{4})[\\/]\1_(\d\d)[\\/]\1_\2_\d\d$")]
    private static partial Regex DatedDirRegex();

    [GeneratedRegex(@"\(\d+\)$|-duplicate\d*$|[ _-]copy$|-\d$", RegexOptions.IgnoreCase)]
    private static partial Regex DupMarkRegex();

    public static bool IsDatedRelativeDir(string relDir) => DatedDirRegex().IsMatch(relDir);

    /// <summary>True when a library still has media outside the dated layout (ignoring the reserved folders).</summary>
    public static bool NeedsOrganising(IEnumerable<MediaRecord> libraryRecords, string root)
    {
        foreach (var r in libraryRecords)
        {
            var rel = Path.GetRelativePath(root, r.Path);
            var relDir = Path.GetDirectoryName(rel) ?? "";
            var top = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
            if (top is LibraryFolders.Undated or LibraryFolders.Duplicates or LibraryFolders.Logs or LibraryFolders.Albums or LibraryFolders.Vault) continue;
            if (!IsDatedRelativeDir(relDir)) return true;
        }
        return false;
    }

    public static OrganisePlan Plan(string root, ScanResult scan, bool useMtime)
    {
        root = Path.GetFullPath(root);
        var alloc = new DestAllocator();
        var kept = new Dictionary<string, string>(StringComparer.Ordinal); // sha -> path that stays
        var settled = new List<MediaRecord>();
        var todo = new List<MediaRecord>();

        foreach (var r in scan.Records)
        {
            var rel = Path.GetRelativePath(root, r.Path);
            var relDir = Path.GetDirectoryName(rel) ?? "";
            var top = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
            var inDated = IsDatedRelativeDir(relDir);
            var inUndated = top == LibraryFolders.Undated && !(useMtime || r.Taken is not null);
            var inAlbum = top == LibraryFolders.Albums; // a physical album: the user put it there on purpose
            (inDated || inUndated || inAlbum ? settled : todo).Add(r);
        }
        foreach (var r in settled)
        {
            kept.TryAdd(r.Sha256, r.Path);
            alloc.Reserve(r.Path);
        }

        var items = new List<OrganiseItem>();
        foreach (var r in todo.OrderBy(KeepPreference, KeepComparer.Instance))
        {
            var rel = Path.GetRelativePath(root, r.Path);
            if (kept.TryGetValue(r.Sha256, out var original))
            {
                items.Add(new OrganiseItem(OrganiseAction.Duplicate, r, alloc.Claim(Path.Combine(root, LibraryFolders.Duplicates, rel)), original, r.TakenSource));
                continue;
            }
            var when = r.Taken;
            var source = r.TakenSource;
            if (when is null && useMtime)
            {
                when = new DateTime(r.MTime, DateTimeKind.Utc).ToLocalTime();
                source = DateSources.FileModified;
            }
            string dest;
            OrganiseAction action;
            if (when is { } w)
            {
                dest = alloc.Claim(Path.Combine(root, DateRules.DatedFolder(w), r.FileName));
                action = OrganiseAction.Move;
            }
            else
            {
                var top = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
                var sub = top == LibraryFolders.Undated ? Path.GetRelativePath(LibraryFolders.Undated, rel) : rel;
                dest = alloc.Claim(Path.Combine(root, LibraryFolders.Undated, sub));
                action = OrganiseAction.Undated;
            }
            kept[r.Sha256] = dest;
            items.Add(new OrganiseItem(action, r, dest, null, source));
        }

        return new OrganisePlan
        {
            Root = root, Items = items, Total = scan.Records.Count, Settled = settled.Count, OtherFiles = scan.OtherFiles,
        };
    }

    private static (bool HasDupMark, int NameLength, string Path) KeepPreference(MediaRecord r) =>
        (DupMarkRegex().IsMatch(Path.GetFileNameWithoutExtension(r.Path)), r.FileName.Length, r.Path.ToLowerInvariant());

    private sealed class KeepComparer : IComparer<(bool, int, string)>
    {
        public static readonly KeepComparer Instance = new();
        public int Compare((bool, int, string) a, (bool, int, string) b)
        {
            var c = a.Item1.CompareTo(b.Item1);
            if (c != 0) return c;
            c = a.Item2.CompareTo(b.Item2);
            return c != 0 ? c : string.CompareOrdinal(a.Item3, b.Item3);
        }
    }

    public static async Task<OrganiseOutcome> ApplyAsync(LibraryDb db, OrganisePlan plan, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var root = plan.Root;
        var logsDir = Path.Combine(root, LibraryFolders.Logs);
        Directory.CreateDirectory(logsDir);
        var manifest = Path.Combine(logsDir, $"organise_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
        var runId = db.BeginOrganiseRun(root);
        var moved = 0;
        var errors = new List<string>();
        var movedFrom = new HashSet<string>(Scanner.PathComparer);
        var logItems = new List<(string, string, string, string)>();

        await using (var writer = new StreamWriter(manifest, false, new UTF8Encoding(true)))
        {
            await writer.WriteLineAsync("action,original_path,new_path,taken,date_source,duplicate_of,sha256,status");
            for (var i = 0; i < plan.Items.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var item = plan.Items[i];
                var status = "done";
                try
                {
                    if (File.Exists(item.Dest)) throw new IOException($"destination exists: {item.Dest}");
                    Directory.CreateDirectory(Path.GetDirectoryName(item.Dest)!);
                    File.Move(item.Record.Path, item.Dest);
                    db.UpdatePath(item.Record.Path, item.Dest, inLibrary: item.Action != OrganiseAction.Duplicate);
                    movedFrom.Add(Path.GetDirectoryName(item.Record.Path)!);
                    moved++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    status = "error: " + ex.Message;
                    errors.Add($"{item.Record.Path}: {ex.Message}");
                }
                var action = item.Action.ToString().ToLowerInvariant();
                await writer.WriteLineAsync(string.Join(",", Csv(action), Csv(item.Record.Path), Csv(item.Dest),
                    Csv(item.Record.Taken?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? ""),
                    Csv(item.DateSource ?? ""), Csv(item.DuplicateOf ?? ""), Csv(item.Record.Sha256), Csv(status)));
                logItems.Add((action, item.Record.Path, item.Dest, status));
                if (i % 100 == 0 || i == plan.Items.Count - 1)
                {
                    progress?.Report(new ScanProgress("Moving files", i + 1, plan.Items.Count, item.Record.FileName));
                    await writer.FlushAsync(ct);
                }
            }
        }
        db.AddOrganiseItems(runId, logItems);
        db.FinishOrganiseRun(runId, manifest, moved, errors.Count);
        var removed = PruneEmptyDirs(root, movedFrom);
        return new OrganiseOutcome(runId, manifest, moved, errors.Count, removed, errors);
    }

    public static int PruneEmptyDirs(string root, IEnumerable<string> dirs)
    {
        var removed = 0;
        foreach (var start in dirs.OrderByDescending(d => d.Length))
        {
            var d = start;
            while (!Scanner.PathComparer.Equals(d, root) && d.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    if (!Directory.Exists(d)) break;
                    var leftovers = Directory.EnumerateFileSystemEntries(d).ToList();
                    if (leftovers.Any(e => !MediaKinds.IgnoredNames.Contains(Path.GetFileName(e)) || Directory.Exists(e))) break;
                    foreach (var e in leftovers) File.Delete(e);
                    Directory.Delete(d);
                    removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    break;
                }
                d = Path.GetDirectoryName(d)!;
            }
        }
        return removed;
    }

    /// <summary>Moves every file from a run back where it came from.</summary>
    public static async Task<(int Restored, int Skipped)> UndoAsync(LibraryDb db, long runId, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var items = db.GetOrganiseItems(runId).Where(i => i.Status == "done").ToList();
        var restored = 0;
        var skipped = 0;
        var touched = new HashSet<string>(Scanner.PathComparer);
        for (var i = items.Count - 1; i >= 0; i--)
        {
            ct.ThrowIfCancellationRequested();
            var (_, original, current, _) = items[i];
            try
            {
                if (File.Exists(current) && !File.Exists(original))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(original)!);
                    File.Move(current, original);
                    db.UpdatePath(current, original, inLibrary: true);
                    touched.Add(Path.GetDirectoryName(current)!);
                    restored++;
                }
                else skipped++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped++;
            }
            if (i % 100 == 0) progress?.Report(new ScanProgress("Restoring files", items.Count - i, items.Count));
        }
        var run = db.GetOrganiseRuns().FirstOrDefault(r => r.Id == runId);
        if (run is not null) PruneEmptyDirs(run.Library, touched);
        db.MarkOrganiseUndone(runId);
        return (restored, skipped);
    }

    public static string Csv(string s) =>
        s.Contains(',') || s.Contains('"') || s.Contains('\n') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}

/// <summary>Hands out destination paths that collide neither with existing files nor with earlier allocations.</summary>
public sealed class DestAllocator
{
    private readonly HashSet<string> _used = new(Scanner.PathComparer);

    public void Reserve(string path) => _used.Add(path);

    public string Claim(string dest)
    {
        var dir = Path.GetDirectoryName(dest)!;
        var stem = Path.GetFileNameWithoutExtension(dest);
        var ext = Path.GetExtension(dest);
        var n = 1;
        var candidate = dest;
        while (_used.Contains(candidate) || File.Exists(candidate))
            candidate = Path.Combine(dir, $"{stem}_{n++}{ext}");
        _used.Add(candidate);
        return candidate;
    }
}
