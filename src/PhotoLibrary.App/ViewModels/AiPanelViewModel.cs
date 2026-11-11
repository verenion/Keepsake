using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoLibrary.AI;
using PhotoLibrary.AI.Clip;
using PhotoLibrary.AI.Db;
using PhotoLibrary.App.Services;
using PhotoLibrary.Core.Db;

namespace PhotoLibrary.App.ViewModels;

public sealed record TagChip(string Tag, int Count, ICommand Search)
{
    public string Label => $"{Tag}  {Count:N0}";
}

public sealed class EventItem(PhotoEvent ev)
{
    public PhotoEvent Event { get; } = ev;
    public string Name => Event.Name;
    public string CountText => Event.Count == 1 ? "1 photo" : $"{Event.Count:N0} photos";
}

/// <summary>
/// The AI tab: model download, incremental indexing, text and image search, tag cloud and events.
/// Results are shown as the same tiles as the library grid and select into the shared preview pane.
/// </summary>
public partial class AiPanelViewModel : ViewModelBase
{
    private readonly MainViewModel _main;
    private CancellationTokenSource? _indexCts;
    private CancellationTokenSource? _downloadCts;

    private AppState State => _main.State;
    private AiEngine Ai => State.Ai;

    public AiPanelViewModel(MainViewModel main)
    {
        _main = main;
        main.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(MainViewModel.SelectedPhoto)) return;
            FindSimilarCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(SelectedName));
            OnPropertyChanged(nameof(SelectedTags));
        };
        main.PhotosRemoved += OnPhotosRemoved;
        State.Settings.ModelChanged += () =>
        {
            OnPropertyChanged(nameof(ModelName));
            OnPropertyChanged(nameof(ModelSize));
            Refresh();
        };
        Refresh();
    }

    [ObservableProperty] public partial bool ModelInstalled { get; set; }
    [ObservableProperty] public partial string ModelStatus { get; set; } = "";
    [ObservableProperty] public partial bool IsDownloading { get; set; }
    [ObservableProperty] public partial double DownloadPercent { get; set; }

    [ObservableProperty] public partial string IndexStatus { get; set; } = "";
    [ObservableProperty] public partial bool IsIndexing { get; set; }
    [ObservableProperty] public partial double IndexPercent { get; set; }
    [ObservableProperty] public partial bool IndexComplete { get; set; }

    [ObservableProperty] public partial string Query { get; set; } = "";
    [ObservableProperty] public partial string ResultsHeader { get; set; } = "Search for something, pick a tag, or choose an event.";
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial EventItem? SelectedEvent { get; set; }
    [ObservableProperty] public partial bool HasTags { get; set; }
    [ObservableProperty] public partial bool HasEvents { get; set; }

    public ObservableCollection<PhotoViewModel> Results { get; } = [];
    public ObservableCollection<TagChip> Tags { get; } = [];
    public ObservableCollection<EventItem> Events { get; } = [];

    public string ModelName => Ai.Files.DisplayName;
    public string ModelSize => Ai.Spec.SizeText;
    public string SelectedName => _main.SelectedPhoto?.FileName ?? "No photo selected";

    /// <summary>Zero-shot labels for the selected photo, e.g. "beach 62% · sea 21%", or why there are none.</summary>
    public string SelectedTags
    {
        get
        {
            if (_main.SelectedPhoto is not { } photo) return "";
            if (!ModelInstalled) return "";
            var tags = Ai.TagsFor(photo.Record.Id);
            return tags.Count == 0
                ? "Not indexed yet, or no label fitted well enough."
                : string.Join("  ·  ", tags.Select(t => $"{t.Tag} {t.Score:P0}"));
        }
    }

    /// <summary>Re-read model, index, tag and event state. Cheap; called after scans and after our own work.</summary>
    public void Refresh()
    {
        ModelInstalled = Ai.ModelInstalled;
        ModelStatus = !ModelInstalled
            ? $"Search and tagging need the {ModelName} model ({ModelSize}), downloaded once from Hugging Face."
            : Ai.LoadedDevice is { } device
                ? $"{ModelName} is running on {device}. Nothing leaves your machine."
                : $"{ModelName} is installed and runs on this PC. Nothing leaves your machine.";
        _main.Settings?.RefreshDevice();
        if (!IsIndexing)
        {
            Ai.InvalidateIndex();
            var (embedded, total) = Ai.IndexStatus();
            IndexComplete = embedded >= total && total > 0;
            IndexStatus = total == 0 ? "The library is empty."
                : embedded == 0 ? $"{total:N0} items are not indexed yet."
                : embedded >= total ? $"All {total:N0} items are indexed."
                : $"{embedded:N0} of {total:N0} items indexed. {total - embedded:N0} to go.";
        }
        Tags.Clear();
        foreach (var t in Ai.TagCounts(40)) Tags.Add(new TagChip(t.Tag, t.Count, SearchTagCommand));
        HasTags = Tags.Count > 0;
        var selected = SelectedEvent?.Event.Id;
        Events.Clear();
        foreach (var e in Ai.Events()) Events.Add(new EventItem(e));
        HasEvents = Events.Count > 0;
        if (selected is { } id) SelectedEvent = Events.FirstOrDefault(e => e.Event.Id == id);
        OnPropertyChanged(nameof(SelectedTags));
        IndexLibraryCommand.NotifyCanExecuteChanged();
        SearchCommand.NotifyCanExecuteChanged();
        FindSimilarCommand.NotifyCanExecuteChanged();
    }

    // ---- model

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private async Task DownloadModelAsync()
    {
        IsDownloading = true;
        _downloadCts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<ClipModelFiles.DownloadProgress>(p =>
            {
                DownloadPercent = p.BytesTotal == 0 ? 0 : 100.0 * p.BytesDone / p.BytesTotal;
                ModelStatus = p.CurrentFile.Length == 0 ? "Download complete." : $"Downloading {p.CurrentFile}… {p.BytesDone / 1_000_000} / {p.BytesTotal / 1_000_000} MB";
            });
            await Ai.DownloadModelAsync(progress, _downloadCts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ModelStatus = "Download failed: " + ex.Message;
            IsDownloading = false;
            return;
        }
        finally
        {
            IsDownloading = false;
            DownloadPercent = 0;
        }
        Refresh();
        _main.Settings.RefreshModels();
    }

    private bool CanDownload() => !IsDownloading;

    [RelayCommand]
    private void CancelDownload() => _downloadCts?.Cancel();

    // ---- indexing

    [RelayCommand(CanExecute = nameof(CanIndex))]
    private async Task IndexLibraryAsync()
    {
        IsIndexing = true;
        IndexPercent = 0;
        IndexStatus = "Loading model…";
        _indexCts = new CancellationTokenSource();
        var ct = _indexCts.Token;
        try
        {
            var progress = new Progress<IndexProgress>(p =>
            {
                IndexPercent = p.Total == 0 ? 100 : 100.0 * p.Done / p.Total;
                IndexStatus = p.Total == 0 ? "Nothing new to index." : $"Indexing {p.Done:N0} / {p.Total:N0}" + (p.Current is null ? "" : $"  —  {p.Current}");
            });
            var result = await Task.Run(() => Ai.IndexAsync(progress, ct), ct);
            IndexStatus = result.Skipped == 0
                ? $"Indexed {result.Indexed:N0} items."
                : $"Indexed {result.Indexed:N0} items; {result.Skipped:N0} could not be decoded.";
        }
        catch (OperationCanceledException)
        {
            IndexStatus = "Indexing stopped. Run it again to continue where it left off.";
        }
        catch (Exception ex)
        {
            IndexStatus = "Indexing failed: " + ex.Message;
        }
        finally
        {
            IsIndexing = false;
            IndexPercent = 0;
        }
        var status = IndexStatus;
        Refresh();
        IndexStatus = status;
    }

    private bool CanIndex() => ModelInstalled && !IsIndexing;

    [RelayCommand]
    private void CancelIndex() => _indexCts?.Cancel();

    // ---- search

    [RelayCommand(CanExecute = nameof(CanSearch))]
    private async Task SearchAsync()
    {
        var query = Query.Trim();
        if (query.Length == 0) return;
        SelectedEvent = null;
        await RunAsync($"Searching for “{query}”…", () => Ai.SearchText(query),
            results => $"“{query}”  ·  {results.Count:N0} {(results.Count == 1 ? "match" : "matches")}");
    }

    private bool CanSearch() => ModelInstalled && !IsBusy;

    [RelayCommand]
    private async Task SearchTagAsync(string tag)
    {
        Query = tag;
        await SearchAsync();
    }

    [RelayCommand(CanExecute = nameof(CanFindSimilar))]
    private async Task FindSimilarAsync()
    {
        if (_main.SelectedPhoto is not { } photo) return;
        SelectedEvent = null;
        var record = photo.Record;
        await RunAsync($"Finding photos like {record.FileName}…", () => Ai.FindSimilarAsync(record).GetAwaiter().GetResult(),
            results => $"Like {record.FileName}  ·  {results.Count:N0} {(results.Count == 1 ? "match" : "matches")}");
    }

    private bool CanFindSimilar() => ModelInstalled && !IsBusy && _main.SelectedPhoto is not null;

    private async Task RunAsync(string busyText, Func<List<SearchResult>> work, Func<List<SearchResult>, string> header)
    {
        IsBusy = true;
        ResultsHeader = busyText;
        try
        {
            var loaded = Ai.ModelLoaded;
            var results = await Task.Run(work);
            ShowResults(results, header(results));
            if (!loaded) Refresh();
        }
        catch (Exception ex)
        {
            ResultsHeader = "Error: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
            SearchCommand.NotifyCanExecuteChanged();
            FindSimilarCommand.NotifyCanExecuteChanged();
        }
    }

    // ---- events

    [RelayCommand(CanExecute = nameof(CanBuildEvents))]
    private async Task BuildEventsAsync()
    {
        IsBusy = true;
        ResultsHeader = "Grouping the library into events…";
        try
        {
            var events = await Task.Run(Ai.BuildEvents);
            Events.Clear();
            foreach (var e in events) Events.Add(new EventItem(e));
            HasEvents = Events.Count > 0;
            ResultsHeader = $"{events.Count:N0} events. Pick one to see its photos.";
        }
        catch (Exception ex)
        {
            ResultsHeader = "Error: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanBuildEvents() => !IsBusy;

    partial void OnSelectedEventChanged(EventItem? value)
    {
        if (value is null) return;
        _ = ShowEventAsync(value);
    }

    private async Task ShowEventAsync(EventItem item)
    {
        try
        {
            var members = await Task.Run(() => Ai.EventMembers(item.Event.Id));
            if (!ReferenceEquals(SelectedEvent, item)) return;
            ShowResults(members, $"{item.Name}  ·  {item.CountText}");
        }
        catch (Exception ex)
        {
            ResultsHeader = "Error: " + ex.Message;
        }
    }

    partial void OnIsBusyChanged(bool value) => BuildEventsCommand.NotifyCanExecuteChanged();

    private void SelectInResults(PhotoViewModel photo, SelectMode mode) => _main.ApplySelection(Results, photo, mode);

    private void OnPhotosRemoved(IReadOnlySet<long> ids)
    {
        foreach (var p in Results.Where(p => ids.Contains(p.Record.Id)).ToList())
        {
            p.ReleaseThumbnail();
            Results.Remove(p);
        }
    }

    private void ShowResults(IEnumerable<MediaRecord> records, string header) =>
        ShowResults(records.Select(r => new SearchResult(r, 0)), header);

    private void ShowResults(IEnumerable<SearchResult> results, string header)
    {
        var records = results.Where(r => !r.Record.IsCompanion).ToList(); // a RAW or clip shows as its photo
        foreach (var p in Results) p.ReleaseThumbnail();
        Results.Clear();
        foreach (var r in records) Results.Add(new PhotoViewModel(r.Record, _main, SelectInResults) { AtMs = r.AtMs });
        ResultsHeader = Results.Count == 0 ? header + "  —  nothing found" : header;
    }
}
