using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoLibrary.AI;
using PhotoLibrary.AI.Features;
using PhotoLibrary.App.Services;
using PhotoLibrary.Core.Db;

namespace PhotoLibrary.App.ViewModels;

/// <summary>
/// The Duplicates tab: pick how loose a match to allow, find groups, tick the copies to drop (the best copy in
/// each group is left unticked), then remove them from the library or send them to the Recycle Bin.
/// </summary>
public partial class DuplicatesViewModel : ViewModelBase
{
    private readonly MainViewModel _main;
    private CancellationTokenSource? _cts;

    private AiEngine Ai => _main.State.Ai;

    public DuplicatesViewModel(MainViewModel main)
    {
        _main = main;
        main.PhotosRemoved += OnPhotosRemoved;
        UpdateLevelText();
    }

    public ObservableCollection<ReviewGroupViewModel> Groups { get; } = [];

    [ObservableProperty] public partial double Level { get; set; } = 0;
    [ObservableProperty] public partial string LevelName { get; set; } = "";
    [ObservableProperty] public partial string LevelDescription { get; set; } = "";
    [ObservableProperty] public partial string LevelAccent { get; set; } = "#2E9E4F";
    [ObservableProperty] public partial bool IncludeRawJpegPairs { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = "Choose a level and press Find duplicates.";
    [ObservableProperty] public partial string Summary { get; set; } = "";
    [ObservableProperty] public partial bool HasGroups { get; set; }
    [ObservableProperty] public partial int MarkedCount { get; set; }
    [ObservableProperty] public partial string MarkedText { get; set; } = "";

    public string ModelNote => Ai.DuplicatesCanUseModel()
        ? ""
        : "The two loosest levels compare photos with the AI model. Index the library on the AI Tools tab to enable them; until then they fall back to fingerprints only.";

    private DuplicateLevel CurrentLevel => (DuplicateLevel)Math.Clamp((int)Math.Round(Level), 0, 3);

    partial void OnLevelChanged(double value) => UpdateLevelText();

    private void UpdateLevelText()
    {
        (LevelName, LevelDescription, LevelAccent) = CurrentLevel switch
        {
            DuplicateLevel.Identical => ("Safe: identical files",
                "Byte-for-byte copies of the same file. Removing all but one loses nothing at all.", "#2E9E4F"),
            DuplicateLevel.SamePicture => ("Low risk: same picture",
                "The same photo saved again: re-encoded, resized or lightly edited. The copy with the most pixels is kept.", "#8DB600"),
            DuplicateLevel.NearIdentical => ("Medium risk: near-identical",
                "Burst shots, small crops and re-frames. Usually interchangeable, but glance at each group before removing.", "#E0A000"),
            _ => ("High risk: similar photos",
                "Different photos of the same scene. Many of these are keepers; treat the ticks as a starting point only.", "#D9480F"),
        };
        OnPropertyChanged(nameof(ModelNote));
    }

    [RelayCommand(CanExecute = nameof(CanFind))]
    private async Task FindAsync()
    {
        IsBusy = true;
        _cts = new CancellationTokenSource();
        var level = CurrentLevel;
        try
        {
            var progress = new Progress<IndexProgress>(p =>
                Status = p.Total == 0 ? "Comparing photos…" : $"Fingerprinting {p.Done:N0} / {p.Total:N0}" + (p.Current is null ? "" : $"  —  {p.Current}"));
            Status = "Comparing photos…";
            var sets = await Ai.FindDuplicatesAsync(level, progress, _cts.Token, IncludeRawJpegPairs);
            Show(sets);
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelled.";
        }
        catch (Exception ex)
        {
            Status = "Failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
            FindCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanFind() => !IsBusy;

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    private void Show(List<DuplicateSet> sets)
    {
        Clear();
        var root = _main.State.LibraryRoot;
        foreach (var set in sets)
        {
            var items = set.Records.Select((r, i) =>
            {
                var photo = new PhotoViewModel(r, _main, SelectInGroups);
                var item = new ReviewItemViewModel(photo, i == 0 ? "Keep (best copy)" : "Remove", ReviewItemViewModel.DescribeFile(r, root)) { Marked = i > 0 };
                item.MarkedChanged += _ => UpdateMarked();
                return item;
            });
            var accent = set.Level switch { DuplicateLevel.Identical => "#2E9E4F", DuplicateLevel.SamePicture => "#8DB600", DuplicateLevel.NearIdentical => "#E0A000", _ => "#D9480F" };
            Groups.Add(new ReviewGroupViewModel($"{set.Records.Count} × {set.Reason}", $"{Formatting.Bytes(set.ReclaimableBytes)} reclaimable", items, accent));
        }
        HasGroups = Groups.Count > 0;
        var files = sets.Sum(s => s.Records.Count);
        var bytes = sets.Sum(s => s.ReclaimableBytes);
        Summary = sets.Count == 0 ? "No duplicates at this level." : $"{sets.Count:N0} groups  ·  {files:N0} files  ·  {Formatting.Bytes(bytes)} reclaimable";
        Status = sets.Count == 0 ? "Nothing found. Try a looser level." : "The best copy in each group is kept; the rest are ticked. Untick anything you want to keep.";
        UpdateMarked();
    }

    private IEnumerable<ReviewItemViewModel> AllItems => Groups.SelectMany(g => g.Items);

    private void SelectInGroups(PhotoViewModel photo, SelectMode mode) =>
        _main.ApplySelection(AllItems.Select(i => i.Photo).ToList(), photo, mode);

    private void UpdateMarked()
    {
        var marked = AllItems.Where(i => i.Marked).ToList();
        MarkedCount = marked.Count;
        MarkedText = marked.Count == 0 ? "Nothing ticked" : $"{marked.Count:N0} ticked  ·  {Formatting.Bytes(marked.Sum(i => i.Record.Size))}";
        RemoveMarkedCommand.NotifyCanExecuteChanged();
        DeleteMarkedCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void MarkAllButBest()
    {
        foreach (var g in Groups) for (var i = 0; i < g.Items.Count; i++) g.Items[i].Marked = i > 0;
    }

    [RelayCommand]
    private void ClearMarks()
    {
        foreach (var i in AllItems) i.Marked = false;
    }

    [RelayCommand(CanExecute = nameof(HasMarked))]
    private Task RemoveMarkedAsync() => _main.RemoveAsync(AllItems.Where(i => i.Marked).Select(i => i.Record).ToList(), fromDisk: false);

    [RelayCommand(CanExecute = nameof(HasMarked))]
    private Task DeleteMarkedAsync() => _main.RemoveAsync(AllItems.Where(i => i.Marked).Select(i => i.Record).ToList(), fromDisk: true);

    private bool HasMarked() => MarkedCount > 0;

    private void OnPhotosRemoved(IReadOnlySet<long> ids)
    {
        foreach (var g in Groups.ToList())
        {
            foreach (var item in g.Items.Where(i => ids.Contains(i.Record.Id)).ToList())
            {
                item.Photo.ReleaseThumbnail();
                g.Items.Remove(item);
            }
            if (g.Items.Count < 2)
            {
                foreach (var i in g.Items) i.Photo.ReleaseThumbnail();
                Groups.Remove(g);
            }
        }
        HasGroups = Groups.Count > 0;
        if (Groups.Count == 0) Summary = "All reviewed.";
        UpdateMarked();
    }

    private void Clear()
    {
        foreach (var i in AllItems) i.Photo.ReleaseThumbnail();
        Groups.Clear();
    }
}
