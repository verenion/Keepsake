using Avalonia.Controls;
using Avalonia.Input;
using PhotoLibrary.App.ViewModels;

namespace PhotoLibrary.App.Views;

public partial class DatabaseView : UserControl
{
    public DatabaseView()
    {
        InitializeComponent();
    }

    private void TagPhotoDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is DatabaseViewModel vm) vm.InspectTagPhotoCommand.Execute(null);
    }
}
