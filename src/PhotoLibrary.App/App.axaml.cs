using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using PhotoLibrary.App.Services;
using PhotoLibrary.App.ViewModels;
using PhotoLibrary.App.Views;

namespace PhotoLibrary.App;

public partial class App : Application
{
    private AppState? _state;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _state = new AppState();
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainViewModel(_state),
            };
            desktop.Exit += (_, _) => _state.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
