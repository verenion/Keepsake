using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PhotoLibrary.Core;

public sealed record TakeoutInfo(string JsonPath, DateTime? Taken, string? Description, double? Lat, double? Lon);

/// <summary>
/// Finds and reads Google Takeout ".supplemental-metadata.json" sidecars. Google truncates long sidecar
/// names, numbers duplicates as "name(1).jpg" + "name.jpg(1).json", and shares one sidecar between an
/// original and its "-edited" copy, so matching is fuzzier than a straight name lookup.
/// </summary>
public static partial class TakeoutSidecar
{
    private const string SidecarSuffix = ".supplemental-metadata";

    private sealed record Entry(string Key, string Num, string Path);

    private static readonly ConcurrentDictionary<string, List<Entry>> DirCache = new(StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"\((\d+)\)$")]
    private static partial Regex TrailingNumber();

    [GeneratedRegex(@"\.[a-z-]*$")]
    private static partial Regex TrailingLowerExt();

    [GeneratedRegex(@"(\(\d+\))(\.\w+)$")]
    private static partial Regex NumberedMedia();

    [GeneratedRegex(@"-(edited|bearbeitet|modifié)$", RegexOptions.IgnoreCase)]
    private static partial Regex EditedSuffix();

    public static void ClearCache() => DirCache.Clear();

    private static List<Entry> Sidecars(string folder) => DirCache.GetOrAdd(folder, f =>
    {
        var list = new List<Entry>();
        IEnumerable<string> jsons;
        try { jsons = Directory.EnumerateFiles(f, "*.json"); }
        catch (IOException) { return list; }
        catch (UnauthorizedAccessException) { return list; }
        foreach (var j in jsons)
        {
            var key = Path.GetFileName(j)[..^5];
            var num = "";
            var m = TrailingNumber().Match(key);
            if (m.Success) { num = m.Value; key = key[..m.Index]; }
            m = TrailingLowerExt().Match(key);
            if (m.Success && m.Value.Length > 0 && SidecarSuffix.StartsWith(m.Value, StringComparison.Ordinal))
                key = key[..m.Index]; // the suffix may be truncated by Google: ".supplemental-met"
            list.Add(new Entry(key.ToLowerInvariant(), num, j));
        }
        return list;
    });

    /// <summary>The sidecar for a media file, or null.</summary>
    public static string? Find(string mediaPath)
    {
        var folder = Path.GetDirectoryName(mediaPath);
        if (folder is null) return null;
        var name = Path.GetFileName(mediaPath);
        var num = "";
        var baseName = name;
        var m = NumberedMedia().Match(name);
        if (m.Success)
        {
            num = m.Groups[1].Value;
            baseName = name[..m.Index] + m.Groups[2].Value;
        }
        var stem = Path.GetFileNameWithoutExtension(baseName);
        var ext = Path.GetExtension(baseName);
        var variants = new[] { baseName, EditedSuffix().Replace(stem, "") + ext }
            .Select(v => v.ToLowerInvariant()).Distinct().ToArray();

        Entry? best = null;
        foreach (var e in Sidecars(folder))
        {
            if (e.Num != num || e.Key.Length == 0) continue;
            foreach (var v in variants)
            {
                var hit = v == e.Key
                          || (v.StartsWith(e.Key, StringComparison.Ordinal) && e.Key.Length >= 40)
                          || e.Key == Path.GetFileNameWithoutExtension(v);
                if (hit && (best is null || e.Key.Length > best.Key.Length)) best = e;
            }
        }
        return best?.Path;
    }

    public static TakeoutInfo? Read(string jsonPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(jsonPath));
            var root = doc.RootElement;
            DateTime? taken = null;
            if (root.TryGetProperty("photoTakenTime", out var ptt) && ptt.TryGetProperty("timestamp", out var tsEl)
                && long.TryParse(tsEl.GetString(), out var ts) && ts > 0)
                taken = DateRules.Valid(DateTimeOffset.FromUnixTimeSeconds(ts).ToLocalTime().DateTime);

            string? description = null;
            if (root.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String)
                description = string.IsNullOrWhiteSpace(d.GetString()) ? null : d.GetString();

            double? lat = null, lon = null;
            foreach (var geoKey in new[] { "geoDataExif", "geoData" })
            {
                if (!root.TryGetProperty(geoKey, out var geo)) continue;
                if (geo.TryGetProperty("latitude", out var la) && geo.TryGetProperty("longitude", out var lo)
                    && la.TryGetDouble(out var latV) && lo.TryGetDouble(out var lonV) && (latV != 0 || lonV != 0))
                {
                    lat = latV; lon = lonV;
                    break;
                }
            }
            return new TakeoutInfo(jsonPath, taken, description, lat, lon);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static TakeoutInfo? For(string mediaPath) => Find(mediaPath) is { } j ? Read(j) : null;
}
