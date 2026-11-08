using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PhotoLibrary.App.Views;

/// <summary>Asks for a password and keeps asking until the validator accepts it or the user cancels.</summary>
public partial class PasswordWindow : Window
{
    private Func<string, Task<string?>> _validate = _ => Task.FromResult<string?>(null);

    public PasswordWindow()
    {
        InitializeComponent();
        Opened += (_, _) => PasswordBox.Focus();
    }

    public static async Task<bool> ShowAsync(Window owner, string title, string message, Func<string, Task<string?>> validate)
    {
        var win = new PasswordWindow { Title = title, _validate = validate };
        win.Heading.Text = title;
        win.Message.Text = message;
        return await win.ShowDialog<bool?>(owner) == true;
    }

    private async void OkClick(object? sender, RoutedEventArgs e)
    {
        var pw = PasswordBox.Text ?? "";
        if (pw.Length == 0) return;
        OkButton.IsEnabled = false;
        Error.IsVisible = false;
        try
        {
            var error = await _validate(pw);
            if (error is null) { PasswordBox.Text = ""; Close(true); return; }
            Error.Text = error;
            Error.IsVisible = true;
            PasswordBox.SelectAll();
            PasswordBox.Focus();
        }
        finally { OkButton.IsEnabled = true; }
    }

    private void CancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
