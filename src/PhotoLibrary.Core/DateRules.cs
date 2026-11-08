using System.Globalization;
using System.Text.RegularExpressions;

namespace PhotoLibrary.Core;

/// <summary>
/// Where a capture date came from, in priority order:
/// exif > full date+time in file name > exif digitized/modified > Takeout json > date-only file name.
/// </summary>
public static class DateSources
{
    public const string Exif = "exif";
    public const string ExifDigitized = "exif-digitized";
    public const string ExifModified = "exif-modified";
    public const string VideoApple = "video-apple";
    public const string VideoMvhd = "video-mvhd";
    public const string FileName = "filename";
    public const string TakeoutJson = "takeout-json";
    public const string FileModified = "file-modified-time";

    /// <summary>Sources that are the camera's own word for when the shot was taken.</summary>
    public static bool IsCaptureSource(string? s) => s is Exif or VideoApple or VideoMvhd;

    public static string Describe(string? s) => s switch
    {
        Exif => "camera EXIF",
        ExifDigitized => "EXIF digitized date",
        ExifModified => "EXIF modified date",
        VideoApple => "video (Apple creation date)",
        VideoMvhd => "video header",
        FileName => "file name",
        TakeoutJson => "Google Takeout",
        FileModified => "file modified time",
        null => "no date",
        _ => s,
    };
}

public static partial class DateRules
{
    [GeneratedRegex(@"(?<!\d)((?:19|20)\d\d)[-_.]?(0[1-9]|1[0-2])[-_.]?(0[1-9]|[12]\d|3[01])(?:[ _T.-]{0,2}([01]\d|2[0-3])[.:_-]?([0-5]\d)[.:_-]?([0-5]\d))?")]
    private static partial Regex NameRegex();

    [GeneratedRegex(@"(\d{4})[:\-](\d\d)[:\-](\d\d)[ T](\d\d):(\d\d):(\d\d)")]
    private static partial Regex LooseRegex();

    /// <summary>Rejects obviously wrong years (unset clocks, 1904 epoch, far future).</summary>
    public static DateTime? Valid(DateTime? d) =>
        d is { } x && x.Year >= 1990 && x.Year <= DateTime.Today.Year + 1 ? x : null;

    /// <summary>Date embedded in a file name such as 20190106_125200000_iOS.jpg or 2014-04-24 15.54.51.jpg.</summary>
    public static DateTime? FromFileName(string name, bool needTime = false)
    {
        foreach (Match m in NameRegex().Matches(name))
        {
            var hasTime = m.Groups[4].Success;
            if (needTime && !hasTime) continue;
            try
            {
                var d = new DateTime(
                    int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                    int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                    int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture),
                    hasTime ? int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture) : 0,
                    hasTime ? int.Parse(m.Groups[5].Value, CultureInfo.InvariantCulture) : 0,
                    hasTime ? int.Parse(m.Groups[6].Value, CultureInfo.InvariantCulture) : 0);
                if (Valid(d) is { } ok) return ok;
            }
            catch (ArgumentOutOfRangeException)
            {
                // e.g. 2013-02-30: not a date, keep looking
            }
        }
        return null;
    }

    /// <summary>"2019:07:23 22:13:49" or "2019-07-23T22:13:49+0100" → the wall-clock time, time zone ignored.</summary>
    public static DateTime? ParseLoose(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        var m = LooseRegex().Match(s);
        if (!m.Success) return null;
        try
        {
            return Valid(new DateTime(
                int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value),
                int.Parse(m.Groups[4].Value), int.Parse(m.Groups[5].Value), int.Parse(m.Groups[6].Value)));
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>Library folder for a capture date: YYYY/YYYY_MM/YYYY_MM_DD.</summary>
    public static string DatedFolder(DateTime d) =>
        Path.Combine($"{d.Year:D4}", $"{d.Year:D4}_{d.Month:D2}", $"{d.Year:D4}_{d.Month:D2}_{d.Day:D2}");
}
