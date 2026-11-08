namespace PhotoLibrary.Core.Metadata;

/// <summary>Everything we learn about a file from its own contents and any sidecar, before hashing.</summary>
public sealed class MediaInfo
{
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
    public string? Description { get; set; }
    public string? SidecarPath { get; set; }
}
