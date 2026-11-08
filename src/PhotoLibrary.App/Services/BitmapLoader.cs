using Avalonia.Media.Imaging;
using PhotoLibrary.Core.Db;
using PhotoLibrary.Core.Thumbnails;
using SkiaSharp;

namespace PhotoLibrary.App.Services;

public static class BitmapLoader
{
    /// <summary>Loads a cached thumbnail file as an Avalonia bitmap (off the UI thread).</summary>
    public static Task<Bitmap?> FromFileAsync(string path, int width, CancellationToken ct) => Task.Run(() =>
    {
        try
        {
            using var fs = File.OpenRead(path);
            return (Bitmap?)Bitmap.DecodeToWidth(fs, width);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }, ct);

    /// <summary>Decodes a large preview of a record straight from the original file.</summary>
    public static Task<Bitmap?> PreviewAsync(MediaRecord r, int maxEdge, CancellationToken ct, int? atMs = null) => Task.Run(() =>
    {
        using var sk = ThumbnailService.Preview(r, maxEdge, atMs);
        if (sk is null) return null;
        using var data = sk.Encode(SKEncodedImageFormat.Png, 100);
        if (data is null) return null;
        using var ms = new MemoryStream(data.ToArray());
        return (Bitmap?)new Bitmap(ms);
    }, ct);
}
