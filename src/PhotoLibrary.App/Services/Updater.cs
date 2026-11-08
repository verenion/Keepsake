using Velopack;
using Velopack.Sources;

namespace PhotoLibrary.App.Services;

/// <summary>
/// The only code in Keepsake that touches the network, and only when the user presses
/// "Check for updates". It talks to GitHub Releases and nothing else.
/// </summary>
public sealed class Updater
{
    public const string RepoUrl = "https://github.com/verenion/PhotoLibrary";

    private readonly UpdateManager _mgr = new(new GithubSource(RepoUrl, null, prerelease: false));
    private UpdateInfo? _pending;

    /// <summary>False when running from a dev build or a plain folder rather than the installer or portable zip.</summary>
    public bool CanUpdate => _mgr.IsInstalled;

    public string CurrentVersion =>
        _mgr.CurrentVersion?.ToString()
        ?? typeof(Updater).Assembly.GetName().Version?.ToString(3)
        ?? "dev";

    /// <summary>The newer version, or null when up to date.</summary>
    public async Task<string?> CheckAsync()
    {
        _pending = await _mgr.CheckForUpdatesAsync();
        return _pending?.TargetFullRelease.Version.ToString();
    }

    public Task DownloadAsync(Action<int> progress, CancellationToken ct) =>
        _pending is null ? Task.CompletedTask : _mgr.DownloadUpdatesAsync(_pending, progress, ct);

    /// <summary>Exits, swaps in the new version and starts it again.</summary>
    public void ApplyAndRestart()
    {
        if (_pending is not null) _mgr.ApplyUpdatesAndRestart(_pending.TargetFullRelease);
    }
}
