using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoLibrary.App.Services;
using PhotoLibrary.Core;
using PhotoLibrary.Core.Db;
using PhotoLibrary.Core.Organise;
using PhotoLibrary.Core.Scanning;

namespace PhotoLibrary.App.ViewModels;

public sealed record PlanRowViewModel(string Action, string From, string To, string Why);

public partial class PreviousRunViewModel(LibraryDb.OrganiseRun run, OrganiseViewModel owner) : ViewModelBase
{
    public long Id => run.Id;
    public string Text => $"{run.Started:g} — {run.Moved:N0} moved, {run.Errors} errors" + (run.Undone ? " (undone)" : "");
    public bool CanUndo => !run.Undone && run.Moved > 0;

    [RelayCommand]
    public Task UndoAsync() => owner.UndoAsync(Id);
}

public partial class OrganiseViewModel(AppState state, MainViewModel main) : ViewModelBase
{
    private CancellationTokenSource? _cts;
    private OrganisePlan? _plan;

    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; } = "Preview first; nothing moves until you press Organise.";
    [ObservableProperty] public partial double ProgressPercent { get; set; }
    [ObservableProperty] public partial bool UseModifiedTime { get; set; }
    [ObservableProperty] public partial bool HasPlan { get; set; }
    [ObservableProperty] public partial string SummaryText { get; set; } = "";
    [ObservableProperty] public partial bool Confirmed { get; set; }
    [ObservableProperty] public partial string ResultText { get; set; } = "";
    [ObservableProperty] public partial bool PlanTruncated { get; set; }

    public ObservableCollection<PlanRowViewModel> Rows { get; } = [];
    public ObservableCollection<PreviousRunViewModel> PreviousRuns { get; } = [];

    public bool CanApply => !IsBusy && HasPlan && Confirmed && _plan is { Items.Count: > 0 };
    partial void OnConfirmedChanged(bool value) => OnPropertyChanged(nameof(CanApply));
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanApply));
    partial void OnUseModifiedTimeChanged(bool value) { HasPlan = false; Confirmed = false; Rows.Clear(); OnPropertyChanged(nameof(CanApply)); }

    private IProgress<ScanProgress> Progress => new Progress<ScanProgress>(p =>
    {
        ProgressPercent = p.Total == 0 ? 0 : 100.0 * p.Done / p.Total;
        StatusText = $"{p.Phase}: {p.Done:N0} / {p.Total:N0}" + (p.Current is null ? "" : $"  —  {p.Current}");
    });

    public void LoadPreviousRuns()
    {
        PreviousRuns.Clear();
        foreach (var r in state.Db.GetOrganiseRuns()) PreviousRuns.Add(new PreviousRunViewModel(r, this));
    }

    [RelayCommand]
    public async Task PreviewAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        Rows.Clear();
        Confirmed = false;
        _cts = new CancellationTokenSource();
        try
        {
            var scan = await state.Scanner.IndexTreeAsync(state.LibraryRoot, LibraryFolders.ExcludedFromIndex, true, Progress, _cts.Token);
            state.Library = scan.Records;
            var map = state.Albums.Folders();
            var plan = await Task.Run(() => Organizer.Plan(scan, UseModifiedTime, map), _cts.Token);
            _plan = plan;
            var bySource = string.Join("\n", plan.BySource.OrderByDescending(k => k.Value).Select(k => $"        {k.Value:N0} dated by {DateSources.Describe(k.Key)}"));
            var other = plan.OtherFiles.Count == 0 ? "" :
                "\nLeft where they are (not media): " + string.Join(", ", plan.OtherFiles.OrderByDescending(k => k.Value).Select(k => $"{k.Key} ×{k.Value}"));
            if (plan.Blocked.Count > 0)
                other += "\nLeft where they are because their date folder is an album: " +
                         string.Join(", ", plan.Blocked.OrderBy(k => k.Key).Select(k => $"{k.Key} ×{k.Value:N0}")) +
                         ". Change the folder in Settings › Library folders, or pick another date layout.";
            SummaryText = $"{plan.Total:N0} photos and videos in the library, {plan.Settled:N0} already in place.\n\n" +
                          $"   {plan.Moves:N0} will move into date folders like {map.Dates.Example}\n{bySource}\n" +
                          $"   {plan.Renamed:N0} of those get a _1/_2 suffix because another file with the same name lands on the same day\n" +
                          $"   {plan.Undated:N0} with no capture date → {LibraryFolders.Undated}\\" +
                          (UseModifiedTime || plan.Undated == 0 ? "" : "   (tick 'use file modified time' to date these instead)") + other;
            const int limit = 3000;
            foreach (var item in plan.Items.Take(limit))
                Rows.Add(new PlanRowViewModel(item.Action.ToString(), Path.GetRelativePath(plan.Root, item.Record.Path),
                    Path.GetRelativePath(plan.Root, item.Dest),
                    DateSources.Describe(item.DateSource)));
            PlanTruncated = plan.Items.Count > limit;
            HasPlan = true;
            StatusText = plan.Items.Count == 0 ? "Nothing to do — the library is already organised." : "Preview ready. Tick the box and press Organise to apply.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Cancelled.";
        }
        finally
        {
            IsBusy = false;
            ProgressPercent = 0;
            OnPropertyChanged(nameof(CanApply));
        }
    }

    [RelayCommand]
    public async Task ApplyAsync()
    {
        if (!CanApply || _plan is null) return;
        IsBusy = true;
        _cts = new CancellationTokenSource();
        try
        {
            var outcome = await Organizer.ApplyAsync(state.Db, _plan, Progress, _cts.Token);
            ResultText = $"Moved {outcome.Moved:N0} files, {outcome.Errors} errors, {outcome.EmptyDirsRemoved} empty folders removed.\nUndo log: {outcome.Manifest}"
                         + (outcome.ErrorMessages.Count > 0 ? "\n" + string.Join("\n", outcome.ErrorMessages.Take(10)) : "");
            HasPlan = false;
            Confirmed = false;
            Rows.Clear();
            StatusText = "Done.";
            LoadPreviousRuns();
            await main.RescanAsync();
        }
        catch (OperationCanceledException)
        {
            StatusText = "Stopped — files moved so far are logged and can be undone.";
            LoadPreviousRuns();
            await main.RescanAsync();
        }
        finally
        {
            IsBusy = false;
            ProgressPercent = 0;
            OnPropertyChanged(nameof(CanApply));
        }
    }

    public async Task UndoAsync(long runId)
    {
        if (IsBusy) return;
        IsBusy = true;
        _cts = new CancellationTokenSource();
        try
        {
            var (restored, skipped) = await Organizer.UndoAsync(state.Db, runId, Progress, _cts.Token);
            ResultText = $"Undo: {restored:N0} files moved back" + (skipped > 0 ? $", {skipped} skipped (already moved or missing)." : ".");
            StatusText = "Undo finished.";
            HasPlan = false;
            Rows.Clear();
            LoadPreviousRuns();
            await main.RescanAsync();
        }
        finally
        {
            IsBusy = false;
            ProgressPercent = 0;
        }
    }

    public void Cancel() => _cts?.Cancel();
}
