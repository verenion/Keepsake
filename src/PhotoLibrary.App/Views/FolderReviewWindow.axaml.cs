using Avalonia.Controls;
using Avalonia.Interactivity;
using PhotoLibrary.App.ViewModels;

namespace PhotoLibrary.App.Views;

/// <summary>Choose, per top-level folder, whether it holds dated folders or is a folder album.</summary>
public partial class FolderReviewWindow : Window
{
    public FolderReviewWindow()
    {
        InitializeComponent();
    }

    private FolderReviewViewModel Vm => (FolderReviewViewModel)DataContext!;

    /// <summary>Shows the review; true when the user saved.</summary>
    public static async Task<bool> ShowAsync(Window owner, MainViewModel main, bool onlyUndecided)
    {
        var win = new FolderReviewWindow { DataContext = new FolderReviewViewModel(main, onlyUndecided) };
        var saved = await win.ShowDialog<bool?>(owner) == true;
        if (saved) await win.Vm.SaveAsync();
        return saved;
    }

    private void AllDatesClick(object? sender, RoutedEventArgs e) => Vm.SetAll(0);
    private void AllAlbumsClick(object? sender, RoutedEventArgs e) => Vm.SetAll(1);
    private void SaveClick(object? sender, RoutedEventArgs e) => Close(true);
    private void CancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
