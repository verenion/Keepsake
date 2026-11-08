using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PhotoLibrary.App.ViewModels;

namespace PhotoLibrary.App.Views;

public partial class ImportWindow : Window
{
    public ImportWindow()
    {
        InitializeComponent();
    }

    private ImportViewModel Vm => (ImportViewModel)DataContext!;

    private async void BrowseClick(object? sender, RoutedEventArgs e)
    {
        var options = new FolderPickerOpenOptions { Title = "Folder to check against the library", AllowMultiple = false };
        var picked = await StorageProvider.OpenFolderPickerAsync(options);
        if (picked.Count > 0 && picked[0].TryGetLocalPath() is { } p) Vm.FolderPath = p;
    }

    private void CancelClick(object? sender, RoutedEventArgs e) => Vm.Cancel();

    private void CloseClick(object? sender, RoutedEventArgs e) => Close();
}
