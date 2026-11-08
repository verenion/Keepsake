using Avalonia.Controls;
using Avalonia.Interactivity;
using PhotoLibrary.App.ViewModels;

namespace PhotoLibrary.App.Views;

public partial class AlbumsView : UserControl
{
    public AlbumsView()
    {
        InitializeComponent();
    }

    private AlbumsViewModel Vm => (AlbumsViewModel)DataContext!;

    private async void NewAlbumClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var result = await AlbumSettingsWindow.ShowAsync(owner, Vm, null);
        if (result is { Settings: { } s }) Vm.SelectedAlbum = await Vm.CreateAsync(s);
    }

    private async void SettingsClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner || Vm.SelectedAlbum is not { } album) return;
        var result = await AlbumSettingsWindow.ShowAsync(owner, Vm, album);
        if (result is null) return;
        if (result.Delete) await Vm.DeleteAsync(album);
        else if (result.Settings is { } s) await Vm.ApplySettingsAsync(album, s);
    }
}
