using PhotoLibrary.Core.Metadata;

namespace PhotoLibrary.Core.Db;

/// <summary>One indexed file: identity (path, size, mtime, hash) plus the metadata read from it.</summary>
public sealed class MediaRecord
{
    public long Id { get; set; }
    public required string Path { get; set; }
    public bool InLibrary { get; set; }
    /// <summary>Removed from the library by the user: never shown, never re-imported, file left on disk.</summary>
    public bool Hidden { get; set; }
    public long Size { get; set; }
    /// <summary>LastWriteTimeUtc ticks.</summary>
    public long MTime { get; set; }
    public required string Sha256 { get; set; }
    public MediaKind Kind { get; set; }
    public DateTime? Taken { get; set; }
    public string? TakenSource { get; set; }
    public string? Make { get; set; }
    public string? Model { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public int? Orientation { get; set; }
    public double? Lat { get; set; }
    public double? Lon { get; set; }
    public long? DurationMs { get; set; }
    public ulong? DHash { get; set; }
    public bool DHashDone { get; set; }
    public string? Description { get; set; }
    public string? SidecarPath { get; set; }
    public int MetaVersion { get; set; }
    public DateTime Added { get; set; }
    /// <summary>For a companion file (a RAW, a Live Photo clip…): the photo it belongs to. Companions aren't shown on their own.</summary>
    public long? PairOf { get; set; }
    /// <summary>For a photo with companions: what kind they are.</summary>
    public Pairs.PairKind PairKind { get; set; }
    public bool IsCompanion => PairOf is not null;

    public string FileName => System.IO.Path.GetFileName(Path);
    public string Extension => System.IO.Path.GetExtension(Path);
    public bool IsImage => Kind == MediaKind.Image;

    public void Apply(MediaInfo m)
    {
        Kind = m.Kind;
        Taken = m.Taken;
        TakenSource = m.TakenSource;
        Make = m.Make;
        Model = m.Model;
        Width = m.Width;
        Height = m.Height;
        Orientation = m.Orientation;
        Lat = m.Lat;
        Lon = m.Lon;
        DurationMs = m.DurationMs;
        Description = m.Description;
        SidecarPath = m.SidecarPath;
        MetaVersion = MetadataReader.Version;
    }
}
