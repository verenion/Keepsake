using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PhotoLibrary.App.ViewModels;

public enum NodeKind { All, Year, Month, Day, Undated }

public partial class TreeNodeViewModel(NodeKind kind, string title, int count, DateTime from, DateTime to) : ViewModelBase
{
    public NodeKind Kind { get; } = kind;
    public string Title { get; } = title;
    public int Count { get; } = count;
    /// <summary>Inclusive start of the date range this node covers.</summary>
    public DateTime From { get; } = from;
    /// <summary>Exclusive end.</summary>
    public DateTime To { get; } = to;
    public ObservableCollection<TreeNodeViewModel> Children { get; } = [];
    public string Label => $"{Title}  ({Count:N0})";

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }
}
