using System.IO.Compression;

namespace PhotoLibrary.Core.Video;

public sealed record ToolDownloadProgress(long BytesDone, long BytesTotal, string Stage);

/// <summary>
/// Finds, or fetches, the ffmpeg executable that video thumbnails and previews need. Looked for on PATH, in
/// the app's own tools folder and in winget's links folder. The download is the BtbN static GPL build from
/// GitHub (a single self-contained ffmpeg.exe); only that one file is kept from the zip.
/// </summary>
public static class Ffmpeg
{
    public const string DownloadUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/latest/download/ffmpeg-master-latest-win64-gpl.zip";
    public const long ApproxDownloadBytes = 202_000_000;

    private static readonly object Gate = new();
    private static string? _path;
    private static bool _searched;

    public static string ToolsDir => Path.Combine(AppPaths.DataDir, "tools");
    public static string BundledPath => Path.Combine(ToolsDir, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");

    /// <summary>Full path of ffmpeg, or null when none is installed.</summary>
    public static string? ExePath
    {
        get
        {
            lock (Gate)
            {
                if (!_searched) { _path = Locate(); _searched = true; }
                return _path;
            }
        }
    }

    public static bool Available => ExePath is not null;

    /// <summary>Where it was found, for the Settings tab.</summary>
    public static string Describe() => ExePath is { } p
        ? (p == BundledPath ? "Downloaded copy in the app's data folder." : $"Found at {p}.")
        : "Not installed. Video thumbnails and previews are blank until it is.";

    /// <summary>Look again, e.g. after a download or after the user installed it.</summary>
    public static void Refresh()
    {
        lock (Gate) _searched = false;
    }

    private static string? Locate()
    {
        var exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        var candidates = new List<string> { BundledPath };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            if (dir.Length > 0) candidates.Add(Path.Combine(dir.Trim('"'), exe));
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        candidates.Add(Path.Combine(local, "Microsoft", "WinGet", "Links", exe));
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>Downloads the static build and keeps just ffmpeg.exe. Safe to re-run; a finished copy is reused.</summary>
    public static async Task DownloadAsync(IProgress<ToolDownloadProgress>? progress, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Automatic ffmpeg download is only set up for Windows; install ffmpeg with your package manager.");
        Directory.CreateDirectory(ToolsDir);
        var zip = Path.Combine(ToolsDir, "ffmpeg.zip");
        var part = zip + ".part";
        using (var http = new HttpClient { Timeout = TimeSpan.FromHours(2) })
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("PhotoLibrary/1.0");
            using var resp = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? ApproxDownloadBytes;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            var buffer = new byte[1 << 16];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                done += n;
                progress?.Report(new ToolDownloadProgress(done, total, "Downloading"));
            }
        }
        File.Move(part, zip, overwrite: true);

        progress?.Report(new ToolDownloadProgress(1, 1, "Unpacking"));
        try
        {
            using var archive = ZipFile.OpenRead(zip);
            var entry = archive.Entries.FirstOrDefault(e =>
                e.Name.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase) && e.FullName.Replace('\\', '/').Contains("/bin/"))
                ?? throw new InvalidDataException("The download did not contain bin/ffmpeg.exe.");
            var tmp = BundledPath + ".part";
            entry.ExtractToFile(tmp, overwrite: true);
            File.Move(tmp, BundledPath, overwrite: true);
        }
        finally
        {
            try { File.Delete(zip); } catch (IOException) { }
        }
        Refresh();
        if (!Available) throw new InvalidOperationException("ffmpeg was unpacked but could not be found afterwards.");
        progress?.Report(new ToolDownloadProgress(1, 1, "Done"));
    }
}
