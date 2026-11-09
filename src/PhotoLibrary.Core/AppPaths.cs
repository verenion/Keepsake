namespace PhotoLibrary.Core;

/// <summary>Where the app keeps its own data, outside the photo library so the library stays clean.</summary>
public static class AppPaths
{
    public static string DataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoLibrary");

    public static string DbPath => Path.Combine(DataDir, "library.sqlite");
    public static string ThumbDir => Path.Combine(DataDir, "thumbs");
    public static string ReportsDir => Path.Combine(DataDir, "reports");
    public static string AiModelDir => Path.Combine(DataDir, "ai_models");

    public static string DefaultLibraryRoot()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var oneDrive = Path.Combine(home, "OneDrive", "Pictures");
        if (Directory.Exists(oneDrive)) return oneDrive;
        var pics = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        return string.IsNullOrEmpty(pics) ? Path.Combine(home, "Pictures") : pics;
    }

    public static void EnsureDirs()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(ThumbDir);
        Directory.CreateDirectory(ReportsDir);
        Directory.CreateDirectory(AiModelDir);
    }
}

/// <summary>Reserved top-level folders inside the library.</summary>
public static class LibraryFolders
{
    /// <summary>
    /// Where Organise used to move exact duplicates. No longer used, but kept out of the scan and reserved so an
    /// old one isn't mistaken for an album, and so undoing an old Organise run can still bring files back.
    /// </summary>
    public const string Duplicates = "_Duplicates";
    public const string Undated = "_Undated";
    public const string Logs = "_Logs";
    /// <summary>
    /// Where folder albums used to live. Albums already here keep working; new folder albums are top-level
    /// folders of the library.
    /// </summary>
    public const string LegacyAlbums = "_Albums";
    /// <summary>Encrypted vaults (secure albums), one sub-folder each. Never scanned, indexed or organised.</summary>
    public const string Vault = "_Vault";

    public static readonly HashSet<string> ExcludedFromIndex = new(StringComparer.OrdinalIgnoreCase) { Duplicates, Logs, Vault };

    /// <summary>Keepsake's own top-level folders.</summary>
    public static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase) { Duplicates, Undated, Logs, Vault, LegacyAlbums };

    /// <summary>The top-level folder <paramref name="path"/> is in, or null for a file directly in the library root.</summary>
    public static string? TopOf(string root, string path)
    {
        var rel = Path.GetRelativePath(root, path);
        if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel)) return null;
        var sep = rel.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        return sep <= 0 ? null : rel[..sep];
    }
}
