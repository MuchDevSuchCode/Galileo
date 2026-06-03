using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PhotosPlus.Models;
using PhotosPlus.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
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

    private readonly DispatcherTimer _chromeTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool _loadingSettings;

    // Collage mode state
    private readonly Random _rng = new();
    private List<PhotoItem> _collageSource = new();
    private List<PhotoItem> _collageItems = new();
    private int _collageCount;

    public MainWindow(string? initialPath = null)
    {
        InitializeComponent();
        _library = new PhotoLibrary(_state);
        PhotoGrid.ItemsSource = _view;

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        _appWindow = AppWindow.GetFromWindowId(id);
        _appWindow.Title = "PhotosPlus";

        // Mica backdrop for a modern translucent window.
        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();

        // Seamless modern chrome: draw our own content up into the title bar.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            var tb = _appWindow.TitleBar;
            tb.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
            tb.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        }

        _chromeTimer.Tick += (_, _) =>
        {
            _chromeTimer.Stop();
            ViewerChrome.Opacity = 0;
            ViewerChrome.IsHitTestVisible = false;
        };

        // Cold start shows the welcome / picker screen — unless Windows launched us with a
        // file/folder to open (default photo app / "Open with").
        if (!string.IsNullOrEmpty(initialPath))
        {
            if (System.IO.Directory.Exists(initialPath))
                _ = LoadFolderAsync(initialPath);
            else
                _ = OpenSinglePhotoAsync(initialPath!);
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
        CollageView.Visibility = Visibility.Collapsed;
        GalleryView.Visibility = Visibility.Visible;
        InfoPanel.Visibility = Visibility.Collapsed;
        _chromeTimer.Stop();
        ModeLabel.Text = _showHiddenAlbum ? "· Hidden album" : "";
    }

    private void ShowViewer()
    {
        GalleryView.Visibility = Visibility.Collapsed;
        CollageView.Visibility = Visibility.Collapsed;
        ViewerView.Visibility = Visibility.Visible;
        ShowChrome();
    }

    // --- Viewer chrome auto-hide ---

    private void ShowChrome()
    {
        ViewerChrome.Opacity = 1;
        ViewerChrome.IsHitTestVisible = true;
        _chromeTimer.Stop();
        _chromeTimer.Start();
    }

    private void ViewerView_PointerMoved(object sender, PointerRoutedEventArgs e) => ShowChrome();

    private async Task LoadCurrentAsync()
    {
        var item = Current;
        if (item is null)
        {
            ShowGallery();
            return;
        }

        _rotation = 0;
        _bmpW = _bmpH = 0;

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(item.Path);
            using var stream = await file.OpenReadAsync();
            var bmp = new BitmapImage();
            await bmp.SetSourceAsync(stream);
            ViewerImage.Source = bmp;
            _bmpW = bmp.PixelWidth;
            _bmpH = bmp.PixelHeight;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not open {item.FileName}: {ex.Message}";
            ViewerImage.Source = null;
        }

        ResetView();
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

    // ===================== Zoom / pan / rotate =====================
    //
    // The Image fills ImageHost and uses Stretch=Uniform, so at scale 1 the photo is
    // fully visible (scaled up or down to fit). We apply zoom (scale), pan (translate)
    // and rotation through a single CompositeTransform that we drive directly — no
    // ScrollViewer, so the mouse wheel zooms instead of scrolling.

    private const double MaxScale = 8.0;

    private double _scale = 1.0;
    private double _minScale = 1.0;   // the fit scale for the current rotation (1.0 unless rotated 90/270)
    private double _tx;
    private double _ty;
    private double _bmpW;             // source pixel size of the current photo
    private double _bmpH;

    private bool _panning;
    private Windows.Foundation.Point _panStart;
    private double _panStartTx;
    private double _panStartTy;

    private void ApplyTransform()
    {
        ViewerTransform.ScaleX = _scale;
        ViewerTransform.ScaleY = _scale;
        ViewerTransform.TranslateX = _tx;
        ViewerTransform.TranslateY = _ty;
        ViewerTransform.Rotation = _rotation;
    }

    /// <summary>
    /// Scale that makes the photo fully fit the host at the current rotation. 1.0 for
    /// 0°/180°; for 90°/270° the width/height swap, so the image is scaled down to fit.
    /// </summary>
    private double FitScaleForRotation()
    {
        double W = ImageHost.ActualWidth, H = ImageHost.ActualHeight;
        if (_bmpW <= 0 || _bmpH <= 0 || W <= 0 || H <= 0) return 1.0;

        var uniform = Math.Min(W / _bmpW, H / _bmpH); // displayed size at scale 1 (Uniform stretch)
        double fw = _bmpW * uniform, fh = _bmpH * uniform;

        var quarterTurn = _rotation % 180 != 0;
        double boxW = quarterTurn ? fh : fw;
        double boxH = quarterTurn ? fw : fh;

        var fit = Math.Min(W / boxW, H / boxH);
        return fit < 1.0 ? fit : 1.0;
    }

    /// <summary>Resets to the centered fit for the current rotation (zoom/pan cleared).</summary>
    private void ResetView()
    {
        _minScale = FitScaleForRotation();
        _scale = _minScale;
        _tx = 0;
        _ty = 0;
        ApplyTransform();
    }

    /// <summary>Zoom about a focal point (in ImageHost coordinates) so it stays put.</summary>
    private void ZoomAt(double factor, Windows.Foundation.Point focus)
    {
        var newScale = Math.Clamp(_scale * factor, _minScale, MaxScale);
        var ratio = newScale / _scale;
        // Pivot is the host centre (RenderTransformOrigin = 0.5,0.5), so anchor relative to it.
        var ux = focus.X - ImageHost.ActualWidth / 2;
        var uy = focus.Y - ImageHost.ActualHeight / 2;
        _tx = ux - ratio * (ux - _tx);
        _ty = uy - ratio * (uy - _ty);
        _scale = newScale;
        if (_scale <= _minScale + 0.001) { _tx = 0; _ty = 0; } // snap back to centered fit
        ApplyTransform();
    }

    private Windows.Foundation.Point HostCenter() =>
        new(ImageHost.ActualWidth / 2, ImageHost.ActualHeight / 2);

    private bool IsAtFit => _scale <= _minScale + 0.001;

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => ZoomAt(1.25, HostCenter());
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => ZoomAt(0.8, HostCenter());
    private void Fit_Click(object sender, RoutedEventArgs e) => ResetView();

    private void ImageHost_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (!IsAtFit) ResetView();
        else ZoomAt(2.5, e.GetPosition(ImageHost));
    }

    /// <summary>Mouse wheel zooms in/out toward the cursor (no modifier needed).</summary>
    private void ImageHost_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(ImageHost);
        var delta = point.Properties.MouseWheelDelta;
        if (delta == 0) return;
        ZoomAt(delta > 0 ? 1.15 : 1.0 / 1.15, point.Position);
        e.Handled = true;
    }

    // --- Drag to pan when zoomed in ---

    private void ImageHost_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (IsAtFit) return; // nothing to pan at fit
        var point = e.GetCurrentPoint(ImageHost);
        if (!point.Properties.IsLeftButtonPressed) return;

        _panning = true;
        _panStart = point.Position;
        _panStartTx = _tx;
        _panStartTy = _ty;
        ImageHost.CapturePointer(e.Pointer);
    }

    private void ImageHost_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_panning) return;
        var p = e.GetCurrentPoint(ImageHost).Position;
        _tx = _panStartTx + (p.X - _panStart.X);
        _ty = _panStartTy + (p.Y - _panStart.Y);
        ApplyTransform();
    }

    private void ImageHost_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_panning) return;
        _panning = false;
        ImageHost.ReleasePointerCapture(e.Pointer);
    }

    /// <summary>Keeps the image clipped to the host so a zoomed photo can't overlap the chrome.</summary>
    private void ImageHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ImageHost.Clip = new Microsoft.UI.Xaml.Media.RectangleGeometry
        {
            Rect = new Windows.Foundation.Rect(0, 0, ImageHost.ActualWidth, ImageHost.ActualHeight)
        };
        if (IsAtFit) ResetView(); // keep the photo fitted as the window resizes
    }

    /// <summary>Once decoded we know the true pixel size; re-fit if the user hasn't zoomed.</summary>
    private void ViewerImage_ImageOpened(object sender, RoutedEventArgs e)
    {
        if (ViewerImage.Source is BitmapImage b && b.PixelWidth > 0)
        {
            _bmpW = b.PixelWidth;
            _bmpH = b.PixelHeight;
            if (IsAtFit) ResetView();
        }
    }

    private void Rotate_Click(object sender, RoutedEventArgs e)
    {
        _rotation = (_rotation + 90) % 360;
        ResetView(); // re-fit (and re-centre) so the rotated image stays fully in view
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
        FavoriteIcon.Glyph = fav ? "" : ""; // filled / outline star // filled / outline star
        FavoriteIcon.Foreground = fav
            ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gold)
            : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White);
        ToolTipService.SetToolTip(FavoriteButton, fav ? "Unfavorite" : "Favorite");
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
        ToolTipService.SetToolTip(EyeButton, obscured ? "Reveal (H)" : "Hide (H)");
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

    // ===================== Collage =====================

    private bool InCollage => CollageView.Visibility == Visibility.Visible;

    private async void Collage_Click(object sender, RoutedEventArgs e)
    {
        var pool = _view.Where(p => !p.IsHidden).ToList();
        if (pool.Count == 0)
        {
            StatusText.Text = "No photos to make a collage.";
            return;
        }
        _collageSource = pool;
        _collageCount = Math.Min(pool.Count, 12);

        GalleryView.Visibility = Visibility.Collapsed;
        ViewerView.Visibility = Visibility.Collapsed;
        InfoPanel.Visibility = Visibility.Collapsed;
        CollageView.Visibility = Visibility.Visible;
        ModeLabel.Text = "· Collage";

        await RebuildCollageAsync(reshuffle: true);
    }

    private async System.Threading.Tasks.Task RebuildCollageAsync(bool reshuffle)
    {
        if (_collageSource.Count == 0) return;
        _collageCount = Math.Clamp(_collageCount, 1, _collageSource.Count);

        if (reshuffle || _collageItems.Count != _collageCount)
            _collageItems = _collageSource.OrderBy(_ => _rng.Next()).Take(_collageCount).ToList();

        CollageCountText.Text = $"{_collageItems.Count} photo{(_collageItems.Count == 1 ? "" : "s")}";

        await System.Threading.Tasks.Task.WhenAll(_collageItems.Select(i => i.EnsureAspectAsync()));
        LayoutCollage();
    }

    private void LayoutCollage()
    {
        CollageCanvas.Children.Clear();
        if (_collageItems.Count == 0) return;

        var tiles = CollageLayout.Compute(_collageItems, CollageCanvas.ActualWidth, CollageCanvas.ActualHeight, 6);
        foreach (var tile in tiles)
        {
            var image = new Image { Stretch = Stretch.UniformToFill };
            var border = new Border
            {
                Width = tile.Width,
                Height = tile.Height,
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Black),
                Child = image
            };
            var item = tile.Item;
            border.Tapped += (_, _) => OpenFromCollage(item);
            Canvas.SetLeft(border, tile.X);
            Canvas.SetTop(border, tile.Y);
            CollageCanvas.Children.Add(border);
            _ = LoadTileAsync(image, item, (int)Math.Ceiling(tile.Width));
        }
    }

    private static async System.Threading.Tasks.Task LoadTileAsync(Image image, PhotoItem item, int decodeWidth)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(item.Path);
            using var stream = await file.OpenReadAsync();
            var bmp = new BitmapImage();
            if (decodeWidth > 0) bmp.DecodePixelWidth = decodeWidth;
            await bmp.SetSourceAsync(stream);
            image.Source = bmp;
        }
        catch
        {
            // Skip unreadable tiles.
        }
    }

    private void OpenFromCollage(PhotoItem item)
    {
        var idx = _view.IndexOf(item);
        if (idx < 0) return;
        _currentIndex = idx;
        ShowViewer();
        _ = LoadCurrentAsync();
    }

    private void CollageCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (InCollage && _collageItems.Count > 0) LayoutCollage();
    }

    private void CollageBack_Click(object sender, RoutedEventArgs e) => ShowGallery();
    private async void CollageShuffle_Click(object sender, RoutedEventArgs e) => await RebuildCollageAsync(reshuffle: true);

    private async void CollageFewer_Click(object sender, RoutedEventArgs e)
    {
        _collageCount = Math.Max(1, _collageCount - 1);
        await RebuildCollageAsync(reshuffle: true);
    }

    private async void CollageMore_Click(object sender, RoutedEventArgs e)
    {
        _collageCount = Math.Min(_collageSource.Count, _collageCount + 1);
        await RebuildCollageAsync(reshuffle: true);
    }

    private async void CollageSave_Click(object sender, RoutedEventArgs e)
    {
        if (_collageItems.Count == 0) return;
        try
        {
            var rtb = new RenderTargetBitmap();
            await rtb.RenderAsync(CollageCanvas);
            var pixels = await rtb.GetPixelsAsync();

            var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
            picker.FileTypeChoices.Add("PNG image", new List<string> { ".png" });
            picker.SuggestedFileName = "collage";
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSaveFileAsync();
            if (file is null) return;

            using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                (uint)rtb.PixelWidth, (uint)rtb.PixelHeight, 96, 96, pixels.ToArray());
            await encoder.FlushAsync();
            StatusText.Text = $"Collage saved to {file.Path}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Save failed: {ex.Message}";
        }
    }

    // ===================== Settings =====================

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        _loadingSettings = true;
        SlideshowSecondsSlider.Value = Math.Clamp(_state.SlideshowSeconds, 2, 30);
        SlideshowSecondsValue.Text = $"{_state.SlideshowSeconds}s";
        ShuffleSwitch.IsOn = _state.SlideshowShuffle;
        LoopSwitch.IsOn = _state.SlideshowLoop;
        TransitionCombo.SelectedIndex = (int)_state.SlideshowTransition;
        _loadingSettings = false;

        SettingsOverlay.Visibility = Visibility.Visible;
    }

    private void CloseSettings()
    {
        SettingsOverlay.Visibility = Visibility.Collapsed;
        _state.Save();
    }

    private void SettingsClose_Click(object sender, RoutedEventArgs e) => CloseSettings();

    // Tap on the dim scrim closes; tap inside the card is swallowed so it doesn't bubble up.
    private void SettingsScrim_Tapped(object sender, TappedRoutedEventArgs e) => CloseSettings();
    private void SettingsCard_Tapped(object sender, TappedRoutedEventArgs e) => e.Handled = true;

    private void SlideshowSecondsSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loadingSettings) return;
        _state.SlideshowSeconds = (int)Math.Round(e.NewValue);
        SlideshowSecondsValue.Text = $"{_state.SlideshowSeconds}s";
    }

    private void ShuffleSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _state.SlideshowShuffle = ShuffleSwitch.IsOn;
    }

    private void LoopSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _state.SlideshowLoop = LoopSwitch.IsOn;
    }

    private void TransitionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings) return;
        _state.SlideshowTransition = (SlideshowTransition)Math.Max(0, TransitionCombo.SelectedIndex);
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
            case VirtualKey.Escape when SettingsOverlay.Visibility == Visibility.Visible:
                CloseSettings(); e.Handled = true; break;
            case VirtualKey.Escape when InCollage:
                ShowGallery(); e.Handled = true; break;
            case VirtualKey.Escape when InViewer:
                if (_isFullScreen) ToggleFullScreen(); else ShowGallery();
                e.Handled = true; break;
            case VirtualKey.F11:
            case VirtualKey.F:
                ToggleFullScreen(); e.Handled = true; break;
            case VirtualKey.Add when InViewer:
            case (VirtualKey)187 when InViewer: // '='/'+'
                ZoomAt(1.25, HostCenter()); e.Handled = true; break;
            case VirtualKey.Subtract when InViewer:
            case (VirtualKey)189 when InViewer: // '-'
                ZoomAt(0.8, HostCenter()); e.Handled = true; break;
            case VirtualKey.Number0 when InViewer:
                ResetView(); e.Handled = true; break;
            case VirtualKey.R when InViewer:
                Rotate_Click(sender, e); e.Handled = true; break;
            case VirtualKey.Delete when InViewer:
                Delete_Click(sender, e); e.Handled = true; break;
        }
    }
}
