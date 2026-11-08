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
    public const string Duplicates = "_Duplicates";
    public const string Undated = "_Undated";
    public const string Logs = "_Logs";
    /// <summary>Physical albums live here, one sub-folder each; Organise leaves them where they are.</summary>
    public const string Albums = "_Albums";
    /// <summary>Encrypted vaults (secure albums), one sub-folder each. Never scanned, indexed or organised.</summary>
    public const string Vault = "_Vault";

    public static readonly HashSet<string> ExcludedFromIndex = new(StringComparer.OrdinalIgnoreCase) { Duplicates, Logs, Vault };
}
