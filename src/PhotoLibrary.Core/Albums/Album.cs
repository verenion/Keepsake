namespace PhotoLibrary.Core.Albums;

/// <summary>
/// Virtual albums are lists kept in the database: a photo can be in any number of them and nothing on disk
/// changes. A physical album is a top-level folder of the library: adding a photo moves the file into that
/// folder, so a photo can be in at most one of them. Every folder made outside Keepsake is a physical album. Secure albums are vaults: each photo is encrypted into <c>_Vault\&lt;id&gt;</c> under the album's own password,
/// the original is deleted, and every trace of it leaves the library's indexes.
/// </summary>
public enum AlbumKind { Virtual, Physical, Secure }

public sealed class Album
{
    public long Id { get; set; }
    public required string Name { get; set; }
    /// <summary>"#RRGGBB".</summary>
    public string Colour { get; set; } = Palette[0];
    public AlbumKind Kind { get; set; }
    /// <summary>The photo shown as the album's cover; the first item when null.</summary>
    public long? CoverFileId { get; set; }
    public DateTime Created { get; set; }
    /// <summary>
    /// Physical albums only: when true, Organise files this folder's photos into the dated folders like loose
    /// files. Off by default, so a folder another app writes to (a phone's Camera Roll) is never disturbed.
    /// </summary>
    public bool Organise { get; set; }
    /// <summary>Members still in the library, filled in by the query.</summary>
    public int Count { get; set; }

    public bool IsPhysical => Kind == AlbumKind.Physical;
    public bool IsSecure => Kind == AlbumKind.Secure;
    /// <summary>For a secure album: the vault folder's random id under _Vault.</summary>
    public string? VaultId { get; set; }

    public static string KindToDb(AlbumKind k) => k switch { AlbumKind.Physical => "physical", AlbumKind.Secure => "secure", _ => "virtual" };
    public static AlbumKind KindFromDb(string s) => s switch { "physical" => AlbumKind.Physical, "secure" => AlbumKind.Secure, _ => AlbumKind.Virtual };

    /// <summary>Swatches offered in the album settings; any "#RRGGBB" is accepted too.</summary>
    public static readonly string[] Palette =
    [
        "#3D8BFF", "#2EC27E", "#F5A623", "#E5484D", "#9B59B6", "#1ABC9C",
        "#E91E8C", "#8D6E63", "#607D8B", "#FFD600", "#00ACC1", "#FF7043",
    ];

    public static bool IsValidColour(string? s) =>
        s is { Length: 7 } && s[0] == '#' && s.Skip(1).All(Uri.IsHexDigit);

    /// <summary>
    /// The folder name a physical album uses: the name with path-unsafe characters replaced. It never starts
    /// with '$' or '.', which mark sync tools' folders.
    /// </summary>
    public static string FolderName(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Trim().Select(c => bad.Contains(c) ? '_' : c).ToArray()).TrimEnd('.', ' ').TrimStart('$', '.', ' ');
        return cleaned.Length == 0 ? "Album" : cleaned;
    }
}
