using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoLibrary.AI;
using PhotoLibrary.App.Services;

namespace PhotoLibrary.App.ViewModels;

/// <summary>
/// The Quality tab: measure every photo once, then slide the strictness to surface the dark, blown-out, flat
/// and blurry ones, worst first. Tick what should go and remove or delete it.
/// </summary>
public partial class QualityViewModel : ViewModelBase
{
    private readonly MainViewModel _main;
    private CancellationTokenSource? _cts;

    private AiEngine Ai => _main.State.Ai;

    public QualityViewModel(MainViewModel main)
    {
        _main = main;
        main.PhotosRemoved += OnPhotosRemoved;
        RefreshStatus();
    }

    public ObservableCollection<ReviewItemViewModel> Items { get; } = [];

    [ObservableProperty] public partial string MeasureStatus { get; set; } = "";
    [ObservableProperty] public partial bool IsMeasuring { get; set; }
    [ObservableProperty] public partial double MeasurePercent { get; set; }
    [ObservableProperty] public partial bool HasMeasurements { get; set; }
    [ObservableProperty] public partial double Strictness { get; set; } = 0.3;
    [ObservableProperty] public partial string StrictnessText { get; set; } = "";
    [ObservableProperty] public partial string Summary { get; set; } = "Measure the library, then adjust the slider.";
    [ObservableProperty] public partial int MarkedCount { get; set; }
    [ObservableProperty] public partial string MarkedText { get; set; } = "";

    public void RefreshStatus()
    {
        var (measured, total) = Ai.QualityStatus();
        HasMeasurements = measured > 0;
        MeasureStatus = total == 0 ? "No images in the library."
            : measured == 0 ? $"{total:N0} images not measured yet."
            : measured >= total ? $"All {total:N0} images measured."
            : $"{measured:N0} of {total:N0} images measured.";
        UpdateStrictnessText();
    }

    [RelayCommand(CanExecute = nameof(CanMeasure))]
    private async Task MeasureAsync()
    {
        IsMeasuring = true;
        _cts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<IndexProgress>(p =>
            {
                MeasurePercent = p.Total == 0 ? 100 : 100.0 * p.Done / p.Total;
                MeasureStatus = p.Total == 0 ? "Nothing new to measure." : $"Measuring {p.Done:N0} / {p.Total:N0}" + (p.Current is null ? "" : $"  —  {p.Current}");
            });
            var ct = _cts.Token;
            var result = await Task.Run(() => Ai.AnalyseQualityAsync(progress, ct), ct);
            MeasureStatus = result.Skipped == 0 ? $"Measured {result.Indexed:N0} images." : $"Measured {result.Indexed:N0} images; {result.Skipped:N0} could not be decoded.";
        }
        catch (OperationCanceledException) { MeasureStatus = "Stopped. Run again to continue."; }
        catch (Exception ex) { MeasureStatus = "Failed: " + ex.Message; }
        finally
        {
            IsMeasuring = false;
            MeasurePercent = 0;
        }
        var status = MeasureStatus;
        RefreshStatus();
        MeasureStatus = status;
        Apply();
    }

    private bool CanMeasure() => !IsMeasuring;

    [RelayCommand]
    private void CancelMeasure() => _cts?.Cancel();

    partial void OnStrictnessChanged(double value)
    {
        UpdateStrictnessText();
        if (HasMeasurements) Apply();
    }

    private void UpdateStrictnessText()
    {
        StrictnessText = Strictness switch
        {
            < 0.25 => "Only the hopeless: black, white or wildly blurred",
            < 0.6 => "Clearly poor photos",
            < 0.85 => "Including marginal ones",
            _ => "Anything slightly dark, flat or soft",
        };
    }

    /// <summary>Re-run the judgement at the current strictness over the stored measurements.</summary>
    [RelayCommand]
    public void Apply()
    {
        foreach (var i in Items) i.Photo.ReleaseThumbnail();
        Items.Clear();
        var poor = Ai.PoorPhotos((float)Strictness).Where(p => !p.Record.IsCompanion).ToList(); // a RAW goes with its photo
        var root = _main.State.LibraryRoot;
        foreach (var p in poor.Take(600))
        {
            var photo = new PhotoViewModel(p.Record, _main, SelectInItems);
            var item = new ReviewItemViewModel(photo, "Remove", p.Reason + "\n" + ReviewItemViewModel.DescribeFile(p.Record, root));
            item.MarkedChanged += _ => UpdateMarked();
            Items.Add(item);
        }
        Summary = poor.Count == 0 ? "No poor photos at this strictness."
            : poor.Count > 600 ? $"{poor.Count:N0} poor photos; showing the worst 600. Remove some or lower the strictness."
            : $"{poor.Count:N0} poor photos  ·  {Formatting.Bytes(poor.Sum(p => p.Record.Size))}";
        UpdateMarked();
    }

    private void SelectInItems(PhotoViewModel photo, SelectMode mode) =>
        _main.ApplySelection(Items.Select(i => i.Photo).ToList(), photo, mode);

    private void UpdateMarked()
    {
        var marked = Items.Where(i => i.Marked).ToList();
        MarkedCount = marked.Count;
        MarkedText = marked.Count == 0 ? "Nothing ticked" : $"{marked.Count:N0} ticked  ·  {Formatting.Bytes(marked.Sum(i => i.Record.Size))}";
        RemoveMarkedCommand.NotifyCanExecuteChanged();
        DeleteMarkedCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void MarkAll() { foreach (var i in Items) i.Marked = true; }

    [RelayCommand]
    private void ClearMarks() { foreach (var i in Items) i.Marked = false; }

    [RelayCommand(CanExecute = nameof(HasMarked))]
    private Task RemoveMarkedAsync() => _main.RemoveAsync(Items.Where(i => i.Marked).Select(i => i.Record).ToList(), fromDisk: false);

    [RelayCommand(CanExecute = nameof(HasMarked))]
    private Task DeleteMarkedAsync() => _main.RemoveAsync(Items.Where(i => i.Marked).Select(i => i.Record).ToList(), fromDisk: true);

    private bool HasMarked() => MarkedCount > 0;

    private void OnPhotosRemoved(IReadOnlySet<long> ids)
    {
        foreach (var item in Items.Where(i => ids.Contains(i.Record.Id)).ToList())
        {
            item.Photo.ReleaseThumbnail();
            Items.Remove(item);
        }
        UpdateMarked();
    }
}
