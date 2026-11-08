using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using PhotoLibrary.App.Services;
using PhotoLibrary.App.ViewModels;

namespace PhotoLibrary.App.Views;

/// <summary>
/// Thumbnail tile. Containers are virtualised and recycled, so the thumbnail is requested when the tile is
/// attached (or re-bound) and released when it is detached, keeping memory proportional to what's on screen.
/// </summary>
public partial class PhotoTile : UserControl
{
    private PhotoViewModel? _bound;
    private bool _attached;

    public PhotoTile()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Rebind();
        PointerPressed += OnPointerPressed;
        DoubleTapped += (_, _) => { if (_bound is { } vm) Formatting.OpenExternal(vm.Record.Path); };
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_bound is not { } vm) return;
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsRightButtonPressed)
        {
            // Right-click on an unselected tile acts on just that tile; on a selected one, on the whole selection.
            if (!vm.IsSelected) vm.Select(SelectMode.Single);
            return;
        }
        var mode = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? SelectMode.Range
            : e.KeyModifiers.HasFlag(KeyModifiers.Control) ? SelectMode.Toggle
            : SelectMode.Single;
        vm.Select(mode);
    }

    /// <summary>The album list changes at runtime, so the submenu is rebuilt each time the menu opens.</summary>
    private void MenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_bound is not { } vm) return;
        var main = vm.Main;
        var albums = main.Albums;
        var inAlbum = main.InAlbumGrid;
        RemoveFromAlbumItem.IsVisible = inAlbum;
        SetCoverItem.IsVisible = inAlbum && main.SelectedCount == 1;

        var items = new List<object>();
        foreach (var a in albums.Albums)
        {
            var target = a;
            var item = new MenuItem
            {
                Header = a.Name,
                Icon = new Ellipse { Width = 12, Height = 12, Fill = a.ColourBrush },
            };
            if (a.IsPhysical) ToolTip.SetTip(item, "Folder album: the files will be moved into it.");
            if (a.IsSecure)
            {
                item.Header = "🔒 " + a.Name;
                ToolTip.SetTip(item, "Secure album: the files are encrypted into it and the originals deleted.");
            }
            item.Click += async (_, _) => await main.AddSelectedToAlbumAsync(target);
            items.Add(item);
        }
        if (items.Count > 0) items.Add(new Separator());
        var create = new MenuItem { Header = "New album…" };
        create.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this) is not Window owner) return;
            var records = main.SelectedRecords;
            var result = await AlbumSettingsWindow.ShowAsync(owner, albums, null);
            if (result is { Settings: { } s }) await albums.CreateAsync(s, records);
        };
        items.Add(create);
        AlbumMenu.ItemsSource = items;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        Rebind();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        Unbind();
    }

    private void Rebind()
    {
        var vm = DataContext as PhotoViewModel;
        if (ReferenceEquals(vm, _bound)) { if (_attached) vm?.EnsureThumbnail(); return; }
        Unbind();
        _bound = vm;
        if (vm is null) return;
        vm.PropertyChanged += OnVmPropertyChanged;
        UpdateSelection();
        if (_attached) vm.EnsureThumbnail();
    }

    private void Unbind()
    {
        if (_bound is null) return;
        _bound.PropertyChanged -= OnVmPropertyChanged;
        _bound.ReleaseThumbnail();
        _bound = null;
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PhotoViewModel.IsSelected)) UpdateSelection();
    }

    private void UpdateSelection()
    {
        Frame.BorderBrush = _bound is { IsSelected: true } ? new SolidColorBrush(Color.Parse("#3D8BFF")) : Brushes.Transparent;
    }
}
