using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using PhotoLibrary.App.Services;
using PhotoLibrary.Core;
using PhotoLibrary.Core.Db;

namespace PhotoLibrary.App.ViewModels;

/// <summary>How a click changes the selection: replace it, toggle this photo, or extend from the anchor.</summary>
public enum SelectMode { Single, Toggle, Range }

/// <summary>One tile in a grid. The thumbnail is loaded when the tile scrolls into view and dropped when it leaves.</summary>
public partial class PhotoViewModel(MediaRecord record, MainViewModel main, Action<PhotoViewModel, SelectMode> onSelect) : ViewModelBase
{
    private CancellationTokenSource? _cts;

    public MediaRecord Record { get; } = record;

    /// <summary>The shell, so a tile's context menu can reach the selection-wide commands.</summary>
    public MainViewModel Main { get; } = main;

    [ObservableProperty]
    public partial Bitmap? Thumbnail { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    public partial bool ThumbnailFailed { get; set; }

    public string FileName => Record.FileName;

    /// <summary>What a tile shows when there is no thumbnail.</summary>
    public string FailedText => IsVideo && !Core.Imaging.ImageDecoder.FfmpegAvailable
        ? $"{Record.FileName}\n\nVideo previews need ffmpeg (Settings)"
        : Record.FileName;
    public int TileSize => Main.State.Settings.TileSize;
    public bool IsVideo => Record.Kind == MediaKind.Video;
    /// <summary>"▶ 3:10", or "▶ 1:20 / 3:10" when a search matched a moment inside the video.</summary>
    private string VideoBadge
    {
        get
        {
            var length = Formatting.Duration(Record.DurationMs);
            if (AtMs is { } at) return length.Length > 0 ? $"▶ {Formatting.Duration(at)} / {length}" : $"▶ at {Formatting.Duration(at)}";
            return length.Length > 0 ? "▶ " + length : "▶";
        }
    }

    /// <summary>For a video found by search: the moment of the best-matching frame.</summary>
    public int? AtMs { get; init; }

    public string Badge => IsVideo ? VideoBadge : Core.Pairs.FilePairs.Badge(Record.PairKind, Record.Extension) is { } pair ? pair : Record.Extension.TrimStart('.').ToUpperInvariant() is var e && e is "JPG" or "JPEG" or "PNG" ? "" : Record.Extension.TrimStart('.').ToUpperInvariant();
    public string ToolTipText => $"{Record.FileName}\n{Formatting.Taken(Record)}\n{Formatting.Summary(Record)}"
                                 + (Record.PairKind != Core.Pairs.PairKind.None ? "\n" + Core.Pairs.FilePairs.Describe(Record.PairKind) : "")
                                 + (AlbumNames.Length > 0 ? $"\nIn: {AlbumNames}" : "");

    /// <summary>One coloured dot per album the photo is in, shown in the tile's corner.</summary>
    [ObservableProperty] public partial IReadOnlyList<IBrush> AlbumBrushes { get; set; } = main.AlbumsFor(record.Id).Select(a => a.ColourBrush).ToList();
    public string AlbumNames { get; private set; } = string.Join(", ", main.AlbumsFor(record.Id).Select(a => a.Name));
    public bool HasAlbums => AlbumBrushes.Count > 0;

    public void RefreshAlbums()
    {
        var albums = Main.AlbumsFor(Record.Id);
        AlbumNames = string.Join(", ", albums.Select(a => a.Name));
        AlbumBrushes = albums.Select(a => a.ColourBrush).ToList();
        OnPropertyChanged(nameof(HasAlbums));
        OnPropertyChanged(nameof(ToolTipText));
    }

    public void Select(SelectMode mode = SelectMode.Single) => onSelect(this, mode);

    public async void EnsureThumbnail()
    {
        if (Thumbnail is not null || ThumbnailFailed || _cts is not null) return;
        var cts = _cts = new CancellationTokenSource();
        try
        {
            var path = await Main.State.Thumbs.GetAsync(Record, cts.Token);
            if (cts.IsCancellationRequested) return;
            if (path is null) { OnPropertyChanged(nameof(FailedText)); ThumbnailFailed = true; return; }
            var bmp = await BitmapLoader.FromFileAsync(path, null, cts.Token);
            if (cts.IsCancellationRequested) { bmp?.Dispose(); return; }
            if (bmp is null) ThumbnailFailed = true;
            else Thumbnail = bmp;
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(_cts, cts)) _cts = null;
        }
    }

    public void ReleaseThumbnail()
    {
        _cts?.Cancel();
        _cts = null;
        var t = Thumbnail;
        Thumbnail = null;
        t?.Dispose();
    }
}
