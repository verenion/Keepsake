using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PhotoLibrary.App.Services;
using PhotoLibrary.Core.Db;

namespace PhotoLibrary.App.ViewModels;

/// <summary>A photo under review on the Duplicates or Quality tab: a tile, a tick box and a caption.</summary>
public partial class ReviewItemViewModel(PhotoViewModel photo, string caption, string? note = null) : ViewModelBase
{
    public PhotoViewModel Photo { get; } = photo;
    public MediaRecord Record => Photo.Record;
    /// <summary>The tick box's label; the Duplicates tab updates it as ticks change.</summary>
    [ObservableProperty] public partial string Caption { get; set; } = caption;
    /// <summary>The copy Keepsake suggested keeping (Duplicates tab).</summary>
    public bool Suggested { get; init; }
    public string Note { get; } = note ?? "";
    public bool HasNote => Note.Length > 0;

    /// <summary>Ticked: this one goes when the user removes or deletes the marked photos.</summary>
    [ObservableProperty] public partial bool Marked { get; set; }

    public event Action<ReviewItemViewModel>? MarkedChanged;
    partial void OnMarkedChanged(bool value) => MarkedChanged?.Invoke(this);

    public static string DescribeFile(MediaRecord r, string libraryRoot)
    {
        var dims = r.Width is { } w && r.Height is { } h ? $"{w}×{h}  ·  " : "";
        var pair = Core.Pairs.FilePairs.Describe(r.PairKind);
        return $"{dims}{Formatting.Bytes(r.Size)}\n{Path.GetRelativePath(libraryRoot, r.Path)}" + (pair.Length > 0 ? "\n" + pair : "");
    }
}

public sealed class ReviewGroupViewModel(string title, string subtitle, IEnumerable<ReviewItemViewModel> items, string accent) : ViewModelBase
{
    public string Title { get; } = title;
    public string Subtitle { get; } = subtitle;
    /// <summary>Colour of the stripe at the left of the group, by danger level.</summary>
    public string Accent { get; } = accent;
    public ObservableCollection<ReviewItemViewModel> Items { get; } = new(items);
}
