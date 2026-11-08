namespace PhotoLibrary.Core;

public enum MediaKind { Image, Video }

public static class MediaKinds
{
    public static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".heic", ".heif", ".tif", ".tiff", ".gif", ".bmp", ".webp", ".psd", ".psb",
        ".cr2", ".cr3", ".dng", ".nef", ".arw", ".orf", ".rw2", ".raf",
    };

    public static readonly HashSet<string> VideoExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".m4v", ".3gp", ".mp", ".avi", ".mts", ".m2ts", ".wmv", ".mkv",
        ".cover", // Pixel motion photos exported by Google Takeout: "x.MP.COVER" is the MP4 clip beside "x.MP.COVER.jpg"
    };

    public static readonly HashSet<string> RawExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cr2", ".cr3", ".dng", ".nef", ".arw", ".orf", ".rw2", ".raf",
    };

    /// <summary>Housekeeping files that never count as "something in the folder".</summary>
    public static readonly HashSet<string> IgnoredNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "desktop.ini", "thumbs.db", ".ds_store",
    };

    public static MediaKind? KindOf(string path)
    {
        var ext = Path.GetExtension(path);
        if (ImageExt.Contains(ext)) return MediaKind.Image;
        if (VideoExt.Contains(ext)) return MediaKind.Video;
        return null;
    }

    public static bool IsMedia(string path) => KindOf(path) is not null;
    public static bool IsRaw(string path) => RawExt.Contains(Path.GetExtension(path));
}
