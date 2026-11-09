using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoLibrary.App.Services;
using PhotoLibrary.Core.Db;
using PhotoLibrary.Core.Import;
using PhotoLibrary.Core.Scanning;

namespace PhotoLibrary.App.ViewModels;

public partial class SimilarItemViewModel(CheckItem item, AppState state) : ViewModelBase
{
    public CheckItem Item { get; } = item;
    public string NewName => Item.Record.FileName;
    public string NewDetails => $"{Formatting.Taken(Item.Record)}\n{Formatting.Summary(Item.Record)}";
    public string LibraryName => Item.Match?.FileName ?? "";
    public string LibraryDetails => Item.Match is { } m ? $"{Formatting.Taken(m)}\n{Formatting.Summary(m)}\n{Path.GetRelativePath(state.LibraryRoot, m.Path)}" : "";
    public string How => Item.How;

    [ObservableProperty] public partial Bitmap? NewThumb { get; set; }
    [ObservableProperty] public partial Bitmap? LibraryThumb { get; set; }

    public bool Selected
    {
        get => Item.Selected;
        set { Item.Selected = value; OnPropertyChanged(); }
    }

    public async Task LoadThumbsAsync(CancellationToken ct)
    {
        NewThumb = await Load(Item.Record, ct);
        if (Item.Match is { } m) LibraryThumb = await Load(m, ct);
    }

    private async Task<Bitmap?> Load(MediaRecord r, CancellationToken ct)
    {
        var p = await state.Thumbs.GetAsync(r, ct);
        return p is null ? null : await BitmapLoader.FromFileAsync(p, 200, ct);
    }
}

public sealed record MissingItemViewModel(string Name, string Details);

public partial class ImportViewModel(AppState state, MainViewModel main) : ViewModelBase
{
    private CancellationTokenSource? _cts;
    private CheckReport? _report;

    [ObservableProperty] public partial string FolderPath { get; set; } = "";
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; } = "Pick a folder (a Google Takeout export, a card dump, anything) and press Check.";
    [ObservableProperty] public partial double ProgressPercent { get; set; }
    [ObservableProperty] public partial bool ProgressIndeterminate { get; set; }
    [ObservableProperty] public partial bool HasReport { get; set; }
    [ObservableProperty] public partial string SummaryText { get; set; } = "";
    [ObservableProperty] public partial string SimilarHeader { get; set; } = "";
    [ObservableProperty] public partial string MissingHeader { get; set; } = "";
    [ObservableProperty] public partial bool ExactOnly { get; set; }
    [ObservableProperty] public partial bool ImportDone { get; set; }
    [ObservableProperty] public partial string ResultText { get; set; } = "";

    public ObservableCollection<SimilarItemViewModel> Similar { get; } = [];
    public ObservableCollection<MissingItemViewModel> Missing { get; } = [];

    public bool CanCheck => !IsBusy && Directory.Exists(FolderPath);
    public bool CanImport => !IsBusy && HasReport && !ImportDone && _report is { } r && (r.Missing > 0 || r.Items.Any(i => i.Status == MatchStatus.Similar && i.Selected));
    public string ImportButtonText => _report is { } r
        ? $"Import {r.Missing + r.Items.Count(i => i.Status == MatchStatus.Similar && i.Selected):N0} files into library"
        : "Import";

    partial void OnFolderPathChanged(string value) => OnPropertyChanged(nameof(CanCheck));
    partial void OnIsBusyChanged(bool value) { OnPropertyChanged(nameof(CanCheck)); OnPropertyChanged(nameof(CanImport)); }

    private IProgress<ScanProgress> Progress => new Progress<ScanProgress>(p =>
    {
        ProgressIndeterminate = p.Total == 0;
        ProgressPercent = p.Total == 0 ? 0 : 100.0 * p.Done / p.Total;
        StatusText = p.Total == 0 ? p.Phase : $"{p.Phase}: {p.Done:N0} / {p.Total:N0}" + (p.Current is null ? "" : $"  —  {p.Current}");
    });

    [RelayCommand]
    public async Task CheckAsync()
    {
        if (!CanCheck) return;
        IsBusy = true;
        ImportDone = false;
        HasReport = false;
        ResultText = "";
        Similar.Clear();
        Missing.Clear();
        _cts = new CancellationTokenSource();
        try
        {
            var report = await state.Importer.CheckAsync(state.LibraryRoot, FolderPath, state.Library, ExactOnly, Progress, _cts.Token);
            _report = report;
            var skipped = report.Skipped.Count == 0 ? "" :
                "\nIgnored non-media: " + string.Join(", ", report.Skipped.OrderByDescending(k => k.Value).Select(k => $"{k.Key} ×{k.Value}"));
            SummaryText = $"{report.Items.Count:N0} photos and videos checked\n" +
                          $"   {report.Present:N0} already in the library (identical file)\n" +
                          $"   {report.Similar:N0} similar (the library has another version of the same shot)\n" +
                          $"   {report.Missing:N0} missing" + skipped;
            SimilarHeader = report.Similar == 0 ? "" : $"Similar ({report.Similar:N0}) — tick any you want imported as well";
            MissingHeader = report.Missing == 0 ? "" : $"Missing ({report.Missing:N0}) — these will be imported";
            foreach (var i in report.Items.Where(i => i.Status == MatchStatus.Similar))
            {
                var vm = new SimilarItemViewModel(i, state);
                vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SimilarItemViewModel.Selected)) { OnPropertyChanged(nameof(CanImport)); OnPropertyChanged(nameof(ImportButtonText)); } };
                Similar.Add(vm);
            }
            foreach (var i in report.Items.Where(i => i.Status == MatchStatus.Missing))
                Missing.Add(new MissingItemViewModel(Path.GetRelativePath(report.Folder, i.Record.Path), Formatting.Taken(i.Record) + "  ·  " + Formatting.Summary(i.Record)));
            HasReport = true;
            StatusText = report.Missing == 0 && report.Similar == 0 ? "Everything in this folder is already in your library." : "Check complete.";
            OnPropertyChanged(nameof(CanImport));
            OnPropertyChanged(nameof(ImportButtonText));
            foreach (var s in Similar) await s.LoadThumbsAsync(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Cancelled.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = "Error: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
            ProgressPercent = 0;
            ProgressIndeterminate = false;
        }
    }

    [RelayCommand]
    public async Task ImportAsync()
    {
        if (!CanImport || _report is null) return;
        IsBusy = true;
        _cts = new CancellationTokenSource();
        try
        {
            var n = await state.Importer.ImportAsync(_report, state.Albums.Folders(), Progress, _cts.Token);
            var errors = _report.Items.Count(i => i.Error is not null);
            ResultText = $"Imported {n:N0} files into {state.LibraryRoot}" + (errors > 0 ? $" ({errors} failed — see the import log)" : ".");
            ImportDone = true;
            StatusText = "Import finished.";
            await main.RescanAsync();
        }
        catch (OperationCanceledException)
        {
            StatusText = "Cancelled.";
        }
        finally
        {
            IsBusy = false;
            ProgressPercent = 0;
            OnPropertyChanged(nameof(CanImport));
        }
    }

    public void Cancel() => _cts?.Cancel();
}
