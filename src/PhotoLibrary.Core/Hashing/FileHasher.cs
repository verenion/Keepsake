using System.Security.Cryptography;

namespace PhotoLibrary.Core.Hashing;

public static class FileHasher
{
    public static string Sha256Hex(string path, CancellationToken ct = default)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20,
            FileOptions.SequentialScan);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buf = new byte[1 << 20];
        int n;
        while ((n = fs.Read(buf, 0, buf.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            sha.AppendData(buf, 0, n);
        }
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }
}
