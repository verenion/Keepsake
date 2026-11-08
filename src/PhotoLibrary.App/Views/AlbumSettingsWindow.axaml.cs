using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using PhotoLibrary.App.Services;
using PhotoLibrary.App.ViewModels;
using PhotoLibrary.Core;
using PhotoLibrary.Core.Albums;
using PhotoLibrary.Core.Db;

namespace PhotoLibrary.App.Views;

/// <summary>Name, colour, cover and type of one album; also creates new ones and deletes existing ones.</summary>
public partial class AlbumSettingsWindow : Window
{
    public sealed record Outcome(AlbumSettingsResult? Settings, bool Delete);

    private readonly AlbumsViewModel _albums = null!;
    private readonly AlbumViewModel? _existing;
    private string _colour = Album.Palette[0];
    private long? _coverId;

    public AlbumSettingsWindow()
    {
        InitializeComponent();
    }

    private AlbumSettingsWindow(AlbumsViewModel albums, AlbumViewModel? existing) : this()
    {
        _albums = albums;
        _existing = existing;
        Title = existing is null ? "New album" : "Album settings";
        Heading.Text = existing is null ? "New album" : existing.Name;
        DeleteButton.IsVisible = existing is not null;
        SaveButton.Content = existing is null ? "Create" : "Save";
        if (existing is not null)
        {
            NameBox.Text = existing.Album.Name;
            _colour = existing.Album.Colour;
            _coverId = existing.Album.CoverFileId;
            (existing.IsSecure ? SecureRadio : existing.IsPhysical ? PhysicalRadio : VirtualRadio).IsChecked = true;
            if (existing.IsSecure)
            {
                // A vault is left by deleting it (which restores its photos), not by switching type.
                VirtualRadio.IsEnabled = PhysicalRadio.IsEnabled = false;
                PasswordLabel.Text = "Change password (optional)";
                CurrentPasswordBox.IsVisible = true;
                NewPasswordBox.Watermark = "New password (at least 8 characters)";
                ConfirmPasswordBox.Watermark = "Type the new password again";
            }
        }
        ColourBox.Text = _colour;
        ColourBox.TextChanged += (_, _) => { if (Album.IsValidColour(ColourBox.Text)) { _colour = ColourBox.Text!; PaintSwatches(); } };
        foreach (var c in Album.Palette)
        {
            var b = new Button { Classes = { "swatch" }, Background = new SolidColorBrush(Color.Parse(c)), Tag = c, Margin = new Avalonia.Thickness(0, 0, 6, 6) };
            b.Click += (_, _) => { _colour = c; ColourBox.Text = c; PaintSwatches(); };
            Swatches.Children.Add(b);
        }
        PaintSwatches();
        NameBox.TextChanged += (_, _) => UpdateHints();
        VirtualRadio.IsCheckedChanged += (_, _) => UpdateHints();
        PhysicalRadio.IsCheckedChanged += (_, _) => UpdateHints();
        SecureRadio.IsCheckedChanged += (_, _) => UpdateHints();
        UpdateHints();
        _ = LoadCoverAsync();
        Opened += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    public static async Task<Outcome?> ShowAsync(Window owner, AlbumsViewModel albums, AlbumViewModel? existing)
    {
        var win = new AlbumSettingsWindow(albums, existing);
        return await win.ShowDialog<Outcome?>(owner);
    }

    private AppState State => _albums.State;

    private void PaintSwatches()
    {
        foreach (var child in Swatches.Children.OfType<Button>())
            child.Classes.Set("chosen", string.Equals((string)child.Tag!, _colour, StringComparison.OrdinalIgnoreCase));
    }

    private void UpdateHints()
    {
        var name = (NameBox.Text ?? "").Trim();
        var folder = Path.Combine(LibraryFolders.Albums, Album.FolderName(name.Length == 0 ? "Name" : name));
        PhysicalHint.Text = $"A real folder in the library: {folder}\\. Adding a photo moves its file there (and out of any other folder album), " +
                            "removing it moves the file back into its dated folder. A photo can be in only one folder album.";
        var physical = PhysicalRadio.IsChecked == true;
        var secure = SecureRadio.IsChecked == true;
        var n = _existing?.Count ?? 0;
        PasswordPanel.IsVisible = secure;
        CoverPanel.IsVisible = !secure;
        if (secure)
        {
            KindWarning.IsVisible = _existing is { IsSecure: false } && n > 0;
            KindWarningText.Text = $"Saving will encrypt this album's {n:N0} photos into the vault and permanently delete the originals.";
            return;
        }
        var wasPhysical = _existing?.IsPhysical == true;
        KindWarning.IsVisible = _existing is not null && n > 0 && physical != wasPhysical;
        KindWarningText.Text = physical
            ? $"Saving will move {n:N0} files into {folder}\\."
            : $"Saving will move {n:N0} files out of the album folder back into their dated folders.";
        if (_existing is { IsPhysical: true } && physical && name.Length > 0 && !string.Equals(name, _existing.Album.Name, StringComparison.Ordinal))
        {
            KindWarning.IsVisible = true;
            KindWarningText.Text = $"Saving will rename the folder to {folder}\\.";
        }
    }

    private async Task LoadCoverAsync()
    {
        CoverImage.Source = null;
        CoverText.IsVisible = true;
        if (_coverId is not { } id) return;
        var record = State.Db.Query("id=@id", ("@id", id)).FirstOrDefault();
        if (record is null) return;
        CoverText.Text = record.FileName;
        var path = await State.Thumbs.GetAsync(record, CancellationToken.None);
        if (path is null) return;
        var bmp = await BitmapLoader.FromFileAsync(path, 200, CancellationToken.None);
        if (bmp is null) return;
        CoverImage.Source = bmp;
        CoverText.IsVisible = false;
    }

    private async void ChooseCoverClick(object? sender, RoutedEventArgs e)
    {
        var start = _existing is { IsPhysical: true } ex ? State.Albums.FolderFor(ex.Album) : State.LibraryRoot;
        var options = new FilePickerOpenOptions { Title = "Choose the cover photo", AllowMultiple = false };
        if (Directory.Exists(start)) options.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(start);
        var picked = await StorageProvider.OpenFilePickerAsync(options);
        if (picked.Count == 0 || picked[0].TryGetLocalPath() is not { } path) return;
        var record = State.Db.GetByPath(path);
        if (record is null) { ShowError("That file is not in the library."); return; }
        _coverId = record.Id;
        ShowError(null);
        await LoadCoverAsync();
    }

    private async void ClearCoverClick(object? sender, RoutedEventArgs e)
    {
        _coverId = null;
        CoverText.Text = "first photo";
        await LoadCoverAsync();
    }

    private void ShowError(string? text)
    {
        Error.Text = text ?? "";
        Error.IsVisible = text is not null;
    }

    private void SaveClick(object? sender, RoutedEventArgs e)
    {
        var name = (NameBox.Text ?? "").Trim();
        if (name.Length == 0) { ShowError("Give the album a name."); return; }
        if (_albums.Albums.Any(a => !ReferenceEquals(a, _existing) && string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            ShowError($"There is already an album called “{name}”.");
            return;
        }
        var kind = SecureRadio.IsChecked == true ? AlbumKind.Secure : PhysicalRadio.IsChecked == true ? AlbumKind.Physical : AlbumKind.Virtual;
        string? password = null, current = null;
        if (kind == AlbumKind.Secure)
        {
            var pw = NewPasswordBox.Text ?? "";
            var again = ConfirmPasswordBox.Text ?? "";
            var changing = _existing is { IsSecure: true };
            if (!changing || pw.Length > 0 || again.Length > 0 || (CurrentPasswordBox.Text ?? "").Length > 0)
            {
                if (pw.Length < 8) { ShowError("Use a password of at least 8 characters. A long passphrase is best."); return; }
                if (pw != again) { ShowError("The two passwords don't match."); return; }
                if (changing && (CurrentPasswordBox.Text ?? "").Length == 0) { ShowError("Enter the current password to change it."); return; }
                password = pw;
                current = changing ? CurrentPasswordBox.Text : null;
            }
        }
        NewPasswordBox.Text = ConfirmPasswordBox.Text = CurrentPasswordBox.Text = "";
        Close(new Outcome(new AlbumSettingsResult(name, _colour, kind, kind == AlbumKind.Secure ? null : _coverId, password, current), false));
    }

    private async void DeleteClick(object? sender, RoutedEventArgs e)
    {
        if (_existing is null) return;
        var n = _existing.Count;
        var photos = n == 1 ? "its one photo" : $"its {n:N0} photos";
        var message = _existing.IsSecure
            ? $"{(n == 1 ? "Its one item is" : $"Its {n:N0} items are")} decrypted back into the library's dated folders, unencrypted, and then the vault is deleted. You will be asked for the password if the album is locked."
            : _existing.IsPhysical
            ? $"The album is removed and {photos} are moved back into their dated folders. No photos are deleted."
            : $"The album list is removed. The photos stay in the library untouched.";
        var ok = await ConfirmWindow.ShowAsync(this, $"Delete “{_existing.Name}”?", message, "Delete album");
        if (ok) Close(new Outcome(null, true));
    }

    private void CancelClick(object? sender, RoutedEventArgs e) => Close(null);
}
