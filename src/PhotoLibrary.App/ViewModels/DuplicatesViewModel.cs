using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoLibrary.AI;
using PhotoLibrary.AI.Features;
using PhotoLibrary.App.Services;
using PhotoLibrary.Core;
using PhotoLibrary.Core.Db;
using PhotoLibrary.Core.Layout;

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

    /// <summary>A top-level folder some of the duplicates are in, for "keep the copies in this folder".</summary>
    public sealed record FolderChoice(string Key, string Label);

    public ObservableCollection<FolderChoice> FolderChoices { get; } = [];
    [ObservableProperty] public partial FolderChoice? KeepFolder { get; set; }
    public bool HasFolderChoices => FolderChoices.Count > 1;

    private const string RootKey = "";

    private string FolderKey(MediaRecord r) => LibraryFolders.TopOf(_main.State.LibraryRoot, r.Path) ?? RootKey;

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
        var map = _main.State.Albums.Folders();
        var inVirtual = _main.State.Db.AlbumsOf(sets.SelectMany(s => s.Records).Select(r => r.Id));
        bool InAlbum(MediaRecord r) => map.IsInFolderAlbum(r.Path) || inVirtual.ContainsKey(r.Id);
        foreach (var set in sets)
        {
            // Records come best quality first; prefer a copy that's in an album (see KeepChoice).
            var keep = KeepChoice.Choose(set.Records, set.Level == DuplicateLevel.Identical, InAlbum, r => r.PairKind != Core.Pairs.PairKind.None);
            var items = set.Records.Select((r, i) =>
            {
                var photo = new PhotoViewModel(r, _main, SelectInGroups);
                var caption = i != keep ? "Remove" : InAlbum(r) && keep != 0 ? "Keep (in an album)" : InAlbum(r) ? "Keep (best copy, in an album)" : "Keep (best copy)";
                var item = new ReviewItemViewModel(photo, caption, ReviewItemViewModel.DescribeFile(r, root)) { Marked = i != keep, Suggested = i == keep };
                item.MarkedChanged += changed => { changed.Caption = changed.Marked ? "Remove" : "Keep"; UpdateMarked(); };
                return item;
            }).ToList();
            var accent = set.Level switch { DuplicateLevel.Identical => "#2E9E4F", DuplicateLevel.SamePicture => "#8DB600", DuplicateLevel.NearIdentical => "#E0A000", _ => "#D9480F" };
            Groups.Add(new ReviewGroupViewModel($"{set.Records.Count} × {set.Reason}", $"{Formatting.Bytes(set.ReclaimableBytes)} reclaimable", items, accent));
        }
        HasGroups = Groups.Count > 0;
        BuildFolderChoices(map);
        var files = sets.Sum(s => s.Records.Count);
        var bytes = sets.Sum(s => s.ReclaimableBytes);
        Summary = sets.Count == 0 ? "No duplicates at this level." : $"{sets.Count:N0} groups  ·  {files:N0} files  ·  {Formatting.Bytes(bytes)} reclaimable";
        Status = sets.Count == 0 ? "Nothing found. Try a looser level."
            : "In each group the best copy is kept (a copy in an album wins when nothing is lost) and the rest are ticked. Untick anything you want to keep.";
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
        foreach (var g in Groups)
        {
            if (!g.Items.Any(i => i.Suggested)) { for (var i = 0; i < g.Items.Count; i++) g.Items[i].Marked = i > 0; continue; }
            foreach (var i in g.Items) i.Marked = !i.Suggested;
        }
    }

    /// <summary>Top-level folders that hold a copy in at least one group, busiest first.</summary>
    private void BuildFolderChoices(FolderMap map)
    {
        FolderChoices.Clear();
        var counts = Groups
            .SelectMany(g => g.Items.Select(i => FolderKey(i.Record)).Distinct(StringComparer.OrdinalIgnoreCase))
            .GroupBy(k => k, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Key: g.Key, Groups: g.Count()))
            .OrderByDescending(x => x.Groups).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, n) in counts)
        {
            var what = key == RootKey ? "loose in the library folder"
                : map.RoleOf(key) switch
                {
                    FolderRole.Date => "date folder",
                    FolderRole.Album => "folder album",
                    FolderRole.Keepsake => "Keepsake folder",
                    _ => "folder",
                };
            var name = key == RootKey ? "Library root" : key;
            FolderChoices.Add(new FolderChoice(key, $"{name}  ({what}, in {n:N0} group{(n == 1 ? "" : "s")})"));
        }
        KeepFolder = FolderChoices.FirstOrDefault(c => c.Key != RootKey && map.IsInFolderAlbum(Path.Combine(map.Root, c.Key, "x")))
                     ?? FolderChoices.FirstOrDefault();
        OnPropertyChanged(nameof(HasFolderChoices));
    }

    /// <summary>In every group with a copy in the chosen folder, keep that copy and tick the rest.</summary>
    [RelayCommand]
    private void KeepFromFolder()
    {
        if (KeepFolder is not { } folder) return;
        var changed = 0;
        foreach (var g in Groups)
        {
            // Items are best quality first, so the first copy in the folder is the one to keep.
            var keep = g.Items.FirstOrDefault(i => string.Equals(FolderKey(i.Record), folder.Key, StringComparison.OrdinalIgnoreCase));
            if (keep is null) continue;
            // A RAW is never ticked in favour of a non-RAW copy (the RAW + JPEG promise on the left).
            var keepIsRaw = MediaKinds.IsRaw(keep.Record.Path);
            foreach (var i in g.Items) i.Marked = !ReferenceEquals(i, keep) && (keepIsRaw || !MediaKinds.IsRaw(i.Record.Path));
            changed++;
        }
        var name = folder.Key == RootKey ? "the library folder itself" : folder.Key;
        Status = $"Keeping the copy in {name} in {changed:N0} group{(changed == 1 ? "" : "s")}; the other copies are ticked. Groups without a copy there are unchanged.";
    }

    [RelayCommand]
    private void ClearMarks()
    {
        foreach (var i in AllItems) i.Marked = false;
    }

    [RelayCommand(CanExecute = nameof(HasMarked))]
    // Only what's ticked goes: removing one copy of a photo never takes the RAW or clip paired with it.
    private Task RemoveMarkedAsync() => _main.RemoveAsync(AllItems.Where(i => i.Marked).Select(i => i.Record).ToList(), fromDisk: false, withCompanions: false);

    [RelayCommand(CanExecute = nameof(HasMarked))]
    private Task DeleteMarkedAsync() => _main.RemoveAsync(AllItems.Where(i => i.Marked).Select(i => i.Record).ToList(), fromDisk: true, withCompanions: false);

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
        FolderChoices.Clear();
        KeepFolder = null;
        OnPropertyChanged(nameof(HasFolderChoices));
    }
}
