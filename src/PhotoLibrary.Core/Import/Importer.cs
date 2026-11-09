using PhotoLibrary.Core.Db;
using PhotoLibrary.Core.Hashing;
using PhotoLibrary.Core.Organise;
using PhotoLibrary.Core.Scanning;

namespace PhotoLibrary.Core.Import;

public enum MatchStatus { Present, Similar, Missing }

public sealed class CheckItem
{
    public required MediaRecord Record { get; init; }
    public required MatchStatus Status { get; set; }
    public MediaRecord? Match { get; set; }
    public string How { get; set; } = "";
    public int? Distance { get; set; }
    /// <summary>For Similar items: whether the user chose to bring this copy in as well.</summary>
    public bool Selected { get; set; }
    public string? ImportedTo { get; set; }
    public string? Error { get; set; }
}

public sealed class CheckReport
{
    public required string Folder { get; init; }
    public required string LibraryRoot { get; init; }
    public required List<CheckItem> Items { get; init; }
    public required Dictionary<string, int> Skipped { get; init; }
    public int Present => Items.Count(i => i.Status == MatchStatus.Present);
    public int Similar => Items.Count(i => i.Status == MatchStatus.Similar);
    public int Missing => Items.Count(i => i.Status == MatchStatus.Missing);
}

/// <summary>
/// Compares a folder against the library (byte-identical, visually similar, or missing) and copies chosen
/// files into their dated folders. Sources are never modified or deleted.
/// </summary>
public sealed class Importer(LibraryDb db)
{
    public int SimilarThreshold { get; init; } = 4;

    public async Task<CheckReport> CheckAsync(string libraryRoot, string folder, List<MediaRecord> library, bool exactOnly,
        IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var scanner = new Scanner(db);
        folder = Path.GetFullPath(folder);
        var scan = await scanner.IndexTreeAsync(folder, null, inLibrary: false, progress, ct);

        var bySha = library.GroupBy(r => r.Sha256).ToDictionary(g => g.Key, g => g.ToList());
        var hidden = db.GetHiddenShas();
        var items = new List<CheckItem>();
        var unmatched = new List<CheckItem>();
        foreach (var r in scan.Records)
        {
            var item = new CheckItem { Record = r, Status = MatchStatus.Missing };
            if (bySha.TryGetValue(r.Sha256, out var same) && same.FirstOrDefault(c => !Scanner.PathComparer.Equals(c.Path, r.Path)) is { } m)
            {
                item.Status = MatchStatus.Present;
                item.Match = m;
                item.How = "identical file";
            }
            else if (hidden.Contains(r.Sha256))
            {
                item.Status = MatchStatus.Present;
                item.How = "identical to a file you removed from the library; not re-imported";
            }
            else unmatched.Add(item);
            items.Add(item);
        }

        if (unmatched.Count > 0 && !exactOnly)
        {
            // Same name + same capture time: a re-saved/edited copy of a known shot.
            var byShot = new Dictionary<(DateTime, string), List<MediaRecord>>();
            foreach (var l in library)
                if (l.Taken is { } t && DateSources.IsCaptureSource(l.TakenSource))
                {
                    var key = (t, Path.GetFileNameWithoutExtension(l.Path).ToLowerInvariant());
                    if (!byShot.TryGetValue(key, out var list)) byShot[key] = list = [];
                    list.Add(l);
                }

            var needHash = unmatched.Where(i => i.Record.IsImage).Select(i => i.Record).ToList();
            if (needHash.Count > 0)
            {
                await scanner.EnsureDHashAsync(needHash, progress, ct, "Fingerprinting new files");
                await scanner.EnsureDHashAsync(library, progress, ct, "Fingerprinting library (one-off)");
            }
            var libHashes = library.Where(l => l.DHash is not null).Select(l => (Hash: l.DHash!.Value, Rec: l)).ToArray();

            foreach (var item in unmatched)
            {
                ct.ThrowIfCancellationRequested();
                var r = item.Record;
                MediaRecord? m = null;
                var how = "";
                int? dist = null;
                if (r.Taken is { } t && DateSources.IsCaptureSource(r.TakenSource)
                    && byShot.TryGetValue((t, Path.GetFileNameWithoutExtension(r.Path).ToLowerInvariant()), out var shots))
                {
                    m = shots.FirstOrDefault(c => !Scanner.PathComparer.Equals(c.Path, r.Path));
                    how = "same name and capture time (re-saved or edited copy)";
                }
                if (m is null && r.DHash is { } h)
                {
                    var best = (Dist: 99, Rec: (MediaRecord?)null);
                    foreach (var (lh, l) in libHashes)
                    {
                        if (Scanner.PathComparer.Equals(l.Path, r.Path)) continue;
                        // Dark/plain images all fingerprint alike, so dates must agree too (allowing a tz shift).
                        if (r.Taken is { } a && l.Taken is { } b && Math.Abs((a - b).TotalHours) > 26) continue;
                        var d = DHash.Distance(h, lh);
                        if (d < best.Dist) best = (d, l);
                    }
                    if (best.Rec is not null && best.Dist <= SimilarThreshold)
                    {
                        m = best.Rec;
                        dist = best.Dist;
                        how = $"looks the same (visual distance {best.Dist}/64): re-encoded or resized copy";
                    }
                }
                if (m is not null)
                {
                    item.Status = MatchStatus.Similar;
                    item.Match = m;
                    item.How = how;
                    item.Distance = dist;
                }
            }
        }

        return new CheckReport { Folder = folder, LibraryRoot = Path.GetFullPath(libraryRoot), Items = items, Skipped = scan.OtherFiles };
    }

    /// <summary>Copies the Missing items plus any Similar items marked Selected. Returns the number imported.</summary>
    public async Task<int> ImportAsync(CheckReport report, Layout.FolderMap map, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var root = report.LibraryRoot;
        var toImport = report.Items.Where(i => i.Status == MatchStatus.Missing || (i.Status == MatchStatus.Similar && i.Selected)).ToList();
        var runId = db.BeginImportRun(report.Folder);
        var alloc = new DestAllocator();
        var doneShas = new HashSet<string>(db.GetLibrary().Select(r => r.Sha256));
        doneShas.UnionWith(db.GetHiddenShas());
        var imported = 0;
        var log = new List<(string, string, string?, string?)>();

        for (var i = 0; i < toImport.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var item = toImport[i];
            var r = item.Record;
            if (doneShas.Contains(r.Sha256))
            {
                item.How = "identical copy already imported";
                log.Add((r.Path, "skipped-duplicate", null, item.How));
                continue;
            }
            // A date whose folder is an album (or awaiting a decision) is imported like an undated file.
            var dest = r.Taken is { } t && map.DatedPath(t, r.FileName) is { } dated
                ? alloc.Claim(dated)
                : alloc.Claim(Path.Combine(root, LibraryFolders.Undated, Path.GetFileName(report.Folder), Path.GetRelativePath(report.Folder, r.Path)));
            try
            {
                await Task.Run(() => CopyPreserving(r.Path, dest), ct);
                var copy = new MediaRecord
                {
                    Path = dest, InLibrary = true, Size = r.Size, MTime = new FileInfo(dest).LastWriteTimeUtc.Ticks, Sha256 = r.Sha256,
                    Kind = r.Kind, Taken = r.Taken, TakenSource = r.TakenSource, Make = r.Make, Model = r.Model, Width = r.Width,
                    Height = r.Height, Orientation = r.Orientation, Lat = r.Lat, Lon = r.Lon, DurationMs = r.DurationMs,
                    DHash = r.DHash, DHashDone = r.DHashDone, Description = r.Description, SidecarPath = r.SidecarPath,
                    MetaVersion = r.MetaVersion, Added = DateTime.Now,
                };
                db.Upsert(copy);
                doneShas.Add(r.Sha256);
                item.ImportedTo = dest;
                imported++;
                log.Add((r.Path, "imported", dest, item.Status == MatchStatus.Similar ? "similar, imported on request" : "missing"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                item.Error = ex.Message;
                log.Add((r.Path, "error", dest, ex.Message));
            }
            if (i % 20 == 0 || i == toImport.Count - 1)
                progress?.Report(new ScanProgress("Copying", i + 1, toImport.Count, r.FileName));
        }
        foreach (var item in report.Items.Where(i => i.ImportedTo is null && i.Error is null && !toImport.Contains(i)))
            log.Add((item.Record.Path, item.Status.ToString().ToLowerInvariant(), item.Match?.Path, item.How));
        db.AddImportItems(runId, log);
        db.FinishImportRun(runId, report.Items.Count, report.Present, report.Similar, report.Missing, imported);
        return imported;
    }

    public static void CopyPreserving(string src, string dest)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(src, dest, overwrite: false);
        try
        {
            File.SetLastWriteTimeUtc(dest, File.GetLastWriteTimeUtc(src));
            File.SetCreationTimeUtc(dest, File.GetCreationTimeUtc(src));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // timestamps are nice-to-have
        }
    }
}
