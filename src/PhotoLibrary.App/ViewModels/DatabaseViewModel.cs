using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoLibrary.AI.Db;
using PhotoLibrary.App.Services;
using PhotoLibrary.Core.Db;
using PhotoLibrary.Core.Pairs;

namespace PhotoLibrary.App.ViewModels;

/// <summary>
/// Settings › Database (advanced mode): a read-only look inside Keepsake's data. Photos shows everything stored
/// about one file, above all the labels the AI gave it; Labels lists every label and the photos carrying it;
/// Tables runs read-only SQL.
/// </summary>
public partial class DatabaseViewModel(MainViewModel main) : ViewModelBase
{
    private const int PhotoLimit = 300;
    private const int TagPhotoLimit = 500;
    private const int QueryRowLimit = 500;

    private AppState State => main.State;
    private CancellationTokenSource? _detailCts;
    private bool _loaded;

    public sealed record FileRow(MediaRecord Record, string Info, string? ScoreText = null)
    {
        public string Name => Record.FileName;
        public string Folder => Path.GetDirectoryName(Record.Path) ?? "";
    }

    public sealed record Field(string Name, string Value);

    public sealed record TagRow(string Model, string Tag, float Score)
    {
        public string ScoreText => Score.ToString("0.000", CultureInfo.InvariantCulture);
        public double BarWidth => Math.Clamp(Score, 0, 1) * 120;
    }

    public sealed record TagCountRow(string Tag, int Count);
    public sealed record TableRow(string Name, long Rows)
    {
        public string Label => $"{Name}  ({Rows:N0})";
    }
    public sealed record Cell(string Text, double Width);
    public sealed record ResultRow(IReadOnlyList<Cell> Cells);

    /// <summary>Which of Photos / Labels / Tables is showing.</summary>
    [ObservableProperty] public partial int SubTab { get; set; }

    /// <summary>Loads lists the first time the page is opened, so Settings stays quick when it isn't used.</summary>
    public void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        Refresh();
    }

    [RelayCommand]
    private void Refresh()
    {
        SearchPhotos();
        LoadTagModels();
        LoadTables();
    }

    // ---- photos

    [ObservableProperty] public partial string PhotoSearch { get; set; } = "";
    public ObservableCollection<FileRow> PhotoResults { get; } = [];
    [ObservableProperty] public partial string PhotoResultsText { get; set; } = "";
    [ObservableProperty] public partial FileRow? SelectedPhoto { get; set; }

    [ObservableProperty] public partial Bitmap? DetailThumbnail { get; set; }
    [ObservableProperty] public partial string DetailHeader { get; set; } = "Pick a photo to see what Keepsake knows about it.";
    [ObservableProperty] public partial bool HasDetail { get; set; }
    public ObservableCollection<TagRow> DetailTags { get; } = [];
    [ObservableProperty] public partial string DetailTagsText { get; set; } = "";
    public ObservableCollection<Field> DetailAi { get; } = [];
    public ObservableCollection<Field> DetailFile { get; } = [];

    /// <summary>Matches file name or folder; a number also matches the row id.</summary>
    [RelayCommand]
    private void SearchPhotos()
    {
        var text = PhotoSearch.Trim();
        var escaped = text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
        var rows = text.Length == 0
            ? State.Db.Query($"1=1 ORDER BY id DESC LIMIT {PhotoLimit}")
            : State.Db.Query($"(path LIKE @p ESCAPE '\\' OR id=@id) ORDER BY id DESC LIMIT {PhotoLimit}",
                ("@p", "%" + escaped + "%"), ("@id", long.TryParse(text, out var id) ? id : -1));
        PhotoResults.Clear();
        foreach (var r in rows) PhotoResults.Add(new FileRow(r, Describe(r)));
        PhotoResultsText = rows.Count >= PhotoLimit ? $"First {PhotoLimit:N0} matches, newest first" : $"{rows.Count:N0} matches";
    }

    private static string Describe(MediaRecord r)
    {
        var flags = new List<string> { $"#{r.Id}" };
        if (r.Hidden) flags.Add("removed");
        else if (!r.InLibrary) flags.Add("not in library");
        if (r.IsCompanion) flags.Add($"companion of #{r.PairOf}");
        if (r.PairKind != PairKind.None) flags.Add(r.PairKind.ToString().ToLowerInvariant());
        return string.Join("  ·  ", flags);
    }

    partial void OnSelectedPhotoChanged(FileRow? value) => _ = LoadDetailAsync(value?.Record);

    /// <summary>Opens a photo in the Photos page, e.g. from a label's list.</summary>
    public void Inspect(MediaRecord record)
    {
        SubTab = 0;
        var row = PhotoResults.FirstOrDefault(p => p.Record.Id == record.Id);
        if (row is null)
        {
            row = new FileRow(record, Describe(record));
            PhotoResults.Insert(0, row);
        }
        SelectedPhoto = row;
    }

    private async Task LoadDetailAsync(MediaRecord? record)
    {
        _detailCts?.Cancel();
        var cts = _detailCts = new CancellationTokenSource();
        var old = DetailThumbnail;
        DetailThumbnail = null;
        old?.Dispose();
        DetailTags.Clear();
        DetailAi.Clear();
        DetailFile.Clear();
        HasDetail = record is not null;
        if (record is null) { DetailHeader = "Pick a photo to see what Keepsake knows about it."; return; }
        DetailHeader = record.FileName;

        var dbPath = State.Db.Path;
        var (ai, fields) = await Task.Run(() => (State.Db.AiDataFor(record.Id), DatabaseInspector.Row(dbPath, "files", record.Id)));
        if (cts.IsCancellationRequested) return;

        foreach (var t in ai.Tags) DetailTags.Add(new TagRow(t.Model, t.Tag, t.Score));
        DetailTagsText = ai.Tags.Count == 0
            ? "No AI labels. Labels are added when the library is indexed on the AI Tools tab."
            : $"{ai.Tags.Count:N0} labels from {ai.Tags.Select(t => t.Model).Distinct().Count()} model(s), most confident first";

        foreach (var (model, dim) in ai.Embeddings) DetailAi.Add(new Field("Search index", $"{model}  ·  {dim}-dimension vector"));
        if (ai.Embeddings.Count == 0) DetailAi.Add(new Field("Search index", "not indexed"));
        foreach (var (model, frames) in ai.VideoFrames) DetailAi.Add(new Field("Video frames", $"{model}  ·  {frames:N0} frames"));
        if (ai.Quality is { } q)
            DetailAi.Add(new Field("Quality", FormattableString.Invariant($"brightness {q.Brightness:0.000}  ·  contrast {q.Contrast:0.000}  ·  sharpness {q.Sharpness:0.000}  (v{q.Version})")));
        if (ai.Events.Count > 0) DetailAi.Add(new Field("Events", string.Join(", ", ai.Events)));
        var albumIds = State.Db.AlbumsOf([record.Id]).GetValueOrDefault(record.Id) ?? [];
        if (albumIds.Count > 0)
        {
            var names = State.Db.GetAlbums().Where(a => albumIds.Contains(a.Id)).Select(a => a.Name);
            DetailAi.Add(new Field("Albums", string.Join(", ", names)));
        }
        if (record.PairKind != PairKind.None)
            DetailAi.Add(new Field("Paired files", string.Join(", ", State.Db.GetCompanions([record.Id]).Select(c => c.FileName))));
        foreach (var (column, value) in fields) DetailFile.Add(new Field(column, value));

        try
        {
            var thumb = await State.Thumbs.GetAsync(record, cts.Token);
            var bmp = thumb is null ? null : await BitmapLoader.FromFileAsync(thumb, null, cts.Token);
            if (cts.IsCancellationRequested) { bmp?.Dispose(); return; }
            DetailThumbnail = bmp;
        }
        catch (OperationCanceledException) { }
    }

    [RelayCommand]
    private void OpenSelectedFile()
    {
        if (SelectedPhoto?.Record.Path is { } p && File.Exists(p)) Formatting.OpenExternal(p);
    }

    // ---- labels

    public ObservableCollection<string> TagModels { get; } = [];
    [ObservableProperty] public partial string? TagModel { get; set; }
    [ObservableProperty] public partial string TagFilter { get; set; } = "";
    private List<TagCountRow> _allTags = [];
    public ObservableCollection<TagCountRow> Tags { get; } = [];
    [ObservableProperty] public partial string TagsText { get; set; } = "";
    [ObservableProperty] public partial TagCountRow? SelectedTag { get; set; }
    public ObservableCollection<FileRow> TagPhotos { get; } = [];
    [ObservableProperty] public partial FileRow? SelectedTagPhoto { get; set; }
    [ObservableProperty] public partial string TagPhotosText { get; set; } = "";

    private void LoadTagModels()
    {
        var current = TagModel;
        TagModels.Clear();
        foreach (var m in State.Db.TagModels()) TagModels.Add(m);
        TagModel = TagModels.Contains(current ?? "") ? current : TagModels.Contains(State.Ai.ModelId) ? State.Ai.ModelId : TagModels.FirstOrDefault();
        if (TagModels.Count == 0) { _allTags = []; FilterTags(); }
    }

    partial void OnTagModelChanged(string? value)
    {
        _allTags = value is null ? [] : State.Db.TagCounts(value, 5000).Select(t => new TagCountRow(t.Tag, t.Count)).ToList();
        FilterTags();
    }

    partial void OnTagFilterChanged(string value) => FilterTags();

    private void FilterTags()
    {
        Tags.Clear();
        var f = TagFilter.Trim();
        foreach (var t in _allTags.Where(t => f.Length == 0 || t.Tag.Contains(f, StringComparison.OrdinalIgnoreCase))) Tags.Add(t);
        TagsText = TagModels.Count == 0
            ? "No labels yet. Index the library on the AI Tools tab first."
            : $"{Tags.Count:N0} of {_allTags.Count:N0} labels, by how many photos carry them";
    }

    partial void OnSelectedTagChanged(TagCountRow? value)
    {
        TagPhotos.Clear();
        if (value is null || TagModel is not { } model) { TagPhotosText = ""; return; }
        var hits = State.Db.FilesWithTag(model, value.Tag, TagPhotoLimit);
        var records = State.Db.GetByIds(hits.Select(h => h.FileId).ToList()).ToDictionary(r => r.Id);
        foreach (var (id, score) in hits)
            if (records.TryGetValue(id, out var r))
                TagPhotos.Add(new FileRow(r, Describe(r), score.ToString("0.000", CultureInfo.InvariantCulture)));
        TagPhotosText = hits.Count >= TagPhotoLimit
            ? $"“{value.Tag}”: the {TagPhotoLimit:N0} most confident of {value.Count:N0}. Double-click one to inspect it."
            : $"“{value.Tag}”: {value.Count:N0} photos, most confident first. Double-click one to inspect it.";
    }

    [RelayCommand]
    private void InspectTagPhoto()
    {
        if (SelectedTagPhoto is { } p) Inspect(p.Record);
    }

    // ---- tables and SQL

    public ObservableCollection<TableRow> Tables { get; } = [];
    [ObservableProperty] public partial TableRow? SelectedTable { get; set; }
    [ObservableProperty] public partial string Sql { get; set; } = "SELECT model, tag, count(*) AS photos FROM ai_tags GROUP BY model, tag ORDER BY photos DESC";
    public ObservableCollection<Cell> ResultColumns { get; } = [];
    public ObservableCollection<ResultRow> ResultRows { get; } = [];
    [ObservableProperty] public partial string QueryStatus { get; set; } = "Read-only: queries can look at the data but never change it.";
    [ObservableProperty] public partial bool QueryRunning { get; set; }

    private void LoadTables()
    {
        Tables.Clear();
        try
        {
            foreach (var (name, rows) in DatabaseInspector.Tables(State.Db.Path)) Tables.Add(new TableRow(name, rows));
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) { QueryStatus = ex.Message; }
    }

    partial void OnSelectedTableChanged(TableRow? value)
    {
        if (value is null) return;
        Sql = $"SELECT * FROM \"{value.Name}\" LIMIT {QueryRowLimit}";
        _ = RunQueryAsync();
    }

    [RelayCommand]
    private async Task RunQueryAsync()
    {
        if (QueryRunning) return;
        QueryRunning = true;
        QueryStatus = "Running…";
        try
        {
            var sql = Sql;
            var path = State.Db.Path;
            var result = await Task.Run(() => DatabaseInspector.Run(path, sql, QueryRowLimit));
            ResultColumns.Clear();
            ResultRows.Clear();
            if (result.Error is { } error) { QueryStatus = "Error: " + error; return; }
            var widths = result.Columns.Select((c, i) =>
            {
                var longest = result.Rows.Take(200).Select(r => r[i].Length).DefaultIfEmpty(0).Max();
                return Math.Clamp(Math.Max(c.Length, longest) * 7.5 + 20, 60, 380);
            }).ToList();
            for (var i = 0; i < result.Columns.Count; i++) ResultColumns.Add(new Cell(result.Columns[i], widths[i]));
            foreach (var row in result.Rows)
                ResultRows.Add(new ResultRow(row.Select((v, i) => new Cell(v.Length > 300 ? v[..300] + "…" : v, widths[i])).ToList()));
            QueryStatus = (result.Truncated ? $"First {result.Rows.Count:N0} rows" : $"{result.Rows.Count:N0} row{(result.Rows.Count == 1 ? "" : "s")}")
                          + $"  ·  {result.Elapsed.TotalMilliseconds:N0} ms";
        }
        finally { QueryRunning = false; }
    }
}
