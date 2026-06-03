using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using PhotosPlus.Models;
using PhotosPlus.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;

namespace PhotosPlus;

public sealed partial class MainWindow : Window
{
    // Segoe MDL2 Assets glyphs for the eye toggle.
    private const string GlyphEyeOpen = "";  // View
    private const string GlyphEyeOff = "";   // Hide

    private readonly AppState _state = App.State;
    private readonly PhotoLibrary _library;
    private readonly AppWindow _appWindow;

    private readonly List<PhotoItem> _allPhotos = new();
    private readonly ObservableCollection<PhotoItem> _view = new();

    // Paths currently "obscured" in-session by the eye toggle (privacy curtain).
    private readonly HashSet<string> _obscured = new(StringComparer.OrdinalIgnoreCase);

    private int _currentIndex = -1;
    private double _rotation;
    private bool _isFullScreen;
    private bool _showHiddenAlbum;
    private bool _favoritesOnly;

    public MainWindow()
    {
        InitializeComponent();
        _library = new PhotoLibrary(_state);
        PhotoGrid.ItemsSource = _view;

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        _appWindow = AppWindow.GetFromWindowId(id);
        _appWindow.Title = "PhotosPlus";

        if (!string.IsNullOrEmpty(_state.LastFolder) && System.IO.Directory.Exists(_state.LastFolder))
        {
            _ = LoadFolderAsync(_state.LastFolder!);
        }
    }

    // 'new' intentionally hides the (unused) Window.Current; this is the currently viewed photo.
    private new PhotoItem? Current =>
        _currentIndex >= 0 && _currentIndex < _view.Count ? _view[_currentIndex] : null;

    private bool InViewer => ViewerView.Visibility == Visibility.Visible;

    // ===================== Folder loading =====================

    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        picker.FileTypeFilter.Add("*");
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            _state.LastFolder = folder.Path;
            _state.Save();
            await LoadFolderAsync(folder.Path);
        }
    }

    private async void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            ViewMode = PickerViewMode.Thumbnail
        };
        foreach (var ext in PhotoLibrary.SupportedExtensions) picker.FileTypeFilter.Add(ext);
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSingleFileAsync();
        if (file is not null) await OpenSinglePhotoAsync(file.Path);
    }

    /// <summary>Opens one image: loads its containing folder as the gallery and jumps to it.</summary>
    private async Task OpenSinglePhotoAsync(string path)
    {
        var folder = System.IO.Path.GetDirectoryName(path);
        if (folder is null) return;

        _state.LastFolder = folder;
        _state.Save();
        await LoadFolderAsync(folder);

        var match = _view.FirstOrDefault(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            _currentIndex = _view.IndexOf(match);
            ShowViewer();
            await LoadCurrentAsync();
        }
    }

    /// <summary>Builds a gallery from an explicit list of image files (multi-file drop).</summary>
    private async Task LoadPathsAsync(System.Collections.Generic.List<string> paths)
    {
        StatusText.Text = "Loading…";
        ShowGallery();

        var items = await Task.Run(() => _library.LoadFiles(paths));
        _allPhotos.Clear();
        _allPhotos.AddRange(items);
        RefreshView();

        StatusText.Text = $"{_allPhotos.Count} photo(s)";
        EmptyState.Visibility = _allPhotos.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ===================== Drag & drop =====================

    private void RootGrid_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            if (e.DragUIOverride is not null)
            {
                e.DragUIOverride.Caption = "Open in PhotosPlus";
                e.DragUIOverride.IsContentVisible = true;
            }
        }
        else
        {
            e.AcceptedOperation = DataPackageOperation.None;
        }
    }

    private async void RootGrid_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;

        var deferral = e.GetDeferral();
        try
        {
            var dropped = await e.DataView.GetStorageItemsAsync();

            // A dropped folder wins: load it as the gallery.
            var folder = dropped.OfType<StorageFolder>().FirstOrDefault();
            if (folder is not null)
            {
                _state.LastFolder = folder.Path;
                _state.Save();
                await LoadFolderAsync(folder.Path);
                return;
            }

            var files = dropped.OfType<StorageFile>()
                .Select(f => f.Path)
                .Where(PhotoLibrary.IsSupported)
                .ToList();

            if (files.Count == 1) await OpenSinglePhotoAsync(files[0]);
            else if (files.Count > 1) await LoadPathsAsync(files);
            else StatusText.Text = "No supported images in the dropped items.";
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async Task LoadFolderAsync(string folder)
    {
        StatusText.Text = "Loading…";
        ShowGallery();

        var items = await Task.Run(() => _library.Load(folder));
        _allPhotos.Clear();
        _allPhotos.AddRange(items);
        RefreshView();

        StatusText.Text = $"{_allPhotos.Count} photo(s) in {folder}";
        EmptyState.Visibility = _allPhotos.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Rebuilds the visible collection from filters (hidden album, favorites).</summary>
    private void RefreshView()
    {
        IEnumerable<PhotoItem> q = _allPhotos;
        q = _showHiddenAlbum ? q.Where(p => p.IsHidden) : q.Where(p => !p.IsHidden);
        if (_favoritesOnly) q = q.Where(p => p.IsFavorite);

        _view.Clear();
        foreach (var p in q) _view.Add(p);
    }

    private async void PhotoGrid_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue) return;
        if (args.Item is PhotoItem item && item.Thumbnail is null)
        {
            await item.LoadThumbnailAsync();
        }
    }

    // ===================== Gallery <-> Viewer =====================

    private void PhotoGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is PhotoItem item)
        {
            _currentIndex = _view.IndexOf(item);
            ShowViewer();
            _ = LoadCurrentAsync();
        }
    }

    private void BackToGallery_Click(object sender, RoutedEventArgs e) => ShowGallery();

    /// <summary>Completely closes the gallery: clears all loaded photos and returns to the empty state.</summary>
    private void CloseGallery_Click(object sender, RoutedEventArgs e)
    {
        _allPhotos.Clear();
        _view.Clear();
        _currentIndex = -1;
        _obscured.Clear();
        ViewerImage.Source = null;

        // Forget the last folder so the app starts empty next launch too.
        _state.LastFolder = null;
        _state.Save();

        _showHiddenAlbum = false;
        HiddenAlbumButton.IsChecked = false;
        _favoritesOnly = false;
        FavoritesFilterButton.IsChecked = false;

        ShowGallery();
        EmptyState.Visibility = Visibility.Visible;
        ModeLabel.Text = "";
        StatusText.Text = "Gallery closed";
    }

    private void ShowGallery()
    {
        ViewerView.Visibility = Visibility.Collapsed;
        GalleryView.Visibility = Visibility.Visible;
        InfoPanel.Visibility = Visibility.Collapsed;
        ModeLabel.Text = _showHiddenAlbum ? "· Hidden album" : "";
        SetViewerCommandsVisible(false);
    }

    private void ShowViewer()
    {
        GalleryView.Visibility = Visibility.Collapsed;
        ViewerView.Visibility = Visibility.Visible;
        SetViewerCommandsVisible(true);
    }

    private void SetViewerCommandsVisible(bool visible)
    {
        var v = visible ? Visibility.Visible : Visibility.Collapsed;
        BackButton.Visibility = v;
        ViewerSep1.Visibility = v;
        PrevButton.Visibility = v;
        NextButton.Visibility = v;
        ZoomInButton.Visibility = v;
        ZoomOutButton.Visibility = v;
        FitButton.Visibility = v;
        RotateButton.Visibility = v;
        FavoriteButton.Visibility = v;
        EyeButton.Visibility = v;
    }

    private async Task LoadCurrentAsync()
    {
        var item = Current;
        if (item is null)
        {
            ShowGallery();
            return;
        }

        _rotation = 0;
        ViewerRotate.Angle = 0;

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(item.Path);
            using var stream = await file.OpenReadAsync();
            var bmp = new BitmapImage();
            await bmp.SetSourceAsync(stream);
            ViewerImage.Source = bmp;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not open {item.FileName}: {ex.Message}";
            ViewerImage.Source = null;
        }

        FitImageToViewport();
        ImageScroller.ChangeView(0, 0, 1, true);
        UpdateFavoriteIcon();
        UpdateEyeState();
        ModeLabel.Text = $"· {item.FileName}  ({_currentIndex + 1}/{_view.Count})";
        if (InfoPanel.Visibility == Visibility.Visible) await PopulateInfoAsync();
    }

    private void Prev_Click(object sender, RoutedEventArgs e) => Navigate(-1);
    private void Next_Click(object sender, RoutedEventArgs e) => Navigate(+1);

    private void Navigate(int delta)
    {
        if (!InViewer || _view.Count == 0) return;
        _currentIndex = (_currentIndex + delta + _view.Count) % _view.Count;
        _ = LoadCurrentAsync();
    }

    // ===================== Zoom / rotate =====================

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => Zoom(1.25f);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => Zoom(0.8f);

    private void Zoom(float factor)
    {
        var target = Math.Clamp(ImageScroller.ZoomFactor * factor,
            ImageScroller.MinZoomFactor, ImageScroller.MaxZoomFactor);
        ImageScroller.ChangeView(null, null, target);
    }

    private void Fit_Click(object sender, RoutedEventArgs e)
    {
        FitImageToViewport();
        ImageScroller.ChangeView(0, 0, 1, false);
    }

    private void ImageScroller_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var target = ImageScroller.ZoomFactor > 1.05f ? 1f : 2.5f;
        ImageScroller.ChangeView(null, null, target);
    }

    /// <summary>Mouse wheel zooms in/out (no modifier needed).</summary>
    private void ImageScroller_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(ImageScroller).Properties.MouseWheelDelta;
        if (delta == 0) return;
        var target = Math.Clamp(ImageScroller.ZoomFactor * (delta > 0 ? 1.15f : 1f / 1.15f),
            ImageScroller.MinZoomFactor, ImageScroller.MaxZoomFactor);
        ImageScroller.ChangeView(null, null, target);
        e.Handled = true; // stop the ScrollViewer from scrolling instead of zooming
    }

    private void ImageScroller_SizeChanged(object sender, SizeChangedEventArgs e) => FitImageToViewport();

    /// <summary>
    /// Sizes the image box to the viewport so Uniform stretch scales the photo up or down
    /// to be fully visible by default. Zoom (wheel / buttons) then scales on top of this.
    /// </summary>
    private void FitImageToViewport()
    {
        if (ViewerImage.Source is null) return;
        if (ImageScroller.ViewportWidth <= 0 || ImageScroller.ViewportHeight <= 0) return;
        ViewerImage.Width = ImageScroller.ViewportWidth;
        ViewerImage.Height = ImageScroller.ViewportHeight;
    }

    private void Rotate_Click(object sender, RoutedEventArgs e)
    {
        _rotation = (_rotation + 90) % 360;
        ViewerRotate.Angle = _rotation;
    }

    // ===================== Favorites =====================

    private void Favorite_Click(object sender, RoutedEventArgs e)
    {
        var item = Current;
        if (item is null) return;
        item.IsFavorite = !item.IsFavorite;
        if (item.IsFavorite) _state.FavoritePaths.Add(item.Path);
        else _state.FavoritePaths.Remove(item.Path);
        _state.Save();
        UpdateFavoriteIcon();
        if (_favoritesOnly) RefreshView();
    }

    private void UpdateFavoriteIcon()
    {
        var fav = Current?.IsFavorite == true;
        FavoriteButton.Icon = new SymbolIcon(fav ? Symbol.SolidStar : Symbol.OutlineStar);
        FavoriteButton.Label = fav ? "Unfavorite" : "Favorite";
    }

    // ===================== Eye toggle (headline feature) =====================

    /// <summary>Default eye click = toggle the in-view privacy curtain for the current photo.</summary>
    private void Eye_Click(object sender, RoutedEventArgs e) => ToggleObscure();

    private void EyeObscure_Click(object sender, RoutedEventArgs e) => ToggleObscure();

    private void ToggleObscure()
    {
        var item = Current;
        if (item is null) return;

        if (_obscured.Contains(item.Path)) _obscured.Remove(item.Path);
        else _obscured.Add(item.Path);

        UpdateEyeState();
    }

    private void UpdateEyeState()
    {
        var obscured = Current is not null && _obscured.Contains(Current.Path);
        ObscureOverlay.Visibility = obscured ? Visibility.Visible : Visibility.Collapsed;
        // Eye glyph reflects state: open eye = visible, eye-off = hidden.
        EyeIcon.Glyph = obscured ? GlyphEyeOff : GlyphEyeOpen;
        EyeButton.Label = obscured ? "Reveal" : "Hide";
    }

    /// <summary>Permanently flag the current photo as hidden (Hidden album); never deletes the file.</summary>
    private void EyeHidePermanent_Click(object sender, RoutedEventArgs e)
    {
        var item = Current;
        if (item is null) return;

        item.IsHidden = true;
        _state.HiddenPaths.Add(item.Path);
        _state.Save();
        _obscured.Remove(item.Path);

        StatusText.Text = $"{item.FileName} moved to Hidden album";

        // If we're not viewing the Hidden album, it should leave the current view.
        if (!_showHiddenAlbum)
        {
            RefreshView();
            if (_view.Count == 0) { ShowGallery(); return; }
            _currentIndex = Math.Min(_currentIndex, _view.Count - 1);
            _ = LoadCurrentAsync();
        }
    }

    private void HiddenAlbum_Click(object sender, RoutedEventArgs e)
    {
        _showHiddenAlbum = HiddenAlbumButton.IsChecked == true;
        ShowGallery();
        RefreshView();
        StatusText.Text = _showHiddenAlbum
            ? $"Hidden album — {_view.Count} photo(s)"
            : $"{_view.Count} photo(s)";
    }

    private void FavoritesFilter_Click(object sender, RoutedEventArgs e)
    {
        _favoritesOnly = FavoritesFilterButton.IsChecked == true;
        RefreshView();
    }

    // ===================== Info / Reveal / Delete =====================

    private async void Info_Click(object sender, RoutedEventArgs e)
    {
        InfoPanel.Visibility = InfoPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed : Visibility.Visible;
        if (InfoPanel.Visibility == Visibility.Visible) await PopulateInfoAsync();
    }

    private async Task PopulateInfoAsync()
    {
        var item = Current;
        if (item is null) return;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(item.Path);
            var basic = await file.GetBasicPropertiesAsync();
            var img = await file.Properties.GetImagePropertiesAsync();

            var sizeMb = basic.Size / 1024d / 1024d;
            var lines = new List<string>
            {
                $"Name: {item.FileName}",
                $"Folder: {System.IO.Path.GetDirectoryName(item.Path)}",
                $"Dimensions: {img.Width} × {img.Height}",
                $"Size: {sizeMb:0.00} MB",
                $"Modified: {basic.DateModified.LocalDateTime}",
            };
            if (img.DateTaken.Year > 1601) lines.Add($"Taken: {img.DateTaken.LocalDateTime}");
            if (!string.IsNullOrWhiteSpace(img.CameraManufacturer) || !string.IsNullOrWhiteSpace(img.CameraModel))
                lines.Add($"Camera: {img.CameraManufacturer} {img.CameraModel}".Trim());
            lines.Add($"Favorite: {(item.IsFavorite ? "yes" : "no")}");
            lines.Add($"Hidden: {(item.IsHidden ? "yes" : "no")}");

            InfoText.Text = string.Join(Environment.NewLine, lines);
        }
        catch (Exception ex)
        {
            InfoText.Text = $"Metadata unavailable: {ex.Message}";
        }
    }

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        var item = Current ?? (_view.Count > 0 ? _view[0] : null);
        if (item is null) return;
        try
        {
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{item.Path}\"");
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        var item = Current;
        if (item is null) return;

        var dialog = new ContentDialog
        {
            Title = "Delete photo",
            Content = $"Move \"{item.FileName}\" to the Recycle Bin?",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = RootGrid.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(item.Path);
            await file.DeleteAsync(StorageDeleteOption.Default); // to Recycle Bin
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Delete failed: {ex.Message}";
            return;
        }

        _state.HiddenPaths.Remove(item.Path);
        _state.FavoritePaths.Remove(item.Path);
        _state.Save();
        _allPhotos.Remove(item);
        RefreshView();

        if (_view.Count == 0) { ShowGallery(); return; }
        _currentIndex = Math.Min(_currentIndex, _view.Count - 1);
        await LoadCurrentAsync();
    }

    // ===================== Full screen =====================

    private void FullScreen_Click(object sender, RoutedEventArgs e) => ToggleFullScreen();

    private void ToggleFullScreen()
    {
        _isFullScreen = !_isFullScreen;
        _appWindow.SetPresenter(_isFullScreen
            ? AppWindowPresenterKind.FullScreen
            : AppWindowPresenterKind.Default);
    }

    // ===================== Slideshow =====================

    private void Slideshow_Click(object sender, RoutedEventArgs e) => StartSlideshow();

    private void StartSlideshow()
    {
        // Slideshow always skips hidden photos, regardless of current filter.
        var photos = _view.Where(p => !p.IsHidden).ToList();
        if (photos.Count == 0)
        {
            StatusText.Text = "Nothing to show — no visible photos.";
            return;
        }
        var start = Current is not null ? Math.Max(0, photos.IndexOf(Current)) : 0;
        var slideshow = new SlideshowWindow(photos, start, _state);
        slideshow.Activate();
    }

    // ===================== Keyboard =====================

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.F5:
                StartSlideshow(); e.Handled = true; break;
            case VirtualKey.H when InViewer:
                ToggleObscure(); e.Handled = true; break;
            case VirtualKey.Left when InViewer:
                Navigate(-1); e.Handled = true; break;
            case VirtualKey.Right when InViewer:
                Navigate(+1); e.Handled = true; break;
            case VirtualKey.Escape when InViewer:
                if (_isFullScreen) ToggleFullScreen(); else ShowGallery();
                e.Handled = true; break;
            case VirtualKey.F11:
            case VirtualKey.F:
                ToggleFullScreen(); e.Handled = true; break;
            case VirtualKey.Add when InViewer:
            case (VirtualKey)187 when InViewer: // '='/'+'
                Zoom(1.25f); e.Handled = true; break;
            case VirtualKey.Subtract when InViewer:
            case (VirtualKey)189 when InViewer: // '-'
                Zoom(0.8f); e.Handled = true; break;
            case VirtualKey.Number0 when InViewer:
                ImageScroller.ChangeView(0, 0, 1, false); e.Handled = true; break;
            case VirtualKey.R when InViewer:
                Rotate_Click(sender, e); e.Handled = true; break;
            case VirtualKey.Delete when InViewer:
                Delete_Click(sender, e); e.Handled = true; break;
        }
    }
}
