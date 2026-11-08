using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoLibrary.AI;
using PhotoLibrary.AI.Clip;
using PhotoLibrary.App.Services;
using PhotoLibrary.Core;
using PhotoLibrary.Core.Video;

namespace PhotoLibrary.App.ViewModels;

/// <summary>One row in the model picker.</summary>
public sealed partial class ModelOption(ClipModelSpec spec) : ViewModelBase
{
    public ClipModelSpec Spec { get; } = spec;
    public string Name => Spec.DisplayName;
    public string Description => Spec.Description;
    public string SizeText => Spec.SizeText;

    [ObservableProperty] public partial bool Installed { get; set; }
    [ObservableProperty] public partial bool IsActive { get; set; }
    [ObservableProperty] public partial string IndexText { get; set; } = "";

    public string Label => IsActive ? $"{Name}  (in use)" : Installed ? $"{Name}  (downloaded)" : $"{Name}  ·  {SizeText}";
    partial void OnInstalledChanged(bool value) => OnPropertyChanged(nameof(Label));
    partial void OnIsActiveChanged(bool value) => OnPropertyChanged(nameof(Label));
}

/// <summary>The Settings tab. Every change is saved as soon as it is made.</summary>
public partial class SettingsViewModel : ViewModelBase
{
    private readonly MainViewModel _main;
    private CancellationTokenSource? _downloadCts;

    private AppState State => _main.State;
    private AppSettings Settings => State.Settings;
    private AiEngine Ai => State.Ai;

    public SettingsViewModel(MainViewModel main)
    {
        _main = main;
        foreach (var spec in ClipModelCatalog.All) Models.Add(new ModelOption(spec));
        SearchMinZ = Settings.SearchMinZ;
        SearchMaxResults = Settings.SearchMaxResults;
        SimilarMinScore = Settings.SimilarMinScore;
        VideoFrameInterval = Settings.VideoFrameInterval;
        TileSize = Settings.TileSize;
        foreach (var d in GpuDevices.List()) Gpus.Add(d);
        HasGpu = Gpus.Count > 0;
        SelectedGpu = Gpus.FirstOrDefault(g => g.Index == Settings.GpuDevice) ?? Gpus.FirstOrDefault();
        UseGpu = Settings.UseGpu && HasGpu;
        main.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.LibraryRoot)) OnPropertyChanged(nameof(LibraryRoot));
        };
        main.Ai.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AiPanelViewModel.IsIndexing)) { RefreshModels(); NotifyModelCommands(); }
        };
        RefreshModels();
        SelectedModel = Models.First(m => m.IsActive);
        RefreshLibrary();
        RefreshFfmpeg();
    }

    public string LibraryRoot => _main.LibraryRoot;
    private bool _refreshedOnce;

    // ---- video (ffmpeg)

    private CancellationTokenSource? _ffmpegCts;

    [ObservableProperty] public partial bool FfmpegInstalled { get; set; }
    [ObservableProperty] public partial string FfmpegStatus { get; set; } = "";
    [ObservableProperty] public partial bool IsDownloadingFfmpeg { get; set; }
    [ObservableProperty] public partial double FfmpegPercent { get; set; }
    [ObservableProperty] public partial string FfmpegMessage { get; set; } = "";

    public string HeicStatus => Core.Imaging.HeicDecoder.Available
        ? "HEIC: decoded by Windows."
        : "HEIC: Windows has no HEIF decoder (install \"HEIF Image Extensions\" and \"HEVC Video Extensions\" from the Microsoft Store), so ffmpeg is used.";

    public string FfmpegSizeText => $"Download: about {Ffmpeg.ApproxDownloadBytes / 1_000_000:N0} MB (static build from github.com/BtbN/FFmpeg-Builds).";

    public void RefreshFfmpeg()
    {
        var had = FfmpegInstalled;
        Ffmpeg.Refresh();
        FfmpegInstalled = Ffmpeg.Available;
        _main.UpdateNeedsFfmpeg();
        if (FfmpegInstalled && !had && _refreshedOnce)
        {
            var retried = RetryFailedThumbnails();
            FfmpegMessage = $"ffmpeg found. {retried:N0} videos and HEIC files will get thumbnails as they come into view.";
        }
        _refreshedOnce = true;
        FfmpegStatus = Ffmpeg.Available ? "ffmpeg: " + Ffmpeg.Describe() : "ffmpeg: " + Ffmpeg.Describe();
        OnPropertyChanged(nameof(HeicStatus));
        DownloadFfmpegCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanDownloadFfmpeg))]
    private async Task DownloadFfmpegAsync()
    {
        IsDownloadingFfmpeg = true;
        _ffmpegCts = new CancellationTokenSource();
        DownloadFfmpegCommand.NotifyCanExecuteChanged();
        try
        {
            var progress = new Progress<ToolDownloadProgress>(p =>
            {
                FfmpegPercent = p.BytesTotal == 0 ? 0 : 100.0 * p.BytesDone / p.BytesTotal;
                FfmpegMessage = p.Stage == "Downloading" ? $"Downloading…  {p.BytesDone / 1_000_000:N0} / {p.BytesTotal / 1_000_000:N0} MB" : p.Stage + "…";
            });
            await Ffmpeg.DownloadAsync(progress, _ffmpegCts.Token);
            var retried = RetryFailedThumbnails();
            FfmpegMessage = retried == 0 ? "ffmpeg installed." : $"ffmpeg installed. {retried:N0} videos and HEIC files will get thumbnails as they come into view.";
        }
        catch (OperationCanceledException)
        {
            FfmpegMessage = "Download cancelled.";
        }
        catch (Exception ex)
        {
            FfmpegMessage = "Download failed: " + ex.Message;
        }
        finally
        {
            IsDownloadingFfmpeg = false;
            FfmpegPercent = 0;
            RefreshFfmpeg();
        }
    }

    private bool CanDownloadFfmpeg() => !FfmpegInstalled && !IsDownloadingFfmpeg;

    [RelayCommand]
    private void CancelFfmpegDownload() => _ffmpegCts?.Cancel();

    /// <summary>After ffmpeg arrives: forget the "could not decode" markers for everything it can now read and redraw the grid.</summary>
    private int RetryFailedThumbnails()
    {
        var candidates = State.Db.Query("1=1").Where(r => r.Kind == MediaKind.Video
            || r.Path.EndsWith(".heic", StringComparison.OrdinalIgnoreCase) || r.Path.EndsWith(".heif", StringComparison.OrdinalIgnoreCase)).ToList();
        var n = State.Thumbs.ForgetFailures(candidates);
        Ai.ForgetUndecodable();
        _main.ReloadGroups();
        return n;
    }

    // ---- removed items

    [ObservableProperty] public partial int HiddenCount { get; set; }
    [ObservableProperty] public partial string HiddenText { get; set; } = "";

    public void RefreshLibrary()
    {
        HiddenCount = State.Db.CountHidden();
        HiddenText = HiddenCount == 0 ? "No items have been removed from the library."
            : HiddenCount == 1 ? "1 item was removed from the library and is kept out of imports. The file is still on disk."
            : $"{HiddenCount:N0} items were removed from the library and are kept out of imports. The files are still on disk.";
        RestoreHiddenCommand.NotifyCanExecuteChanged();
        _ = RefreshStorageAsync();
    }

    [RelayCommand(CanExecute = nameof(CanRestoreHidden))]
    private async Task RestoreHiddenAsync()
    {
        var n = await Task.Run(State.Db.RestoreHidden);
        RefreshLibrary();
        _main.StatusText = n == 0 ? "Nothing to restore." : $"Restored {n:N0} items. Rescanning…";
        if (n > 0) await _main.RescanAsync();
    }

    private bool CanRestoreHidden() => HiddenCount > 0 && !_main.IsBusy;

    // ---- storage

    public sealed record StorageLine(string Label, string Value, string Detail);

    public ObservableCollection<StorageLine> Storage { get; } = [];
    [ObservableProperty] public partial bool StorageBusy { get; set; }

    public async Task RefreshStorageAsync()
    {
        if (StorageBusy) return;
        StorageBusy = true;
        try
        {
            var s = await Task.Run(Ai.Storage);
            Storage.Clear();
            Storage.Add(new StorageLine("Library database", Formatting.Bytes(s.DatabaseBytes), $"{s.LibraryItems:N0} items in the library"));
            Storage.Add(new StorageLine("Thumbnail cache", Formatting.Bytes(s.ThumbnailBytes), $"{s.ThumbnailCount:N0} thumbnails"));
            Storage.Add(new StorageLine("Model downloads", Formatting.Bytes(s.ModelBytes), $"{Models.Count(m => m.Installed)} of {Models.Count} models installed"));
            foreach (var ix in s.Indexes)
            {
                var name = ClipModelCatalog.Find(ix.Model)?.DisplayName ?? ix.Model;
                var inUse = ix.Model == Settings.Model.Id ? "  (in use)" : "";
                Storage.Add(new StorageLine($"AI index: {name}{inUse}", Formatting.Bytes(ix.VectorBytes), $"{ix.Items:N0} photos  ·  {ix.Dimension}-d vectors  ·  {ix.Tags:N0} tags"));
            }
            Storage.Add(new StorageLine("Quality measurements", $"{s.QualityMeasured:N0} photos", "brightness, contrast and sharpness per photo"));
        }
        finally
        {
            StorageBusy = false;
        }
    }

    [RelayCommand]
    private Task RefreshStorage() => RefreshStorageAsync();

    // ---- models

    public ObservableCollection<ModelOption> Models { get; } = [];

    [ObservableProperty] public partial ModelOption? SelectedModel { get; set; }
    [ObservableProperty] public partial bool IsDownloading { get; set; }
    [ObservableProperty] public partial double DownloadPercent { get; set; }
    [ObservableProperty] public partial string ModelMessage { get; set; } = "";

    public bool SelectedIsInstalled => SelectedModel?.Installed == true;
    public bool SelectedIsActive => SelectedModel?.IsActive == true;
    public bool CanRemoveSelected => SelectedIsInstalled && !SelectedIsActive && !IsDownloading;

    /// <summary>Re-read what is on disk and in the database for every model.</summary>
    public void RefreshModels()
    {
        foreach (var m in Models)
        {
            m.Installed = new ClipModelFiles(m.Spec).IsInstalled;
            m.IsActive = m.Spec.Id == Settings.Model.Id;
            var (embedded, total) = Ai.IndexStatus(m.Spec.Id);
            m.IndexText = total == 0 ? "" : embedded == 0 ? "Not indexed yet." : embedded >= total ? $"All {total:N0} items indexed." : $"{embedded:N0} of {total:N0} items indexed.";
        }
        OnPropertyChanged(nameof(SelectedIsInstalled));
        OnPropertyChanged(nameof(SelectedIsActive));
        OnPropertyChanged(nameof(CanRemoveSelected));
    }

    partial void OnSelectedModelChanged(ModelOption? value)
    {
        ModelMessage = "";
        OnPropertyChanged(nameof(SelectedIsInstalled));
        OnPropertyChanged(nameof(SelectedIsActive));
        OnPropertyChanged(nameof(CanRemoveSelected));
        NotifyModelCommands();
    }

    partial void OnIsDownloadingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanRemoveSelected));
        NotifyModelCommands();
    }

    private void NotifyModelCommands()
    {
        UseModelCommand.NotifyCanExecuteChanged();
        DownloadModelCommand.NotifyCanExecuteChanged();
        RemoveModelCommand.NotifyCanExecuteChanged();
    }

    private bool ModelsLocked => IsDownloading || _main.Ai.IsIndexing;

    [RelayCommand(CanExecute = nameof(CanUseModel))]
    private void UseModel()
    {
        if (SelectedModel is not { } m) return;
        Settings.Model = m.Spec;
        RefreshModels();
        var (embedded, total) = Ai.IndexStatus(m.Spec.Id);
        ModelMessage = embedded >= total
            ? $"Now using {m.Name}."
            : $"Now using {m.Name}. Each model has its own index, so run \"Index new photos\" on the AI Tools tab to search with it.";
        NotifyModelCommands();
    }

    private bool CanUseModel() => SelectedModel is { Installed: true, IsActive: false } && !ModelsLocked;

    [RelayCommand(CanExecute = nameof(CanDownloadModel))]
    private async Task DownloadModelAsync()
    {
        if (SelectedModel is not { } m) return;
        var files = new ClipModelFiles(m.Spec);
        IsDownloading = true;
        _downloadCts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<ClipModelFiles.DownloadProgress>(p =>
            {
                DownloadPercent = p.BytesTotal == 0 ? 0 : 100.0 * p.BytesDone / p.BytesTotal;
                ModelMessage = p.CurrentFile.Length == 0 ? "Download complete." : $"Downloading {p.CurrentFile}…  {p.BytesDone / 1_000_000:N0} / {p.BytesTotal / 1_000_000:N0} MB";
            });
            await files.DownloadAsync(progress, _downloadCts.Token);
            ModelMessage = $"{m.Name} downloaded. Select \"Use this model\" to switch to it.";
        }
        catch (OperationCanceledException)
        {
            ModelMessage = "Download cancelled. Finished files are kept, so it can resume later.";
        }
        catch (Exception ex)
        {
            ModelMessage = "Download failed: " + ex.Message;
        }
        finally
        {
            IsDownloading = false;
            DownloadPercent = 0;
        }
        RefreshModels();
        NotifyModelCommands();
    }

    private bool CanDownloadModel() => SelectedModel is { Installed: false } && !IsDownloading;

    [RelayCommand]
    private void CancelDownload() => _downloadCts?.Cancel();

    [RelayCommand(CanExecute = nameof(CanRemoveModel))]
    private void RemoveModel()
    {
        if (SelectedModel is not { } m) return;
        try
        {
            new ClipModelFiles(m.Spec).Delete();
            ModelMessage = $"{m.Name} removed from disk. Its index is kept, so downloading it again later needs no re-index.";
        }
        catch (Exception ex)
        {
            ModelMessage = "Could not remove: " + ex.Message;
        }
        RefreshModels();
        NotifyModelCommands();
        _ = RefreshStorageAsync();
    }

    private bool CanRemoveModel() => CanRemoveSelected;

    // ---- hardware

    public ObservableCollection<GpuDevice> Gpus { get; } = [];
    [ObservableProperty] public partial bool HasGpu { get; set; }
    [ObservableProperty] public partial bool UseGpu { get; set; }
    [ObservableProperty] public partial GpuDevice? SelectedGpu { get; set; }

    public string GpuHint => HasGpu
        ? "Runs the model on the graphics card through DirectML. Full-precision models index around ten times faster on a GPU; the int8 ones gain much less, so pair this with a full-precision model. Takes effect the next time the model loads."
        : "No DirectX 12 graphics adapter was found, so the model runs on the CPU.";

    public string DeviceStatus
    {
        get
        {
            var device = Ai.LoadedDevice;
            if (device is null) return "Model not loaded yet. It loads on the first search or index run.";
            var reason = Ai.GpuFallbackReason;
            return reason is null ? $"Model is running on {device}." : $"Model is running on {device}. GPU was requested but failed: {reason}";
        }
    }

    partial void OnUseGpuChanged(bool value)
    {
        if (Settings.UseGpu != value) Settings.UseGpu = value;
        OnPropertyChanged(nameof(DeviceStatus));
    }

    partial void OnSelectedGpuChanged(GpuDevice? value)
    {
        if (value is not null && Settings.GpuDevice != value.Index) Settings.GpuDevice = value.Index;
        OnPropertyChanged(nameof(DeviceStatus));
    }

    /// <summary>Called by the AI panel after it loads or unloads the model.</summary>
    public void RefreshDevice() => OnPropertyChanged(nameof(DeviceStatus));

    // ---- search tuning

    // Sliders bind doubles; the settings store the narrower types.
    [ObservableProperty] public partial double SearchMinZ { get; set; }
    [ObservableProperty] public partial double SearchMaxResults { get; set; }
    [ObservableProperty] public partial double SimilarMinScore { get; set; }

    public string SearchMinZText => SearchMinZ switch
    {
        < 2.5f => $"{SearchMinZ:0.0}  ·  loose: more results, more misses",
        < 3.5f => $"{SearchMinZ:0.0}  ·  balanced",
        _ => $"{SearchMinZ:0.0}  ·  strict: fewer, better results",
    };

    public string SimilarMinScoreText => $"{SimilarMinScore:0.00}  ·  " + (SimilarMinScore < 0.6f ? "loose" : SimilarMinScore < 0.75f ? "balanced" : "near-duplicates only");

    partial void OnSearchMinZChanged(double value)
    {
        Settings.SearchMinZ = (float)Math.Round(value, 1);
        OnPropertyChanged(nameof(SearchMinZText));
    }

    partial void OnSearchMaxResultsChanged(double value) => Settings.SearchMaxResults = (int)value;

    partial void OnSimilarMinScoreChanged(double value)
    {
        Settings.SimilarMinScore = (float)Math.Round(value, 2);
        OnPropertyChanged(nameof(SimilarMinScoreText));
    }

    // ---- video frames in the AI index

    [ObservableProperty] public partial int VideoFrameInterval { get; set; }
    public string VideoFrameIntervalText => $"every {VideoFrameInterval} s  ·  up to {Ai.VideoMaxFrames} frames per video";

    partial void OnVideoFrameIntervalChanged(int value)
    {
        Settings.VideoFrameInterval = value;
        OnPropertyChanged(nameof(VideoFrameIntervalText));
    }

    [RelayCommand]
    private void ReindexVideos()
    {
        Ai.ForgetVideoIndex();
        ModelMessage = "Video vectors cleared. Run “Index new photos” on the AI Tools tab to sample them again.";
        _main.Ai.Refresh();
    }

    [RelayCommand]
    private void ResetSearchDefaults()
    {
        SearchMinZ = 3f;
        SearchMaxResults = 400;
        SimilarMinScore = 0.6f;
    }

    // ---- appearance

    [ObservableProperty] public partial double TileSize { get; set; }

    public string TileSizeText => $"{TileSize:0} px";

    partial void OnTileSizeChanged(double value)
    {
        OnPropertyChanged(nameof(TileSizeText));
        if (Settings.TileSize == (int)value) return;
        Settings.TileSize = (int)value;
        _main.ReloadGroups();
    }
}
