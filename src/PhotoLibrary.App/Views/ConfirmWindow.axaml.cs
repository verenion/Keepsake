using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PhotoLibrary.App.Views;

/// <summary>A yes/no prompt. Avalonia has no built-in message box, and this is all the app needs.</summary>
public partial class ConfirmWindow : Window
{
    public ConfirmWindow()
    {
        InitializeComponent();
    }

    public static async Task<bool> ShowAsync(Window owner, string title, string message, string confirmText)
    {
        var win = new ConfirmWindow { Title = title };
        win.Heading.Text = title;
        win.Message.Text = message;
        win.ConfirmButton.Content = confirmText;
        var result = await win.ShowDialog<bool?>(owner);
        return result == true;
    }

    private void ConfirmClick(object? sender, RoutedEventArgs e) => Close(true);
    private void CancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
