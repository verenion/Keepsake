using System.Security.Cryptography;
using Avalonia.Threading;
using PhotoLibrary.Core.Albums;
using PhotoLibrary.Core.Vault;

namespace PhotoLibrary.App.Services;

/// <summary>
/// Keys of unlocked vaults, held in memory only and zeroed on lock. A vault locks itself after
/// <see cref="IdleTimeout"/> without use, and every vault locks when the app closes.
/// </summary>
public sealed class VaultSession : IDisposable
{
    private readonly Dictionary<long, (byte[] Key, DateTime LastUsed)> _keys = [];
    private readonly DispatcherTimer _timer;

    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(10);

    /// <summary>Raised on the UI thread with the album id after a vault locks (by hand or after idling).</summary>
    public event Action<long>? Locked;

    public VaultSession()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _timer.Tick += (_, _) => LockIdle();
        _timer.Start();
    }

    public bool IsUnlocked(long albumId) => _keys.ContainsKey(albumId);

    /// <summary>Checks the password (Argon2id, about half a second, off the UI thread). Throws on a wrong password.</summary>
    public async Task UnlockAsync(Album album, string folder, string password)
    {
        var key = await Task.Run(() => VaultStore.Unlock(folder, password));
        if (_keys.Remove(album.Id, out var old)) CryptographicOperations.ZeroMemory(old.Key);
        _keys[album.Id] = (key, DateTime.UtcNow);
    }

    /// <summary>The vault key, counting as use (resets the idle timer). Null when locked.</summary>
    public byte[]? Key(long albumId)
    {
        if (!_keys.TryGetValue(albumId, out var entry)) return null;
        _keys[albumId] = (entry.Key, DateTime.UtcNow);
        return entry.Key;
    }

    public void Touch(long albumId) => Key(albumId);

    public void Lock(long albumId)
    {
        if (!_keys.Remove(albumId, out var entry)) return;
        CryptographicOperations.ZeroMemory(entry.Key);
        Locked?.Invoke(albumId);
    }

    public void LockAll()
    {
        foreach (var id in _keys.Keys.ToList()) Lock(id);
    }

    private void LockIdle()
    {
        var now = DateTime.UtcNow;
        foreach (var (id, entry) in _keys.ToList())
            if (now - entry.LastUsed > IdleTimeout) Lock(id);
    }

    public void Dispose()
    {
        _timer.Stop();
        LockAll();
    }
}
