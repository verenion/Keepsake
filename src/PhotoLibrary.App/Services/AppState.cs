using PhotoLibrary.AI;
using PhotoLibrary.Core;
using PhotoLibrary.Core.Albums;
using PhotoLibrary.Core.Db;
using PhotoLibrary.Core.Import;
using PhotoLibrary.Core.Scanning;
using PhotoLibrary.Core.Thumbnails;

namespace PhotoLibrary.App.Services;

/// <summary>Long-lived services shared by every window: the index, scanner, thumbnail cache, importer.</summary>
public sealed class AppState : IDisposable
{
    public LibraryDb Db { get; }
    public Scanner Scanner { get; }
    public ThumbnailService Thumbs { get; }
    public Importer Importer { get; }
    public AiEngine Ai { get; }
    public AppSettings Settings { get; }
    public string LibraryRoot { get; private set; }
    /// <summary>Album membership and the file moves physical albums need; rebuilt when the library root changes.</summary>
    public AlbumService Albums { get; private set; }

    /// <summary>Records from the last library scan, so dialogs don't re-query every row each time.</summary>
    public List<MediaRecord> Library { get; set; } = [];

    public AppState()
    {
        AppPaths.EnsureDirs();
        Db = new LibraryDb(AppPaths.DbPath);
        Scanner = new Scanner(Db);
        Thumbs = new ThumbnailService(AppPaths.ThumbDir);
        Importer = new Importer(Db);
        Ai = new AiEngine(Db, Thumbs);
        Ai.EnsureSchema();
        Ai.RebuildRawDerivedData();
        if (Db.GetSetting("heic_wic_v1") != "1")
        {
            // HEIC thumbnails used to fail outright; now that Windows' decoder is used, give them another go.
            Thumbs.ForgetFailures(Db.Query("1=1").Where(r => r.Path.EndsWith(".heic", StringComparison.OrdinalIgnoreCase) || r.Path.EndsWith(".heif", StringComparison.OrdinalIgnoreCase)));
            Db.SetSetting("heic_wic_v1", "1");
        }
        if (Db.GetSetting("video_duration_v1") != "1")
        {
            // Video lengths were never stored (the metadata library returns them as a TimeSpan). The next scan
            // re-reads metadata for videos only; nothing is re-hashed.
            Db.ResetMetadata(Db.Query("kind='video'").Select(r => r.Id));
            Db.SetSetting("video_duration_v1", "1");
        }
        if (Db.GetSetting("tiff_wic_v1") != "1")
        {
            // TIFF thumbnails used to fail outright (Skia has no TIFF codec); Windows' decoder handles them now.
            Thumbs.ForgetFailures(Db.Query("1=1").Where(r => r.Path.EndsWith(".tif", StringComparison.OrdinalIgnoreCase) || r.Path.EndsWith(".tiff", StringComparison.OrdinalIgnoreCase)));
            Db.SetSetting("tiff_wic_v1", "1");
        }
        if (Db.GetSetting("psd_v1") != "1")
        {
            // PSD thumbnails used to fail outright; give them another go, and re-read their size from the header.
            var psds = Db.Query("1=1").Where(r => r.Path.EndsWith(".psd", StringComparison.OrdinalIgnoreCase) || r.Path.EndsWith(".psb", StringComparison.OrdinalIgnoreCase)).ToList();
            Thumbs.ForgetFailures(psds);
            Db.ResetMetadata(psds.Select(r => r.Id));
            Db.SetSetting("psd_v1", "1");
        }
        Settings = new AppSettings(Db, Ai);
        Thumbs.Size = Settings.ThumbnailSize;
        Thumbs.Quality = Settings.ThumbnailQuality;
        if (Db.GetSetting("thumbs_v2") != "1")
        {
            // Thumbnails are now sized by their short edge (and tiles no longer shrink them on load); rebuild
            // the old long-edge cache lazily. Deleting runs in the background so startup isn't held up.
            var thumbs = Thumbs;
            Task.Run(() => thumbs.Clear());
            Db.SetSetting("thumbs_v2", "1");
        }
        LibraryRoot = Db.GetSetting("library_root") ?? AppPaths.DefaultLibraryRoot();
        Albums = new AlbumService(Db, LibraryRoot) { Kdf = Settings.VaultKdf.Params };
    }

    public void SetLibraryRoot(string root)
    {
        root = Path.GetFullPath(root);
        if (!Scanner.PathComparer.Equals(root, LibraryRoot)) Db.SetInLibraryUnder(LibraryRoot, false);
        LibraryRoot = root;
        Albums = new AlbumService(Db, root) { Kdf = Settings.VaultKdf.Params };
        Db.SetSetting("library_root", root);
    }

    public void Dispose()
    {
        Ai.Dispose();
        Db.Dispose();
    }
}
