using System.Security.Cryptography;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using PhotoLibrary.App.Services;
using PhotoLibrary.Core;
using PhotoLibrary.Core.Imaging;
using PhotoLibrary.Core.Vault;
using SkiaSharp;

namespace PhotoLibrary.App.ViewModels;

/// <summary>
/// One photo in an unlocked vault. Its thumbnail is decrypted and decoded in memory when the tile scrolls into
/// view and is never written anywhere; until then (and for videos) the tile is a grey box with a lock.
/// </summary>
public partial class VaultItemViewModel(VaultItem item, Func<byte[]?> key, Action<VaultItemViewModel> onSelect) : ViewModelBase
{
    private static readonly SemaphoreSlim DecodeGate = new(2, 2);
    private CancellationTokenSource? _cts;

    public VaultItem Item { get; } = item;
    public VaultItemInfo Info => Item.Info;

    [ObservableProperty] public partial Bitmap? Thumbnail { get; set; }
    [ObservableProperty] public partial bool IsSelected { get; set; }

    public int TileSize { get; init; } = 168;
    public string FileName => Info.OriginalName;
    public bool IsVideo => Info.Kind == MediaKind.Video;
    public string Badge => IsVideo ? "▶ " + Formatting.Duration(Info.DurationMs) : "";
    public string ToolTipText => $"{Info.OriginalName}\n{(Info.Taken is { } t ? t.ToString("ddd d MMM yyyy HH:mm") : "no date")}\n{Formatting.Bytes(Info.Size)}";

    public void Select() => onSelect(this);

    public async void EnsureThumbnail()
    {
        if (Thumbnail is not null || _cts is not null || IsVideo) return;
        var cts = _cts = new CancellationTokenSource();
        try
        {
            var bmp = await DecodeAsync(320, cts.Token);
            if (cts.IsCancellationRequested) { bmp?.Dispose(); return; }
            Thumbnail = bmp;
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(_cts, cts)) _cts = null;
        }
    }

    /// <summary>Decrypts and decodes into memory only; the decrypted bytes are wiped as soon as they are decoded.</summary>
    public async Task<Bitmap?> DecodeAsync(int maxEdge, CancellationToken ct)
    {
        await DecodeGate.WaitAsync(ct);
        try
        {
            return await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                if (key() is not { } k) return null;
                byte[]? plain = null;
                try
                {
                    plain = VaultStore.ReadContent(Item, k);
                    using var sk = ImageDecoder.DecodeInMemory(plain, Info.OriginalName, Info.Kind, maxEdge);
                    if (sk is null) return null;
                    using var data = sk.Encode(SKEncodedImageFormat.Png, 100);
                    if (data is null) return null;
                    using var ms = new MemoryStream(data.ToArray());
                    return (Bitmap?)new Bitmap(ms);
                }
                catch (Exception ex) when (ex is VaultAuthenticationException or IOException or ArgumentException or InvalidOperationException)
                {
                    return null;
                }
                finally
                {
                    if (plain is not null) CryptographicOperations.ZeroMemory(plain);
                }
            }, ct);
        }
        finally { DecodeGate.Release(); }
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
