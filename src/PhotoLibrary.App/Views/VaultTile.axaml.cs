using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using PhotoLibrary.App.ViewModels;

namespace PhotoLibrary.App.Views;

/// <summary>A vault tile: grey with a lock until its thumbnail has been decrypted into memory; dropped when detached.</summary>
public partial class VaultTile : UserControl
{
    private VaultItemViewModel? _bound;
    private bool _attached;

    public VaultTile()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Rebind();
        PointerPressed += (_, _) => _bound?.Select();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        _bound?.EnsureThumbnail();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        _bound?.ReleaseThumbnail();
    }

    private void Rebind()
    {
        if (_bound is not null) _bound.PropertyChanged -= OnChanged;
        _bound = DataContext as VaultItemViewModel;
        if (_bound is null) return;
        _bound.PropertyChanged += OnChanged;
        Paint();
        if (_attached) _bound.EnsureThumbnail();
    }

    private void OnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VaultItemViewModel.IsSelected)) Paint();
    }

    private void Paint() =>
        Frame.BorderBrush = _bound is { IsSelected: true } ? new SolidColorBrush(Color.Parse("#3D8BFF")) : Brushes.Transparent;
}
