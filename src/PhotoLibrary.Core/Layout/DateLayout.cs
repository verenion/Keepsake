using System.Globalization;

namespace PhotoLibrary.Core.Layout;

/// <summary>
/// One way of laying out dated folders, e.g. <c>2024\2024_05\2024_05_31</c> or <c>2024-05-31</c>. Each
/// <c>/</c>-separated part of <see cref="Format"/> is a .NET custom date format for one folder level.
/// </summary>
public sealed record DateLayout(string Id, string Format)
{
    public static readonly DateLayout[] All =
    [
        new("y/y_m/y_m_d", "yyyy/yyyy_MM/yyyy_MM_dd"),
        new("y/m/d", "yyyy/MM/dd"),
        new("y/y-m-d", "yyyy/yyyy-MM-dd"),
        new("y/y_m_d", "yyyy/yyyy_MM_dd"),
        new("y/y-m", "yyyy/yyyy-MM"),
        new("y/m", "yyyy/MM"),
        new("y-m-d", "yyyy-MM-dd"),
        new("y_m_d", "yyyy_MM_dd"),
        new("y-m", "yyyy-MM"),
        new("y", "yyyy"),
    ];

    /// <summary>Keepsake's original layout, and the default.</summary>
    public static DateLayout Default => All[0];

    public static DateLayout Find(string? id) => All.FirstOrDefault(l => l.Id == id) ?? Default;

    private static readonly DateTime Sample = new(2024, 5, 31);

    public string[] Levels => Format.Split('/');

    /// <summary>How 31 May 2024 is filed, e.g. <c>2024\2024_05\2024_05_31</c>.</summary>
    public string Example => RelativeDir(Sample);

    public string Grain => Format.Contains("dd") ? "a folder per day" : Format.Contains("MM") ? "a folder per month" : "a folder per year";

    public string Label => $"{Example}   ({Grain})";

    /// <summary>The folder, relative to the library, a photo taken on <paramref name="d"/> belongs in.</summary>
    public string RelativeDir(DateTime d) => Path.Combine(Levels.Select(l => d.ToString(l, CultureInfo.InvariantCulture)).ToArray());

    /// <summary>The top-level folder name a photo taken on <paramref name="d"/> goes under.</summary>
    public string TopFor(DateTime d) => d.ToString(Levels[0], CultureInfo.InvariantCulture);

    /// <summary>True when <paramref name="relDir"/> (relative to the library) is exactly a folder this layout makes.</summary>
    public bool Matches(string relDir) => TryParse(relDir, out _);

    public bool TryParse(string relDir, out DateTime date)
    {
        date = default;
        var parts = relDir.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        var levels = Levels;
        if (parts.Length != levels.Length) return false;
        int? year = null;
        int month = 1, day = 1;
        for (var i = 0; i < parts.Length; i++)
        {
            if (!DateTime.TryParseExact(parts[i], levels[i], CultureInfo.InvariantCulture, DateTimeStyles.NoCurrentDateDefault, out var p)) return false;
            if (levels[i].Contains("yyyy")) year = p.Year;
            if (levels[i].Contains("MM")) month = p.Month;
            if (levels[i].Contains("dd")) day = p.Day;
        }
        if (year is not { } y || !PlausibleYear(y) || day > DateTime.DaysInMonth(y, month)) return false;
        date = new DateTime(y, month, day);
        // Rejects mixed-up levels such as 2024\2023_05\..., and odd spellings the format happens to accept.
        return string.Equals(RelativeDir(date), string.Join(Path.DirectorySeparatorChar, parts), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when a top-level folder name could be this layout's first level, e.g. "2012" or "2012-05-31".</summary>
    public bool MatchesTop(string name) =>
        DateTime.TryParseExact(name, Levels[0], CultureInfo.InvariantCulture, DateTimeStyles.NoCurrentDateDefault, out var d)
        && PlausibleYear(d.Year)
        && string.Equals(d.ToString(Levels[0], CultureInfo.InvariantCulture), name, StringComparison.OrdinalIgnoreCase);

    /// <summary>Photos are from 1826 onwards; a folder named "4023" is not a year.</summary>
    public static bool PlausibleYear(int y) => y >= 1826 && y <= DateTime.Now.Year + 1;

    /// <summary>True when the name could be the top of any known layout.</summary>
    public static bool LooksLikeDate(string name) => All.Any(l => l.MatchesTop(name));

    /// <summary>
    /// The layout that best explains the files in a top-level folder: the one most of their folders match.
    /// <paramref name="relDirs"/> are the files' folders relative to the library (including the top folder).
    /// </summary>
    public static (DateLayout? Layout, int Matching) Detect(string topName, IReadOnlyCollection<string> relDirs)
    {
        DateLayout? best = null;
        var bestCount = -1;
        foreach (var layout in All.Where(l => l.MatchesTop(topName)))
        {
            var n = relDirs.Count(layout.Matches);
            if (n > bestCount) { best = layout; bestCount = n; }
        }
        return (best, Math.Max(bestCount, 0));
    }
}
