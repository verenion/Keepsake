using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Jpeg;
using MetadataExtractor.Formats.Png;
using MetadataExtractor.Formats.QuickTime;
using MeDirectory = MetadataExtractor.Directory;

namespace PhotoLibrary.Core.Metadata;

/// <summary>
/// Reads capture date and camera details. The date priority is the one validated against a hand-sorted
/// reference library (see DateSources): the camera's own timestamp wins, then a full date+time in the file
/// name, then the weaker EXIF dates, then Google Takeout's photoTakenTime (often just the upload time), then a
/// date-only file name. MetadataExtractor does the heavy lifting; the small fallback parsers cover the files it
/// can't read (some Pixel DNGs, old camera MOVs).
/// </summary>
public static class MetadataReader
{
    /// <summary>Bump when the fields read here change, so cached rows get re-read without re-hashing.</summary>
    public const int Version = 2;

    private sealed class Raw
    {
        public DateTime? Original, Digitized, Modified, Apple, Mvhd;
        public string? Make, Model;
        public int? Orientation;
        public long? DurationMs;
        public bool HadExif, HadMovie;
    }

    public static MediaInfo Read(string path)
    {
        var info = new MediaInfo { Kind = MediaKinds.KindOf(path) ?? MediaKind.Image };
        var raw = new Raw();
        IReadOnlyList<MeDirectory> dirs = Array.Empty<MeDirectory>();
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            dirs = ImageMetadataReader.ReadMetadata(fs);
        }
        catch (Exception ex) when (ex is ImageProcessingException or IOException or UnauthorizedAccessException
                                   or NotSupportedException or ArgumentException or IndexOutOfRangeException)
        {
            // unreadable or unknown container: the fallbacks below get a go
        }

        ReadFromExtractor(dirs, raw, info);

        // RAW files: IFD0 is often a reduced preview (Pixel DNGs: 1280px), so take the sensor IFD's size instead,
        // and its orientation when EXIF gave none.
        if (Path.GetExtension(path) is var psdExt && (psdExt.Equals(".psd", StringComparison.OrdinalIgnoreCase) || psdExt.Equals(".psb", StringComparison.OrdinalIgnoreCase))
            && Imaging.PsdDecoder.ReadSize(path) is { } psd)
        {
            info.Width = psd.Width;
            info.Height = psd.Height;
        }
        if (MediaKinds.IsRaw(path) && Imaging.TiffPreview.Inspect(path, readPreview: false) is { Width: > 0, Height: > 0 } tiff)
        {
            info.Width = tiff.Width;
            info.Height = tiff.Height;
            raw.Orientation ??= tiff.Orientation;
        }

        // Fallbacks: only when MetadataExtractor produced nothing for that part of the file.
        if (!raw.HadExif && info.Kind == MediaKind.Image)
        {
            var ex = ExifFallback.Read(path);
            if (ex.Found)
            {
                raw.Original ??= DateRules.ParseLoose(ex.DateTimeOriginal);
                raw.Digitized ??= DateRules.ParseLoose(ex.DateTimeDigitized);
                raw.Modified ??= DateRules.ParseLoose(ex.DateTime);
                raw.Make ??= Clean(ex.Make);
                raw.Model ??= Clean(ex.Model);
                raw.Orientation ??= ex.Orientation;
            }
        }
        if ((!raw.HadMovie || raw.DurationMs is null && info.Kind == MediaKind.Video) && (info.Kind == MediaKind.Video || LooksLikeIsoBmff(path)))
        {
            var qt = QuickTimeFallback.Read(path);
            raw.Apple ??= DateRules.ParseLoose(qt.AppleCreationDate);
            if (raw.Mvhd is null && qt.MvhdCreatedUtc is { } u) raw.Mvhd = DateRules.Valid(u.ToLocalTime());
            raw.DurationMs ??= qt.DurationMs;
        }

        info.Make = raw.Make;
        info.Model = raw.Model;
        info.Orientation = raw.Orientation;
        info.DurationMs = raw.DurationMs;

        var fileName = Path.GetFileName(path);
        DateTime? taken;
        string? source = null;
        if ((taken = raw.Original) is not null) source = DateSources.Exif;
        else if ((taken = raw.Apple) is not null) source = DateSources.VideoApple;
        else if ((taken = raw.Mvhd) is not null) source = DateSources.VideoMvhd;
        else if ((taken = DateRules.FromFileName(fileName, needTime: true)) is not null) source = DateSources.FileName;
        else if ((taken = raw.Digitized) is not null) source = DateSources.ExifDigitized;
        else if ((taken = raw.Modified) is not null) source = DateSources.ExifModified;
        else if ((taken = DateRules.FromFileName(fileName)) is not null) source = DateSources.FileName;

        // Takeout sidecar: only trusted over the weak sources, but its description/GPS are always worth keeping.
        var strong = source is DateSources.Exif or DateSources.VideoApple
                     || (source == DateSources.FileName && DateRules.FromFileName(fileName, needTime: true) is not null);
        if (TakeoutSidecar.For(path) is { } t)
        {
            info.SidecarPath = t.JsonPath;
            info.Description = t.Description;
            if (info.Lat is null && t.Lat is not null) { info.Lat = t.Lat; info.Lon = t.Lon; }
            if (!strong && t.Taken is not null) { taken = t.Taken; source = DateSources.TakeoutJson; }
        }

        info.Taken = taken;
        info.TakenSource = source;
        return info;
    }

    private static bool LooksLikeIsoBmff(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> b = stackalloc byte[12];
            if (fs.Read(b) < 12) return false;
            return b[4] == 'f' && b[5] == 't' && b[6] == 'y' && b[7] == 'p' && b[8] != 'h' && b[8] != 'm'; // not heic/mif1
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void ReadFromExtractor(IReadOnlyList<MeDirectory> dirs, Raw raw, MediaInfo info)
    {
        // Some files (Pixel DNGs) yield several Exif directories, the first of them empty: look through all.
        var ifd0s = dirs.OfType<ExifIfd0Directory>().ToList();
        var subs = dirs.OfType<ExifSubIfdDirectory>().ToList();
        raw.HadExif = ifd0s.Count > 0 || subs.Count > 0;
        raw.Make = ifd0s.Select(d => Clean(d.GetString(ExifDirectoryBase.TagMake))).FirstOrDefault(s => s is not null);
        raw.Model = ifd0s.Select(d => Clean(d.GetString(ExifDirectoryBase.TagModel))).FirstOrDefault(s => s is not null);
        foreach (var d in ifd0s)
            if (d.TryGetInt32(ExifDirectoryBase.TagOrientation, out var o) && o is >= 1 and <= 8) { raw.Orientation = o; break; }
        raw.Original = GetDate(subs, ExifDirectoryBase.TagDateTimeOriginal) ?? GetDate(ifd0s, ExifDirectoryBase.TagDateTimeOriginal);
        raw.Digitized = GetDate(subs, ExifDirectoryBase.TagDateTimeDigitized) ?? GetDate(ifd0s, ExifDirectoryBase.TagDateTimeDigitized);
        raw.Modified = GetDate(ifd0s, ExifDirectoryBase.TagDateTime) ?? GetDate(subs, ExifDirectoryBase.TagDateTime);

        var mvhd = dirs.OfType<QuickTimeMovieHeaderDirectory>().FirstOrDefault();
        if (mvhd is not null)
        {
            raw.HadMovie = true;
            if (mvhd.TryGetDateTime(QuickTimeMovieHeaderDirectory.TagCreated, out var created) && created.Year >= 1990)
            {
                // mvhd stores UTC; the library files by local wall-clock time like everything else.
                if (created.Kind == DateTimeKind.Unspecified) created = DateTime.SpecifyKind(created, DateTimeKind.Utc);
                raw.Mvhd = DateRules.Valid(created.ToLocalTime());
            }
            // MetadataExtractor already divides by the time scale and hands back a TimeSpan; older versions gave raw ticks.
            var dur = mvhd.GetObject(QuickTimeMovieHeaderDirectory.TagDuration);
            if (dur is TimeSpan span && span > TimeSpan.Zero) raw.DurationMs = (long)span.TotalMilliseconds;
            else if (mvhd.TryGetInt64(QuickTimeMovieHeaderDirectory.TagDuration, out var ticks)
                && mvhd.TryGetInt64(QuickTimeMovieHeaderDirectory.TagTimeScale, out var scale) && scale > 0)
                raw.DurationMs = ticks * 1000 / scale;
        }
        // com.apple.quicktime.creationdate carries the local wall-clock time, e.g. "2019-07-23T22:13:49+0100".
        foreach (var d in dirs.OfType<QuickTimeMetadataHeaderDirectory>())
        foreach (var tag in d.Tags)
            if (tag.Name.Contains("Creation", StringComparison.OrdinalIgnoreCase) && DateRules.ParseLoose(tag.Description) is { } dt)
            {
                raw.Apple ??= dt;
                raw.HadMovie = true;
            }

        ReadDimensions(dirs, info);
        var gps = dirs.OfType<GpsDirectory>().FirstOrDefault()?.GetGeoLocation();
        if (gps is { } g && !g.IsZero)
        {
            info.Lat = g.Latitude;
            info.Lon = g.Longitude;
        }
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().TrimEnd('\0').Trim();

    private static DateTime? GetDate(IEnumerable<MeDirectory> ds, int tag)
    {
        // Some writers store "0000:00:00 00:00:00" or junk; TryGetDateTime rejects those.
        foreach (var d in ds)
            if (d.TryGetDateTime(tag, out var dt) && DateRules.Valid(dt) is { } ok) return ok;
        return null;
    }

    private static void ReadDimensions(IReadOnlyList<MeDirectory> dirs, MediaInfo info)
    {
        int? w = null, h = null;
        if (dirs.OfType<JpegDirectory>().FirstOrDefault() is { } jpeg
            && jpeg.TryGetInt32(JpegDirectory.TagImageWidth, out var jw) && jpeg.TryGetInt32(JpegDirectory.TagImageHeight, out var jh))
        { w = jw; h = jh; }
        else if (dirs.OfType<PngDirectory>().FirstOrDefault() is { } png
            && png.TryGetInt32(PngDirectory.TagImageWidth, out var pw) && png.TryGetInt32(PngDirectory.TagImageHeight, out var ph))
        { w = pw; h = ph; }
        else if (dirs.OfType<ExifSubIfdDirectory>().FirstOrDefault(d => d.ContainsTag(ExifDirectoryBase.TagExifImageWidth)) is { } sub
            && sub.TryGetInt32(ExifDirectoryBase.TagExifImageWidth, out var ew) && sub.TryGetInt32(ExifDirectoryBase.TagExifImageHeight, out var eh))
        { w = ew; h = eh; }
        else if (dirs.OfType<ExifIfd0Directory>().FirstOrDefault(d => d.ContainsTag(ExifDirectoryBase.TagImageWidth)) is { } ifd0
            && ifd0.TryGetInt32(ExifDirectoryBase.TagImageWidth, out var iw) && ifd0.TryGetInt32(ExifDirectoryBase.TagImageHeight, out var ih))
        { w = iw; h = ih; }
        else if (dirs.OfType<QuickTimeTrackHeaderDirectory>().FirstOrDefault(t =>
                     t.TryGetInt32(QuickTimeTrackHeaderDirectory.TagWidth, out var tw) && tw > 0) is { } trak
                 && trak.TryGetInt32(QuickTimeTrackHeaderDirectory.TagWidth, out var vw)
                 && trak.TryGetInt32(QuickTimeTrackHeaderDirectory.TagHeight, out var vh))
        { w = vw; h = vh; }
        if (w > 0 && h > 0) { info.Width = w; info.Height = h; }
    }
}
