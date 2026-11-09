using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoLibrary.App.Services;
using PhotoLibrary.Core.Backup;

namespace PhotoLibrary.App.ViewModels;

/// <summary>
/// Backs the library up to a chosen folder, on demand or on a schedule. Owned by the main window rather than
/// the Settings tab, so scheduled backups run whichever tab is open. Backups only run while Keepsake is open.
/// </summary>
public sealed partial class BackupViewModel : ViewModelBase
{
    public sealed record ScheduleOption(string Key, string Label);

    private readonly MainViewModel _main;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(30) };
    private CancellationTokenSource? _cts;
    private bool _ranThisSession;

    private AppSettings Settings => _main.State.Settings;

    public BackupViewModel(MainViewModel main)
    {
        _main = main;
        Folder = Settings.BackupFolder;
        Schedule = ScheduleOptions.FirstOrDefault(o => o.Key == Settings.BackupSchedule) ?? ScheduleOptions[2];
        _timer.Tick += (_, _) => RunIfDue();
    }

    public ScheduleOption[] ScheduleOptions { get; } =
    [
        new("manual", "Only when I click Back up now"),
        new("startup", "Each time Keepsake opens"),
        new("daily", "Once a day"),
        new("weekly", "Once a week"),
    ];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasFolder), nameof(CanRun))]
    public partial string? Folder { get; set; }

    [ObservableProperty] public partial ScheduleOption? Schedule { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanRun))]
    public partial bool IsRunning { get; set; }

    [ObservableProperty] public partial double Progress { get; set; }
    [ObservableProperty] public partial string? ProgressText { get; set; }

    public bool HasFolder => Folder is not null;
    public bool CanRun => HasFolder && !IsRunning;

    public string LastBackupText
    {
        get
        {
            var last = Settings.LastBackupUtc is { } t ? $"Last successful backup: {t.ToLocalTime():d MMM yyyy, HH:mm}." : "No successful backup yet.";
            return Settings.LastBackupSummary is { } s ? $"{last} {s}" : last;
        }
    }

    partial void OnScheduleChanged(ScheduleOption? value)
    {
        if (value is not null && value.Key != Settings.BackupSchedule) Settings.BackupSchedule = value.Key;
    }

    /// <summary>Starts the schedule. Called once the window is open and the first library scan has started.</summary>
    public void Start()
    {
        _timer.Start();
        // Give the startup scan a head start before checking.
        DispatcherTimer.RunOnce(RunIfDue, TimeSpan.FromMinutes(1));
    }

    public void Cancel() => _cts?.Cancel();

    public void SetFolder(string? folder)
    {
        if (folder is not null && BackupService.Validate(_main.State.LibraryRoot, folder) is { } problem)
        {
            ProgressText = problem;
            return;
        }
        Settings.BackupFolder = folder;
        Folder = folder;
        ProgressText = null;
    }

    [RelayCommand]
    private void ClearFolder() => SetFolder(null);

    [RelayCommand]
    private Task BackUpNow() => RunAsync();

    private void RunIfDue()
    {
        if (IsRunning || Folder is null || _main.IsBusy) return;   // a busy scan/organise: try again next tick
        var last = Settings.LastBackupUtc;
        var due = Settings.BackupSchedule switch
        {
            "startup" => !_ranThisSession,
            "daily" => last is null || DateTime.UtcNow - last > TimeSpan.FromHours(23),
            "weekly" => last is null || DateTime.UtcNow - last > TimeSpan.FromDays(7) - TimeSpan.FromHours(1),
            _ => false,
        };
        if (due) _ = RunAsync();
    }

    private async Task RunAsync()
    {
        if (!CanRun) return;
        var library = _main.State.LibraryRoot;
        var folder = Folder!;
        var db = _main.State.Db;
        IsRunning = true;
        _ranThisSession = true;
        Progress = 0;
        ProgressText = "Starting backup…";
        _cts = new CancellationTokenSource();
        var progress = new Progress<BackupProgress>(p =>
        {
            Progress = p.Total == 0 ? 0 : 100.0 * p.Done / p.Total;
            ProgressText = p.Current is null ? "Saving the database…" : $"{p.Done:N0} / {p.Total:N0}  —  {p.Current}";
        });
        string summary;
        try
        {
            var ct = _cts.Token;
            var r = await Task.Run(() => BackupService.Run(library, folder, db.SnapshotTo, progress, ct), ct);
            summary = Describe(r);
            if (r.Ok) Settings.LastBackupUtc = DateTime.UtcNow;
            ProgressText = r.Ok ? null : "Problems: " + string.Join("  •  ", r.Errors.Take(5)) + (r.Errors.Count > 5 ? $"  (+{r.Errors.Count - 5:N0} more)" : "");
        }
        catch (OperationCanceledException)
        {
            summary = "The last backup was cancelled; the next one carries on where it stopped.";
            ProgressText = null;
        }
        catch (Exception ex)
        {
            summary = $"The last backup failed: {ex.Message}";
            ProgressText = null;
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            IsRunning = false;
        }
        Settings.LastBackupSummary = summary;
        OnPropertyChanged(nameof(LastBackupText));
        if (!_main.IsBusy) _main.StatusText = "Backup: " + summary;
    }

    private static string Describe(BackupResult r)
    {
        var parts = new List<string>();
        if (r.Copied > 0) parts.Add($"{r.Copied:N0} new");
        if (r.Updated > 0) parts.Add($"{r.Updated:N0} changed");
        if (r.Archived > 0) parts.Add($"{r.Archived:N0} removed from the library and archived");
        var what = parts.Count == 0 ? "nothing new to copy" : string.Join(", ", parts) + $" ({FormatBytes(r.BytesCopied)})";
        var errors = r.Ok ? "" : $", {r.Errors.Count:N0} problem{(r.Errors.Count == 1 ? "" : "s")}";
        return $"{(r.Ok ? "Done" : "Finished with problems")}: {what}{errors}, in {r.Elapsed:h\\:mm\\:ss}.";
    }

    private static string FormatBytes(long b) => b switch
    {
        >= 1L << 30 => (b / (double)(1L << 30)).ToString("0.0", CultureInfo.CurrentCulture) + " GB",
        >= 1L << 20 => (b / (double)(1L << 20)).ToString("0.0", CultureInfo.CurrentCulture) + " MB",
        _ => (b / 1024.0).ToString("0", CultureInfo.CurrentCulture) + " KB",
    };
}
