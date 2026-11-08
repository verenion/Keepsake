using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using PhotoLibrary.App.Services;
using PhotoLibrary.Core.Albums;
using PhotoLibrary.Core.Db;

namespace PhotoLibrary.App.ViewModels;

/// <summary>One album in the list: its row plus a cover thumbnail loaded on demand.</summary>
public partial class AlbumViewModel(Album album, AppState state) : ViewModelBase
{
    private CancellationTokenSource? _coverCts;

    public Album Album { get; private set; } = album;
    public long Id => Album.Id;

    [ObservableProperty] public partial string Name { get; set; } = album.Name;
    [ObservableProperty] public partial int Count { get; set; } = album.Count;
    [ObservableProperty] public partial IBrush ColourBrush { get; set; } = Brush(album.Colour);
    [ObservableProperty] public partial Bitmap? Cover { get; set; }
    [ObservableProperty] public partial bool IsPhysical { get; set; } = album.IsPhysical;
    [ObservableProperty] public partial bool IsSecure { get; set; } = album.IsSecure;

    public string KindText => IsSecure ? "Secure" : IsPhysical ? "Folder" : "Virtual";
    public string CountText => Count == 1 ? "1 item" : $"{Count:N0} items";
    public string Description => IsSecure
        ? $"Secure album  ·  {CountText}  ·  encrypted"
        : IsPhysical
        ? $"Folder album  ·  {CountText}  ·  {Path.GetRelativePath(state.LibraryRoot, state.Albums.FolderFor(Album))}"
        : $"Virtual album  ·  {CountText}";

    partial void OnCountChanged(int value) { OnPropertyChanged(nameof(CountText)); OnPropertyChanged(nameof(Description)); }
    partial void OnIsPhysicalChanged(bool value) { OnPropertyChanged(nameof(KindText)); OnPropertyChanged(nameof(Description)); }

    private static IBrush Brush(string colour) =>
        Color.TryParse(colour, out var c) ? new SolidColorBrush(c) : new SolidColorBrush(Color.Parse(Album.Palette[0]));

    /// <summary>Re-reads the row (name, colour, kind, count, cover).</summary>
    public void Update(Album fresh)
    {
        var coverChanged = fresh.CoverFileId != Album.CoverFileId || (Album.CoverFileId is null && fresh.Count != Count);
        Album = fresh;
        Name = fresh.Name;
        Count = fresh.Count;
        IsPhysical = fresh.IsPhysical;
        IsSecure = fresh.IsSecure;
        ColourBrush = Brush(fresh.Colour);
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(KindText));
        if (fresh.IsSecure) ReleaseCover();
        else if (coverChanged) ReloadCover();
    }

    public void ReloadCover()
    {
        ReleaseCover();
        _ = LoadCoverAsync();
    }

    public async Task LoadCoverAsync()
    {
        if (Cover is not null || _coverCts is not null || Album.IsSecure) return; // never decrypt a vault for a cover
        var cts = _coverCts = new CancellationTokenSource();
        try
        {
            var record = await Task.Run(() =>
            {
                var r = Album.CoverFileId is { } id ? state.Db.Query("id=@id AND in_library=1", ("@id", id)).FirstOrDefault() : null;
                return r ?? state.Db.Query("in_library=1 AND id IN (SELECT file_id FROM album_items WHERE album_id=@a) ORDER BY taken IS NULL, taken, path LIMIT 1", ("@a", Album.Id)).FirstOrDefault();
            }, cts.Token);
            if (record is null || cts.IsCancellationRequested) return;
            var path = await state.Thumbs.GetAsync(record, cts.Token);
            if (path is null || cts.IsCancellationRequested) return;
            var bmp = await BitmapLoader.FromFileAsync(path, 200, cts.Token);
            if (cts.IsCancellationRequested) { bmp?.Dispose(); return; }
            Cover = bmp;
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(_coverCts, cts)) _coverCts = null;
        }
    }

    public void ReleaseCover()
    {
        _coverCts?.Cancel();
        _coverCts = null;
        var c = Cover;
        Cover = null;
        c?.Dispose();
    }
}
