using System.Collections.ObjectModel;

namespace PhotoLibrary.App.ViewModels;

public sealed class DayGroupViewModel(string title, IEnumerable<PhotoViewModel> photos) : ViewModelBase
{
    public string Title { get; } = title;
    public ObservableCollection<PhotoViewModel> Photos { get; } = new(photos);
    public string CountText => Photos.Count == 1 ? "1 item" : $"{Photos.Count} items";
}
