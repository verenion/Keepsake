using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PhotoLibrary.App.ViewModels;

namespace PhotoLibrary.App.Views;

public partial class VaultView : UserControl
{
    public VaultView()
    {
        InitializeComponent();
    }

    private async void ExportClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not VaultViewModel vm || vm.Selected is not { } item || TopLevel.GetTopLevel(this) is not { } top) return;
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export a decrypted copy",
            SuggestedFileName = item.FileName,
            ShowOverwritePrompt = true,
        });
        if (file?.TryGetLocalPath() is { } path) await vm.ExportSelectedAsync(path);
    }
}
