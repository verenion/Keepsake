using Avalonia.Controls;
using Avalonia.Interactivity;
using PhotoLibrary.App.ViewModels;

namespace PhotoLibrary.App.Views;

public partial class OrganiseWindow : Window
{
    public OrganiseWindow()
    {
        InitializeComponent();
    }

    private void CancelClick(object? sender, RoutedEventArgs e) => ((OrganiseViewModel)DataContext!).Cancel();

    private void CloseClick(object? sender, RoutedEventArgs e) => Close();
}
