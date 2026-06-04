using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PhotosPlus.Models;
using PhotosPlus.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Media.Core;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
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

    // File-explorer state
    private FileSystemService _fs = null!;
    private readonly ObservableCollection<ExplorerItem> _explorerItems = new();
    private readonly List<string?> _navHistory = new();
    private List<ExplorerItem> _explorerRaw = new();
    private int _navIndex = -1;
    private string? _currentFolder; // null = home (This PC)
    private bool _showAppHidden;
    private string _explorerViewMode = "Large";
    private double _iconSize = 110;
    private ExplorerItem? _explorerContextItem;

    // Collage mode state
    private readonly Random _rng = new();
    private List<PhotoItem> _collageSource = new();
    private List<PhotoItem> _collageItems = new();
    private int _collageCount;
    private CollagePreset _collagePreset = CollagePreset.Justified;

    public MainWindow(string? initialPath = null)
    {
        InitializeComponent();
        _library = new PhotoLibrary(_state);
        PhotoGrid.ItemsSource = _view;

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        _appWindow = AppWindow.GetFromWindowId(id);
        _appWindow.Title = "Galileo";
        try { _appWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "galileo.ico")); } catch { }
        try { TitleLogo.Source = new BitmapImage(new Uri(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "galileo.png"))); } catch { }

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

        // File explorer is the home view.
        _fs = new FileSystemService(_state);
        ExplorerIconsView.ItemsSource = _explorerItems;
        ExplorerDetailsList.ItemsSource = _explorerItems;
        ExplorerIconsView.ItemTemplate = (DataTemplate)Application.Current.Resources["ExplorerIconTemplate"];
        _iconSize = _state.IconSize is > 0 and <= 240 ? _state.IconSize : 110;
        _collagePreset = ParseCollagePreset(_state.CollagePreset);
        ExplorerItem.ShowFolderPreviews = _state.FolderPreviews;
        ExplorerItem.ShowExtensions = _state.ShowExtensions;

        PopulateSidebar();
        IconSizeSlider.Value = _iconSize;
        ApplyIconSize();
        ApplyTheme();
        ApplyClickMode();
        SyncSortGroupRadios();

        // Windows may launch us with a file (default app) or folder to open.
        if (!string.IsNullOrEmpty(initialPath) && System.IO.File.Exists(initialPath))
        {
            var dir = System.IO.Path.GetDirectoryName(initialPath);
            NavigateTo(dir);
            var match = _explorerItems.FirstOrDefault(i => string.Equals(i.Path, initialPath, StringComparison.OrdinalIgnoreCase));
            if (match is not null) OpenImageFromExplorer(match);
        }
        else if (!string.IsNullOrEmpty(initialPath) && System.IO.Directory.Exists(initialPath))
        {
            NavigateTo(initialPath);
        }
        else
        {
            NavigateTo(null); // This PC / home
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
        ShowExplorer();

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
                e.DragUIOverride.Caption = "Open in Galileo";
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

            // Dropping onto an open collage adds the images to it.
            if (InCollage)
            {
                var dropFiles = dropped.OfType<StorageFile>()
                    .Select(f => f.Path).Where(PhotoLibrary.IsSupported).ToList();
                if (dropFiles.Count > 0) await AddToCollageAsync(dropFiles);
                else StatusText.Text = "No supported images in the dropped items.";
                return;
            }

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
        ShowExplorer();

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

    private void BackToGallery_Click(object sender, RoutedEventArgs e) => ShowExplorer();

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

        ShowExplorer();
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
        ModeLabel.Text = _showHiddenAlbum ? "Hidden album" : "";
    }

    private void ShowViewer()
    {
        ExplorerView.Visibility = Visibility.Collapsed;
        GalleryView.Visibility = Visibility.Collapsed;
        CollageView.Visibility = Visibility.Collapsed;
        ViewerView.Visibility = Visibility.Visible;
        ShowChrome();
    }

    /// <summary>Returns to the file-explorer home (the photo viewer / collage live on top of it).</summary>
    private void ShowExplorer()
    {
        StopVideo();
        ViewerView.Visibility = Visibility.Collapsed;
        GalleryView.Visibility = Visibility.Collapsed;
        CollageView.Visibility = Visibility.Collapsed;
        SettingsOverlay.Visibility = Visibility.Collapsed;
        InfoPanel.Visibility = Visibility.Collapsed;
        ExplorerView.Visibility = Visibility.Visible;
        _chromeTimer.Stop();
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
            ShowExplorer();
            return;
        }

        EnterImageMode();
        _rotation = 0;
        _bmpW = _bmpH = 0;

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(item.Path);

            // Cap the decoded size on the longer side. Decoding at full resolution can exceed the
            // GPU's max texture size on large images (panoramas/huge screenshots) and crash the
            // render thread — a failure the try/catch here can't see. 8000px is well under the
            // ~16384 D3D limit and still sharp for screen + zoom.
            const int maxSide = 8000;
            var props = await file.Properties.GetImagePropertiesAsync();
            uint w = props.Width, h = props.Height;

            using var stream = await file.OpenReadAsync();
            var bmp = new BitmapImage { DecodePixelType = DecodePixelType.Logical };
            if (w > 0 && h > 0 && Math.Max(w, h) > maxSide)
            {
                if (w >= h) bmp.DecodePixelWidth = maxSide;
                else bmp.DecodePixelHeight = maxSide;
            }
            await bmp.SetSourceAsync(stream);
            ViewerImage.Source = bmp;
            _bmpW = bmp.PixelWidth;
            _bmpH = bmp.PixelHeight;
        }
        catch (Exception ex)
        {
            App.Log("LoadCurrent", ex);
            StatusText.Text = $"Could not open {item.FileName}: {ex.Message}";
            ViewerImage.Source = null;
        }

        ResetView();
        UpdateFavoriteIcon();
        UpdateEyeState();
        ModeLabel.Text = $"{item.FileName}   ({_currentIndex + 1}/{_view.Count})";
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

        // The Image fills the host with Uniform stretch, so at transform-scale 1 the photo is
        // already magnified by `uniform`. We want the base view magnified by `m` instead:
        //   m = min(1.0, fitMagnification)  → fit large photos, but never upscale small ones.
        var uniform = Math.Min(W / _bmpW, H / _bmpH);
        var quarterTurn = _rotation % 180 != 0;
        var fitMagnification = quarterTurn ? Math.Min(W / _bmpH, H / _bmpW) : uniform;
        var m = Math.Min(1.0, fitMagnification);
        return m / uniform; // transform scale that yields magnification m
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

    private void ViewerImage_ImageFailed(object sender, ExceptionRoutedEventArgs e)
    {
        App.Log("ImageFailed", new Exception(e.ErrorMessage));
        var name = Current?.FileName ?? "image";
        StatusText.Text = $"Could not display {name}: {e.ErrorMessage}";
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
        if (Current is { } item) FavoriteItem(item);
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
        if (Current is { } item) HideItemPermanently(item);
    }

    private void HiddenAlbum_Click(object sender, RoutedEventArgs e)
    {
        _showHiddenAlbum = HiddenAlbumButton.IsChecked == true;
        ShowExplorer();
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

    // ===================== Selection =====================

    private void SelectMode_Click(object sender, RoutedEventArgs e)
    {
        if (SelectModeButton.IsChecked == true)
        {
            PhotoGrid.SelectionMode = ListViewSelectionMode.Multiple;
            PhotoGrid.IsItemClickEnabled = false;
            ModeLabel.Text = "Select photos — then Collage";
        }
        else
        {
            PhotoGrid.SelectedItems.Clear();
            PhotoGrid.SelectionMode = ListViewSelectionMode.None;
            PhotoGrid.IsItemClickEnabled = true;
            ModeLabel.Text = _showHiddenAlbum ? "Hidden album" : "";
        }
    }

    private void PhotoGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PhotoGrid.SelectionMode != ListViewSelectionMode.None)
            StatusText.Text = $"{PhotoGrid.SelectedItems.Count} selected";
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
            lines.Add($"Favorite: {(item.IsFavorite ? "" : "")}");
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
        if (item is not null) RevealItem(item);
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Current is { } item) await DeleteItemAsync(item);
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
        // Use the user's selection if they're picking photos; otherwise all visible photos.
        List<PhotoItem> pool;
        bool fromSelection = SelectModeButton.IsChecked == true && PhotoGrid.SelectedItems.Count > 0;
        if (fromSelection)
            pool = PhotoGrid.SelectedItems.OfType<PhotoItem>().Where(p => !p.IsHidden).ToList();
        else
            pool = _view.Where(p => !p.IsHidden).ToList();

        if (pool.Count == 0)
        {
            StatusText.Text = "No photos to make a collage.";
            return;
        }
        _collageSource = pool;
        // When the user hand-picked photos, include them all; otherwise sample a screen-friendly number.
        _collageCount = fromSelection ? pool.Count : Math.Min(pool.Count, 12);

        ExplorerView.Visibility = Visibility.Collapsed;
        GalleryView.Visibility = Visibility.Collapsed;
        ViewerView.Visibility = Visibility.Collapsed;
        InfoPanel.Visibility = Visibility.Collapsed;
        CollageView.Visibility = Visibility.Visible;
        ModeLabel.Text = "Collage";

        // Reflect the default layout (from settings) in the in-collage picker.
        PresetJustified.IsChecked = _collagePreset == CollagePreset.Justified;
        PresetGrid.IsChecked = _collagePreset == CollagePreset.Grid;
        PresetHero.IsChecked = _collagePreset == CollagePreset.Hero;

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

        var tiles = CollageLayout.Compute(_collageItems, CollageCanvas.ActualWidth, CollageCanvas.ActualHeight, 6, _collagePreset);
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

    private void CollageBack_Click(object sender, RoutedEventArgs e) => ShowExplorer();
    private async void CollageShuffle_Click(object sender, RoutedEventArgs e) => await RebuildCollageAsync(reshuffle: true);

    private void CollagePreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioMenuFlyoutItem item && Enum.TryParse<CollagePreset>(item.Tag as string, out var preset))
        {
            _collagePreset = preset;
            LayoutCollage();
        }
    }

    /// <summary>Adds dropped image files to the open collage and re-lays it out.</summary>
    private async System.Threading.Tasks.Task AddToCollageAsync(List<string> paths)
    {
        var existing = new HashSet<string>(_collageSource.Select(p => p.Path), StringComparer.OrdinalIgnoreCase);
        var added = paths
            .Where(p => !existing.Contains(p))
            .Select(p => new PhotoItem(p)
            {
                IsFavorite = _state.FavoritePaths.Contains(p),
                IsHidden = _state.HiddenPaths.Contains(p)
            })
            .ToList();
        if (added.Count == 0) return;

        _collageSource.AddRange(added);
        _collageItems.AddRange(added);
        _collageCount = _collageItems.Count;
        CollageCountText.Text = $"{_collageItems.Count} photo{(_collageItems.Count == 1 ? "" : "s")}";

        await System.Threading.Tasks.Task.WhenAll(added.Select(i => i.EnsureAspectAsync()));
        LayoutCollage();
        StatusText.Text = $"Added {added.Count} photo(s) to the collage";
    }

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

    // ===================== File Explorer =====================

    private void PopulateSidebar()
    {
        var quick = _fs.GetQuickAccess();
        var drives = _fs.GetDrives();
        QuickAccessList.ItemsSource = quick;
        DrivesList.ItemsSource = drives;
        foreach (var i in quick.Concat(drives)) _ = i.LoadIconAsync(32);
        ExplorerIconsView.Loaded += (_, _) => ApplyIconSize();

        // Ctrl + mouse wheel resizes the thumbnails (handledEventsToo so it fires even though
        // the list scrolls the wheel internally).
        ExplorerIconsView.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(Explorer_PointerWheelChanged), true);
        ExplorerDetailsList.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(Explorer_PointerWheelChanged), true);
    }

    private void Explorer_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control)) return;
        var delta = e.GetCurrentPoint((UIElement)sender).Properties.MouseWheelDelta;
        if (delta == 0) return;

        if (_explorerViewMode == "Details") { _explorerViewMode = "Large"; ApplyViewMode(); }
        _iconSize = Math.Clamp(_iconSize + (delta > 0 ? 16 : -16), 48, 240);
        IconSizeSlider.Value = _iconSize;
        ApplyIconSize();
        e.Handled = true;
    }

    private void NavHome_Click(object sender, RoutedEventArgs e) { ShowExplorer(); NavigateTo(null); }

    private void Sidebar_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ExplorerItem item) { ShowExplorer(); NavigateTo(item.Path); }
    }

    private void NavigateTo(string? path, bool addHistory = true)
    {
        _currentFolder = path;
        if (addHistory)
        {
            if (_navIndex < _navHistory.Count - 1)
                _navHistory.RemoveRange(_navIndex + 1, _navHistory.Count - _navIndex - 1);
            _navHistory.Add(path);
            _navIndex = _navHistory.Count - 1;
        }
        LoadCurrentFolder();
        UpdateNavButtons();
        BuildBreadcrumb();
    }

    private void LoadCurrentFolder()
    {
        HiddenFolderPlaceholder.Visibility = Visibility.Collapsed;
        ExplorerEmpty.Visibility = Visibility.Collapsed;

        if (_currentFolder is null)
        {
            _explorerRaw = _fs.GetQuickAccess().Concat(_fs.GetDrives()).ToList();
            ApplySortAndGroup();
            ApplyViewMode();
            UpdateHideFolderButton();
            StatusText.Text = "This PC";
            return;
        }

        // App-hidden folder: present it as an ordinary empty folder — never reveal that it's hidden.
        if (_state.HiddenFolders.Contains(_currentFolder) && !_showAppHidden)
        {
            _explorerRaw = new List<ExplorerItem>();
            ApplySortAndGroup();
            ApplyViewMode();
            ExplorerEmpty.Visibility = Visibility.Visible;
            UpdateHideFolderButton();
            StatusText.Text = "0 item(s)";
            return;
        }

        _explorerRaw = _fs.List(_currentFolder, showWindowsHidden: false, _showAppHidden);
        ApplySortAndGroup();
        ApplyViewMode();
        ExplorerEmpty.Visibility = _explorerRaw.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateHideFolderButton();
        StatusText.Text = $"{_explorerRaw.Count} item(s)";
    }

    // ---- Sort & group ----

    private void ApplySortAndGroup()
    {
        var sorted = SortItems(_explorerRaw);

        // Keep the flat collection current (used by image/collage/slideshow code).
        _explorerItems.Clear();
        foreach (var it in sorted) _explorerItems.Add(it);

        if (_state.GroupBy == "None")
        {
            ExplorerIconsView.ItemsSource = _explorerItems;
            ExplorerDetailsList.ItemsSource = _explorerItems;
            return;
        }

        var groups = BuildGroups(sorted);
        ExplorerIconsView.ItemsSource = new CollectionViewSource { IsSourceGrouped = true, Source = groups }.View;
        ExplorerDetailsList.ItemsSource = new CollectionViewSource { IsSourceGrouped = true, Source = groups }.View;
    }

    private List<ExplorerItem> SortItems(List<ExplorerItem> items)
    {
        var dir = _state.SortDescending ? -1 : 1;
        int ByName(ExplorerItem a, ExplorerItem b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        int Primary(ExplorerItem a, ExplorerItem b) => _state.SortBy switch
        {
            "Date" => a.Modified.CompareTo(b.Modified),
            "Type" => string.Compare(a.TypeName, b.TypeName, StringComparison.OrdinalIgnoreCase) is var c && c != 0 ? c : ByName(a, b),
            "Size" => a.Size.CompareTo(b.Size),
            _ => ByName(a, b)
        };

        var sorted = new List<ExplorerItem>(items);
        sorted.Sort((a, b) =>
        {
            if (a.IsFolder != b.IsFolder) return a.IsFolder ? -1 : 1; // folders first, always
            var c = Primary(a, b) * dir;
            return c != 0 ? c : ByName(a, b) * dir;
        });
        return sorted;
    }

    private List<ExplorerGroup> BuildGroups(List<ExplorerItem> sorted)
    {
        var map = new Dictionary<string, ExplorerGroup>(StringComparer.OrdinalIgnoreCase);
        var groups = new List<ExplorerGroup>();
        foreach (var it in sorted)
        {
            var (key, rank) = GroupKeyRank(it);
            if (!map.TryGetValue(key, out var g))
            {
                g = new ExplorerGroup { Key = key, Rank = rank };
                map[key] = g;
                groups.Add(g);
            }
            g.Add(it);
        }
        groups.Sort((a, b) => a.Rank.CompareTo(b.Rank) is var c && c != 0 ? c
            : string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase));
        return groups;
    }

    private (string Key, double Rank) GroupKeyRank(ExplorerItem it)
    {
        switch (_state.GroupBy)
        {
            case "Type":
                return it.IsFolder ? ("File folder", -1) : (it.TypeName, 1);

            case "Size":
                if (it.IsFolder) return ("—", -1);
                var s = it.Size;
                if (s == 0) return ("Empty", 0);
                if (s < 16 * 1024) return ("Tiny (0–16 KB)", 1);
                if (s < 1024 * 1024) return ("Small (16 KB–1 MB)", 2);
                if (s < 128L * 1024 * 1024) return ("Medium (1–128 MB)", 3);
                if (s < 1024L * 1024 * 1024) return ("Large (128 MB–1 GB)", 4);
                return ("Huge (> 1 GB)", 5);

            case "Date":
                var d = it.Modified;
                if (d == default) return ("Unknown", 100);
                var today = DateTime.Now.Date;
                var dd = d.Date;
                if (dd == today) return ("Today", 0);
                if (dd == today.AddDays(-1)) return ("Yesterday", 1);
                if (dd > today.AddDays(-7)) return ("Earlier this week", 2);
                if (dd > today.AddDays(-14)) return ("Last week", 3);
                if (d.Year == today.Year && d.Month == today.Month) return ("Earlier this month", 4);
                if (d.Year == today.Year) return ("Earlier this year", 5);
                return ("A long time ago", 6);

            default: // Name
                var ch = it.Name.Length > 0 ? char.ToUpperInvariant(it.Name[0]) : '#';
                if (ch is >= 'A' and <= 'Z') return (ch.ToString(), ch - 'A');
                if (ch is >= '0' and <= '9') return ("0–9", 26);
                return ("#", 27);
        }
    }

    private void Sort_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioMenuFlyoutItem item) { _state.SortBy = item.Tag as string ?? "Name"; _state.Save(); ApplySortAndGroup(); }
    }

    private void SortDir_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioMenuFlyoutItem item) { _state.SortDescending = (item.Tag as string) == "Desc"; _state.Save(); ApplySortAndGroup(); }
    }

    private void Group_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioMenuFlyoutItem item) { _state.GroupBy = item.Tag as string ?? "None"; _state.Save(); ApplySortAndGroup(); }
    }

    private void SyncSortGroupRadios()
    {
        SortName.IsChecked = _state.SortBy == "Name";
        SortDate.IsChecked = _state.SortBy == "Date";
        SortType.IsChecked = _state.SortBy == "Type";
        SortSize.IsChecked = _state.SortBy == "Size";
        SortAsc.IsChecked = !_state.SortDescending;
        SortDesc.IsChecked = _state.SortDescending;
        GroupNone.IsChecked = _state.GroupBy == "None";
        GroupName.IsChecked = _state.GroupBy == "Name";
        GroupDate.IsChecked = _state.GroupBy == "Date";
        GroupType.IsChecked = _state.GroupBy == "Type";
        GroupSize.IsChecked = _state.GroupBy == "Size";
    }

    private void ApplyViewMode()
    {
        var details = _explorerViewMode == "Details";
        ExplorerIconsView.Visibility = details ? Visibility.Collapsed : Visibility.Visible;
        ExplorerDetailsView.Visibility = details ? Visibility.Visible : Visibility.Collapsed;
        if (!details) ApplyIconSize();
    }

    private void ApplyIconSize()
    {
        if (ExplorerIconsView.ItemsPanelRoot is ItemsWrapGrid wg)
        {
            wg.ItemWidth = _iconSize;
            wg.ItemHeight = _iconSize + 28;
        }
    }

    private void UpdateNavButtons()
    {
        BackNav.IsEnabled = _navIndex > 0;
        FwdNav.IsEnabled = _navIndex < _navHistory.Count - 1;
        UpNav.IsEnabled = _currentFolder is not null;
    }

    private void BuildBreadcrumb()
    {
        Breadcrumb.Children.Clear();
        AddCrumb("This PC", null);
        if (_currentFolder is not null)
        {
            var chain = new List<(string Name, string Path)>();
            var di = new DirectoryInfo(_currentFolder);
            while (di is not null) { chain.Insert(0, (string.IsNullOrEmpty(di.Name) ? di.FullName : di.Name, di.FullName)); di = di.Parent; }
            foreach (var (name, path) in chain)
            {
                Breadcrumb.Children.Add(new TextBlock { Text = "›", Opacity = 0.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 2, 0) });
                AddCrumb(name, path);
            }
        }

        // Keep the current (right-most) folder visible when the path is long.
        BreadcrumbScroller.UpdateLayout();
        BreadcrumbScroller.ChangeView(BreadcrumbScroller.ScrollableWidth, null, null, true);
    }

    private void PathBar_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => EditPath_Click(sender, null!);

    private void AddCrumb(string text, string? path)
    {
        var btn = new Button
        {
            Content = text,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 4, 6, 4),
            FontSize = 13
        };
        btn.Click += (_, _) => NavigateTo(path);
        Breadcrumb.Children.Add(btn);
    }

    private void NavBack_Click(object sender, RoutedEventArgs e)
    {
        if (_navIndex > 0) { _navIndex--; NavigateTo(_navHistory[_navIndex], addHistory: false); }
    }

    private void NavForward_Click(object sender, RoutedEventArgs e)
    {
        if (_navIndex < _navHistory.Count - 1) { _navIndex++; NavigateTo(_navHistory[_navIndex], addHistory: false); }
    }

    private void NavUp_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFolder is null) return;
        NavigateTo(Directory.GetParent(_currentFolder)?.FullName);
    }

    private void EditPath_Click(object sender, RoutedEventArgs e)
    {
        AddressBox.Text = _currentFolder ?? "";
        BreadcrumbScroller.Visibility = Visibility.Collapsed;
        AddressBox.Visibility = Visibility.Visible;
        AddressBox.Focus(FocusState.Programmatic);
        AddressBox.SelectAll();
    }

    private void AddressBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            var path = AddressBox.Text.Trim();
            EndEditPath();
            if (Directory.Exists(path)) NavigateTo(path);
            else if (File.Exists(path) && PhotoLibrary.IsSupported(path))
            {
                NavigateTo(Directory.GetParent(path)?.FullName);
                var m = _explorerItems.FirstOrDefault(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));
                if (m is not null) OpenImageFromExplorer(m);
            }
            else StatusText.Text = "Path not found.";
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape) { EndEditPath(); e.Handled = true; }
    }

    private void EndEditPath()
    {
        AddressBox.Visibility = Visibility.Collapsed;
        BreadcrumbScroller.Visibility = Visibility.Visible;
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => LoadCurrentFolder();

    private async void ExplorerIcons_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue) return;
        if (args.Item is ExplorerItem it && it.Icon is null)
            await it.LoadIconAsync((uint)Math.Clamp(_iconSize, 48, 256));
    }

    private void ExplorerItem_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not ExplorerItem item) return;
        OpenExplorerItem(item);
    }

    /// <summary>Lets the user drag files/folders out to Explorer, terminals, chat apps, etc.</summary>
    private void Explorer_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        var picked = e.Items.OfType<ExplorerItem>().Select(i => (i.Path, i.IsFolder)).ToList();
        if (picked.Count == 0) { e.Cancel = true; return; }

        e.Data.RequestedOperation = DataPackageOperation.Copy;
        // Path text fallback (terminals/editors that accept text).
        e.Data.SetText(string.Join(" ", picked.Select(p => $"\"{p.Path}\"")));

        // Real file drop (CF_HDROP) provided on demand so we can resolve StorageItems async.
        e.Data.SetDataProvider(StandardDataFormats.StorageItems, async request =>
        {
            var deferral = request.GetDeferral();
            try
            {
                var items = new List<IStorageItem>();
                foreach (var (path, isFolder) in picked)
                {
                    try
                    {
                        items.Add(isFolder
                            ? await StorageFolder.GetFolderFromPathAsync(path)
                            : await StorageFile.GetFileFromPathAsync(path));
                    }
                    catch { /* skip unreadable */ }
                }
                request.SetData(items);
            }
            finally { deferral.Complete(); }
        });
    }

    private void OpenExplorerItem(ExplorerItem item)
    {
        if (item.IsFolder) NavigateTo(item.Path);
        else if (item.IsImage) OpenImageFromExplorer(item);
        else if (PhotoLibrary.IsVideo(item.Path)) OpenVideoFromExplorer(item);
        else
        {
            try
            {
                ShellOps.AllowForeground(); // let the opened app come to the front, not stay behind us
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = item.Path, UseShellExecute = true });
            }
            catch (Exception ex) { StatusText.Text = ex.Message; App.Log("OpenDefault", ex); }
        }
    }

    private void ExplorerItem_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_state.SingleClickToOpen) return; // single-click already opened it
        if ((e.OriginalSource as FrameworkElement)?.DataContext is ExplorerItem item)
            OpenExplorerItem(item);
    }

    // ---- Embedded video player ----

    private bool _videoMuted;
    private bool _videoRepeat;

    private async void OpenVideoFromExplorer(ExplorerItem item)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(item.Path);
            ShowViewer();
            EnterVideoMode();
            VideoPlayer.Source = MediaSource.CreateFromStorageFile(file);
            var mp = VideoPlayer.MediaPlayer;
            if (mp is not null)
            {
                mp.IsMuted = _videoMuted;
                mp.IsLoopingEnabled = _videoRepeat;
                mp.Play();
            }
            UpdateVideoToggleIcons();
            ModeLabel.Text = item.Name;
        }
        catch (Exception ex) { StatusText.Text = $"Couldn't play video: {ex.Message}"; App.Log("OpenVideo", ex); }
    }

    private void VideoMute_Click(object sender, RoutedEventArgs e)
    {
        _videoMuted = !_videoMuted;
        if (VideoPlayer.MediaPlayer is not null) VideoPlayer.MediaPlayer.IsMuted = _videoMuted;
        UpdateVideoToggleIcons();
    }

    private void VideoRepeat_Click(object sender, RoutedEventArgs e)
    {
        _videoRepeat = !_videoRepeat;
        if (VideoPlayer.MediaPlayer is not null) VideoPlayer.MediaPlayer.IsLoopingEnabled = _videoRepeat;
        UpdateVideoToggleIcons();
    }

    private void UpdateVideoToggleIcons()
    {
        VideoMuteIcon.Glyph = _videoMuted ? "" : ""; // Mute / Volume
        VideoRepeatIcon.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            _videoRepeat ? Microsoft.UI.Colors.Gold : Microsoft.UI.Colors.White);
    }

    private void EnterVideoMode()
    {
        ImageHost.Visibility = Visibility.Collapsed;
        ObscureOverlay.Visibility = Visibility.Collapsed;
        ViewerChrome.Visibility = Visibility.Collapsed;
        InfoPanel.Visibility = Visibility.Collapsed;
        VideoPlayer.Visibility = Visibility.Visible;
        VideoBackBar.Visibility = Visibility.Visible;
        VideoControlsBar.Visibility = Visibility.Visible;
    }

    private void EnterImageMode()
    {
        StopVideo();
        VideoPlayer.Visibility = Visibility.Collapsed;
        VideoBackBar.Visibility = Visibility.Collapsed;
        VideoControlsBar.Visibility = Visibility.Collapsed;
        ImageHost.Visibility = Visibility.Visible;
        ViewerChrome.Visibility = Visibility.Visible;
    }

    private void StopVideo()
    {
        try
        {
            VideoPlayer.MediaPlayer?.Pause();
            VideoPlayer.Source = null;
        }
        catch { /* ignore */ }
    }

    private void OpenImageFromExplorer(ExplorerItem item)
    {
        PopulatePhotoPipelineFromCurrent();
        var idx = _view.ToList().FindIndex(p => string.Equals(p.Path, item.Path, StringComparison.OrdinalIgnoreCase));
        _currentIndex = Math.Max(0, idx);
        ShowViewer();
        _ = LoadCurrentAsync();
    }

    private void PopulatePhotoPipelineFromCurrent()
    {
        var paths = _explorerItems.Where(i => i.IsImage).Select(i => i.Path).ToList();
        _allPhotos.Clear();
        _allPhotos.AddRange(_library.LoadFiles(paths));
        _showHiddenAlbum = false;
        _favoritesOnly = false;
        RefreshView();
    }

    // ---- View controls ----

    private void ViewMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioMenuFlyoutItem item) return;
        _explorerViewMode = item.Tag as string ?? "Large";
        if (_explorerViewMode != "Details")
        {
            _iconSize = _explorerViewMode switch { "Large" => 160, "Medium" => 110, _ => 72 };
            IconSizeSlider.Value = _iconSize;
        }
        ApplyViewMode();
    }

    private void IconSize_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_fs is null) return;
        _iconSize = e.NewValue;
        if (_explorerViewMode == "Details") { _explorerViewMode = "Large"; ApplyViewMode(); }
        ApplyIconSize();
        _state.IconSize = _iconSize;
        _state.Save();
    }

    private void ShowAppHidden_Click(object sender, RoutedEventArgs e)
    {
        _showAppHidden = ShowHiddenToggle.IsChecked == true;
        LoadCurrentFolder();
    }

    // ---- Hide folder feature ----

    private void HideFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFolder is null) return;
        ToggleFolderHidden(_currentFolder);
        LoadCurrentFolder();
    }

    private void ToggleFolderHidden(string folderPath)
    {
        if (_state.HiddenFolders.Contains(folderPath)) _state.HiddenFolders.Remove(folderPath);
        else _state.HiddenFolders.Add(folderPath);
        _state.Save();
    }

    private void UnhideCurrent_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFolder is null) return;
        _state.HiddenFolders.Remove(_currentFolder);
        _state.Save();
        LoadCurrentFolder();
    }

    private void UpdateHideFolderButton()
    {
        var canHide = _currentFolder is not null;
        HideFolderBtn.IsEnabled = canHide;
        var hidden = canHide && _state.HiddenFolders.Contains(_currentFolder!);
        HideFolderText.Text = hidden ? "Unhide folder" : "Hide folder";
        HideFolderIcon.Glyph = hidden ? GlyphEyeOpen : GlyphEyeOff;
    }

    // ---- File operations ----

    private async void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFolder is null) { StatusText.Text = "Pick a folder first."; return; }
        try
        {
            var name = "New folder";
            var n = 2;
            while (Directory.Exists(Path.Combine(_currentFolder, name))) name = $"New folder ({n++})";
            var full = Path.Combine(_currentFolder, name);
            Directory.CreateDirectory(full);
            LoadCurrentFolder();

            // Immediately prompt to name it, like Explorer's inline rename.
            var item = _explorerItems.FirstOrDefault(i => string.Equals(i.Path, full, StringComparison.OrdinalIgnoreCase));
            if (item is not null) await RenameExplorerAsync(item);
        }
        catch (Exception ex) { StatusText.Text = $"Couldn't create folder: {ex.Message}"; }
    }

    private void ExplorerSlideshow_Click(object sender, RoutedEventArgs e)
    {
        PopulatePhotoPipelineFromCurrent();
        StartSlideshow();
    }

    private void ExplorerCollage_Click(object sender, RoutedEventArgs e)
    {
        PopulatePhotoPipelineFromCurrent();
        if (_view.Count == 0) { StatusText.Text = "No images in this folder."; return; }
        Collage_Click(this, new RoutedEventArgs());
    }

    // ---- Explorer context menu ----

    private void ExplorerView_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var item = (e.OriginalSource as FrameworkElement)?.DataContext as ExplorerItem;
        _explorerContextItem = item;
        var target = (FrameworkElement)sender;
        ShowExplorerMenu(item, target, e.GetPosition(target));
        e.Handled = true;
    }

    private void ShowExplorerMenu(ExplorerItem? item, FrameworkElement target, Windows.Foundation.Point position)
    {
        var menu = new MenuFlyout();
        MenuFlyoutItem SMI(string text, Symbol? sym, RoutedEventHandler click)
        {
            var i = new MenuFlyoutItem { Text = text };
            if (sym.HasValue) i.Icon = new SymbolIcon(sym.Value);
            i.Click += click;
            return i;
        }

        if (item is not null)
        {
            menu.Items.Add(SMI(item.IsFolder ? "Open" : "Open", Symbol.OpenFile, (_, _) => OpenExplorerItem(item)));
            if (!item.IsFolder)
                menu.Items.Add(SMI("Open with…", null, (_, _) => OpenWithItem2(item.Path)));
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(SMI("Copy", Symbol.Copy, async (_, _) => await CopyFileToClipboardAsync(item.Path)));
            menu.Items.Add(SMI("Copy path", Symbol.Link, (_, _) => CopyTextToClipboard(item.Path)));
            menu.Items.Add(new MenuFlyoutSeparator());
            if (item.IsFolder)
            {
                var hidden = _state.HiddenFolders.Contains(item.Path);
                menu.Items.Add(SMI(hidden ? "Unhide folder" : "Hide folder", null, (_, _) => { ToggleFolderHidden(item.Path); LoadCurrentFolder(); }));
            }
            menu.Items.Add(SMI("Rename…", Symbol.Rename, async (_, _) => await RenameExplorerAsync(item)));
            menu.Items.Add(SMI("Delete", Symbol.Delete, async (_, _) => await DeleteExplorerAsync(item)));
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(SMI("Properties", null, (_, _) => { var h = WinRT.Interop.WindowNative.GetWindowHandle(this); ShellOps.ShowProperties(h, item.Path); }));
        }
        else
        {
            menu.Items.Add(SMI("New folder", Symbol.NewFolder, NewFolder_Click));
            menu.Items.Add(SMI("Paste", Symbol.Paste, async (_, _) => await PasteIntoCurrentAsync()));
            menu.Items.Add(SMI("Refresh", Symbol.Refresh, (_, _) => LoadCurrentFolder()));
        }

        menu.ShowAt(target, new FlyoutShowOptions { Position = position });
    }

    private void OpenWithItem2(string path)
    {
        try { ShellOps.OpenWith(path); }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }

    private async System.Threading.Tasks.Task CopyFileToClipboardAsync(string path)
    {
        try
        {
            IStorageItem item = Directory.Exists(path)
                ? await StorageFolder.GetFolderFromPathAsync(path)
                : await StorageFile.GetFileFromPathAsync(path);
            var data = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            data.SetStorageItems(new[] { item });
            Clipboard.SetContent(data);
            StatusText.Text = "Copied to clipboard";
        }
        catch (Exception ex) { StatusText.Text = $"Copy failed: {ex.Message}"; }
    }

    private void CopyTextToClipboard(string text)
    {
        var data = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        data.SetText(text);
        Clipboard.SetContent(data);
        StatusText.Text = "Path copied";
    }

    private async System.Threading.Tasks.Task PasteIntoCurrentAsync()
    {
        if (_currentFolder is null) { StatusText.Text = "Pick a folder first."; return; }
        try
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.StorageItems)) { StatusText.Text = "Nothing to paste."; return; }
            var dest = await StorageFolder.GetFolderFromPathAsync(_currentFolder);
            var items = await content.GetStorageItemsAsync();
            var count = 0;
            foreach (var i in items)
            {
                if (i is StorageFile f) { await f.CopyAsync(dest, f.Name, NameCollisionOption.GenerateUniqueName); count++; }
            }
            LoadCurrentFolder();
            StatusText.Text = $"Pasted {count} file(s)";
        }
        catch (Exception ex) { StatusText.Text = $"Paste failed: {ex.Message}"; }
    }

    private async System.Threading.Tasks.Task RenameExplorerAsync(ExplorerItem item)
    {
        var box = new TextBox { Text = item.Name };
        box.Loaded += (_, _) => box.SelectAll();
        var dialog = new ContentDialog
        {
            Title = "Rename",
            Content = box,
            PrimaryButtonText = "Rename",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var newName = box.Text.Trim();
        if (string.IsNullOrEmpty(newName) || newName == item.Name) return;
        try
        {
            if (item.IsFolder)
            {
                var folder = await StorageFolder.GetFolderFromPathAsync(item.Path);
                await folder.RenameAsync(newName, NameCollisionOption.FailIfExists);
            }
            else
            {
                var file = await StorageFile.GetFileFromPathAsync(item.Path);
                await file.RenameAsync(newName, NameCollisionOption.FailIfExists);
            }
            LoadCurrentFolder();
        }
        catch (Exception ex) { StatusText.Text = $"Rename failed: {ex.Message}"; }
    }

    /// <summary>True while Shift is held — used to bypass the Recycle Bin (permanent delete).</summary>
    private static bool IsShiftDown()
    {
        var state = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
        return (state & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
    }

    private async System.Threading.Tasks.Task DeleteExplorerAsync(ExplorerItem item)
    {
        var permanent = IsShiftDown();
        var dialog = new ContentDialog
        {
            Title = permanent ? "Permanently delete" : "Delete",
            Content = permanent
                ? $"Permanently delete \"{item.Name}\"? This can't be undone."
                : $"Move \"{item.Name}\" to the Recycle Bin?",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = RootGrid.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var option = permanent ? StorageDeleteOption.PermanentDelete : StorageDeleteOption.Default;
        try
        {
            if (item.IsFolder)
                await (await StorageFolder.GetFolderFromPathAsync(item.Path)).DeleteAsync(option);
            else
                await (await StorageFile.GetFileFromPathAsync(item.Path)).DeleteAsync(option);
            LoadCurrentFolder();
        }
        catch (Exception ex) { StatusText.Text = $"Delete failed: {ex.Message}"; }
    }

    /// <summary>Deletes the explorer selection (Del key). Shift = permanent.</summary>
    private async System.Threading.Tasks.Task DeleteSelectedExplorerAsync()
    {
        var active = ExplorerIconsView.Visibility == Visibility.Visible
            ? (ListViewBase)ExplorerIconsView : ExplorerDetailsList;
        var selection = active.SelectedItems.OfType<ExplorerItem>().ToList();
        if (selection.Count == 0) return;

        var permanent = IsShiftDown();
        var dialog = new ContentDialog
        {
            Title = permanent ? "Permanently delete" : "Delete",
            Content = permanent
                ? $"Permanently delete {selection.Count} item(s)? This can't be undone."
                : $"Move {selection.Count} item(s) to the Recycle Bin?",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = RootGrid.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var option = permanent ? StorageDeleteOption.PermanentDelete : StorageDeleteOption.Default;
        foreach (var item in selection)
        {
            try
            {
                if (item.IsFolder)
                    await (await StorageFolder.GetFolderFromPathAsync(item.Path)).DeleteAsync(option);
                else
                    await (await StorageFile.GetFileFromPathAsync(item.Path)).DeleteAsync(option);
            }
            catch (Exception ex) { StatusText.Text = $"Delete failed: {ex.Message}"; }
        }
        LoadCurrentFolder();
    }

    // ===================== Right-click context menu =====================

    private PhotoItem? _contextItem;

    private void ImageHost_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (Current is null) return;
        _contextItem = Current;
        ShowImageMenu(ImageHost, e.GetPosition(ImageHost));
        e.Handled = true;
    }

    private void PhotoGrid_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not PhotoItem item) return;
        _contextItem = item;
        ShowImageMenu(PhotoGrid, e.GetPosition(PhotoGrid));
        e.Handled = true;
    }

    private void ShowImageMenu(FrameworkElement target, Windows.Foundation.Point position)
    {
        var item = _contextItem;
        if (item is null) return;

        var seg = new FontFamily("Segoe MDL2 Assets");
        MenuFlyoutItem MI(string text, string glyph, RoutedEventHandler click)
        {
            var i = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph, FontFamily = seg } };
            i.Click += click;
            return i;
        }

        var menu = new MenuFlyout();
        menu.Items.Add(MI("Copy", "", async (_, _) => await CopyImageAsync(item)));
        menu.Items.Add(MI("Copy as file", "", async (_, _) => await CopyFileAsync(item)));
        menu.Items.Add(MI("Copy file path", "", (_, _) => CopyPath(item)));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(MI("Open with…", "", (_, _) => OpenWithItem(item)));
        menu.Items.Add(MI("Print…", "", (_, _) => RunVerb(item, "print")));
        menu.Items.Add(MI("Set as desktop background", "", (_, _) => SetWallpaper(item)));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(MI(item.IsFavorite ? "" : "", item.IsFavorite ? "" : "", (_, _) => FavoriteItem(item)));
        if (!item.IsHidden)
            menu.Items.Add(MI("Hide (Hidden album)", "", (_, _) => HideItemPermanently(item)));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(MI("Rename…", "", async (_, _) => await RenameItemAsync(item)));
        menu.Items.Add(MI("Show in Explorer", "", (_, _) => RevealItem(item)));
        menu.Items.Add(MI("Delete", "", async (_, _) => await DeleteItemAsync(item)));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(MI("Properties", "", (_, _) => ShowProperties(item)));

        menu.ShowAt(target, new FlyoutShowOptions { Position = position });
    }

    // ---- Core operations (shared by toolbar buttons and the context menu) ----

    private async System.Threading.Tasks.Task CopyImageAsync(PhotoItem item)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(item.Path);
            var data = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            data.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));
            Clipboard.SetContent(data);
            StatusText.Text = "Image copied to clipboard";
        }
        catch (Exception ex) { StatusText.Text = $"Copy failed: {ex.Message}"; App.Log("CopyImage", ex); }
    }

    private async System.Threading.Tasks.Task CopyFileAsync(PhotoItem item)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(item.Path);
            var data = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            data.SetStorageItems(new IStorageItem[] { file });
            Clipboard.SetContent(data);
            StatusText.Text = "File copied to clipboard";
        }
        catch (Exception ex) { StatusText.Text = $"Copy failed: {ex.Message}"; }
    }

    private void CopyPath(PhotoItem item)
    {
        var data = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        data.SetText(item.Path);
        Clipboard.SetContent(data);
        StatusText.Text = "Path copied";
    }

    private void RunVerb(PhotoItem item, string verb)
    {
        try { ShellOps.InvokeVerb(item.Path, verb); }
        catch (Exception ex) { StatusText.Text = ex.Message; App.Log("Verb:" + verb, ex); }
    }

    private void OpenWithItem(PhotoItem item)
    {
        try { ShellOps.OpenWith(item.Path); }
        catch (Exception ex) { StatusText.Text = $"Open with failed: {ex.Message}"; App.Log("OpenWith", ex); }
    }

    private void SetWallpaper(PhotoItem item)
    {
        StatusText.Text = ShellOps.SetWallpaper(item.Path)
            ? "Set as desktop background"
            : "Couldn't set the background for this image";
    }

    private void ShowProperties(PhotoItem item)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        ShellOps.ShowProperties(hwnd, item.Path);
    }

    private void FavoriteItem(PhotoItem item)
    {
        item.IsFavorite = !item.IsFavorite;
        if (item.IsFavorite) _state.FavoritePaths.Add(item.Path);
        else _state.FavoritePaths.Remove(item.Path);
        _state.Save();
        if (ReferenceEquals(item, Current)) UpdateFavoriteIcon();
        if (_favoritesOnly) RefreshView();
    }

    private void HideItemPermanently(PhotoItem item)
    {
        item.IsHidden = true;
        _state.HiddenPaths.Add(item.Path);
        _state.Save();
        _obscured.Remove(item.Path);
        StatusText.Text = $"{item.FileName} moved to Hidden album";

        if (_showHiddenAlbum) return;
        var wasCurrent = ReferenceEquals(item, Current);
        RefreshView();
        if (InViewer && wasCurrent)
        {
            if (_view.Count == 0) { ShowExplorer(); return; }
            _currentIndex = Math.Min(_currentIndex, _view.Count - 1);
            _ = LoadCurrentAsync();
        }
    }

    private void RevealItem(PhotoItem item)
    {
        try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{item.Path}\""); }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }

    private async System.Threading.Tasks.Task DeleteItemAsync(PhotoItem item)
    {
        var permanent = IsShiftDown();
        var dialog = new ContentDialog
        {
            Title = permanent ? "Permanently delete" : "Delete photo",
            Content = permanent
                ? $"Permanently delete \"{item.FileName}\"? This can't be undone."
                : $"Move \"{item.FileName}\" to the Recycle Bin?",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = RootGrid.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(item.Path);
            await file.DeleteAsync(permanent ? StorageDeleteOption.PermanentDelete : StorageDeleteOption.Default);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Delete failed: {ex.Message}";
            return;
        }

        _state.HiddenPaths.Remove(item.Path);
        _state.FavoritePaths.Remove(item.Path);
        _state.Save();
        var wasCurrent = ReferenceEquals(item, Current);
        _allPhotos.Remove(item);
        RefreshView();

        if (InViewer && wasCurrent)
        {
            if (_view.Count == 0) { ShowExplorer(); return; }
            _currentIndex = Math.Min(_currentIndex, _view.Count - 1);
            await LoadCurrentAsync();
        }
    }

    private async System.Threading.Tasks.Task RenameItemAsync(PhotoItem item)
    {
        var box = new TextBox { Text = item.FileName };
        box.Loaded += (_, _) => box.SelectAll();
        var dialog = new ContentDialog
        {
            Title = "Rename",
            Content = box,
            PrimaryButtonText = "Rename",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var newName = box.Text.Trim();
        if (string.IsNullOrEmpty(newName) || string.Equals(newName, item.FileName, StringComparison.Ordinal)) return;

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(item.Path);
            await file.RenameAsync(newName, NameCollisionOption.FailIfExists);
            StatusText.Text = $"Renamed to {newName}";
            var dir = System.IO.Path.GetDirectoryName(item.Path);
            if (dir is not null) await LoadFolderAsync(dir); // reload so paths refresh
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Rename failed: {ex.Message}";
        }
    }

    // ===================== Settings =====================

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        _loadingSettings = true;
        ThemeCombo.SelectedIndex = _state.Theme switch { "Light" => 1, "Dark" => 2, "Terminal" => 3, "Gray" => 4, _ => 0 };
        OpenModeCombo.SelectedIndex = _state.SingleClickToOpen ? 1 : 0;
        IconSizeCombo.SelectedIndex = _iconSize <= 85 ? 0 : _iconSize >= 140 ? 2 : 1;
        FolderPreviewSwitch.IsOn = _state.FolderPreviews;
        ShowExtensionsSwitch.IsOn = _state.ShowExtensions;
        CollageLayoutCombo.SelectedIndex = (int)_collagePreset;
        SlideshowSecondsSlider.Value = Math.Clamp(_state.SlideshowSeconds, 2, 30);
        SlideshowSecondsValue.Text = $"{_state.SlideshowSeconds}s";
        ShuffleSwitch.IsOn = _state.SlideshowShuffle;
        LoopSwitch.IsOn = _state.SlideshowLoop;
        TransitionCombo.SelectedIndex = (int)_state.SlideshowTransition;
        _loadingSettings = false;

        SettingsOverlay.Visibility = Visibility.Visible;
    }

    private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings) return;
        _state.Theme = ThemeCombo.SelectedIndex switch { 1 => "Light", 2 => "Dark", 3 => "Terminal", 4 => "Gray", _ => "System" };
        ApplyTheme();
        _state.Save();
    }

    private void OpenModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings) return;
        _state.SingleClickToOpen = OpenModeCombo.SelectedIndex == 1;
        ApplyClickMode();
        _state.Save();
    }

    private static readonly string[] CustomThemeKeys =
    {
        "TextFillColorPrimaryBrush", "TextFillColorSecondaryBrush", "TextFillColorTertiaryBrush",
        "LayerFillColorAltBrush", "LayerFillColorDefaultBrush",
        "CardBackgroundFillColorDefaultBrush", "CardBackgroundFillColorSecondaryBrush",
        "ControlFillColorDefaultBrush", "ControlFillColorSecondaryBrush",
        "AcrylicInAppFillColorDefaultBrush", "AcrylicBackgroundFillColorDefaultBrush",
        "SolidBackgroundFillColorBaseBrush", "CardStrokeColorDefaultBrush", "SubtleFillColorSecondaryBrush"
    };

    private void ApplyTheme()
    {
        var res = Application.Current.Resources;
        foreach (var k in CustomThemeKeys) res.Remove(k);

        switch (_state.Theme)
        {
            case "Light":
                SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
                RootGrid.Background = null;
                SetElementTheme(ElementTheme.Light);
                break;
            case "Dark":
                SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
                RootGrid.Background = null;
                SetElementTheme(ElementTheme.Dark);
                break;
            case "Terminal":
                ApplyCustomTheme(Rgb(255, 4, 10, 4), Rgb(255, 13, 24, 13), Rgb(255, 90, 255, 130), Rgb(130, 60, 210, 110));
                break;
            case "Gray":
                ApplyCustomTheme(Rgb(255, 46, 48, 50), Rgb(255, 64, 66, 68), Rgb(255, 230, 230, 232), Rgb(120, 150, 154, 158));
                break;
            default:
                SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
                RootGrid.Background = null;
                SetElementTheme(ElementTheme.Default);
                break;
        }

        // Caption buttons (min/max/close) need a matching foreground or they vanish on the backdrop.
        SetCaptionColors(_state.Theme switch
        {
            "Light" => Rgb(255, 30, 30, 30),
            "System" => (Windows.UI.Color?)null,   // let the system decide
            _ => Rgb(255, 235, 235, 235)           // Dark / Terminal / Gray
        });
    }

    private void SetCaptionColors(Windows.UI.Color? fg)
    {
        if (!AppWindowTitleBar.IsCustomizationSupported()) return;
        var tb = _appWindow.TitleBar;
        tb.ButtonForegroundColor = fg;
        tb.ButtonHoverForegroundColor = fg;
        tb.ButtonPressedForegroundColor = fg;
        tb.ButtonInactiveForegroundColor = fg.HasValue ? Rgb(160, fg.Value.R, fg.Value.G, fg.Value.B) : null;
        tb.ButtonHoverBackgroundColor = fg.HasValue ? Rgb(40, fg.Value.R, fg.Value.G, fg.Value.B) : null;
    }

    private void ApplyCustomTheme(Windows.UI.Color bg, Windows.UI.Color panel, Windows.UI.Color fg, Windows.UI.Color stroke)
    {
        SystemBackdrop = null;
        RootGrid.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(bg);

        var res = Application.Current.Resources;
        Microsoft.UI.Xaml.Media.SolidColorBrush B(Windows.UI.Color c) => new(c);
        res["TextFillColorPrimaryBrush"] = B(fg);
        res["TextFillColorSecondaryBrush"] = B(Rgb(200, fg.R, fg.G, fg.B));
        res["TextFillColorTertiaryBrush"] = B(Rgb(150, fg.R, fg.G, fg.B));
        res["LayerFillColorAltBrush"] = B(panel);
        res["LayerFillColorDefaultBrush"] = B(panel);
        res["CardBackgroundFillColorDefaultBrush"] = B(panel);
        res["CardBackgroundFillColorSecondaryBrush"] = B(panel);
        res["ControlFillColorDefaultBrush"] = B(panel);
        res["ControlFillColorSecondaryBrush"] = B(panel);
        res["AcrylicInAppFillColorDefaultBrush"] = B(panel);
        res["AcrylicBackgroundFillColorDefaultBrush"] = B(panel);
        res["SolidBackgroundFillColorBaseBrush"] = B(bg);
        res["CardStrokeColorDefaultBrush"] = B(stroke);
        res["SubtleFillColorSecondaryBrush"] = B(panel);

        SetElementTheme(ElementTheme.Dark);
    }

    private void SetElementTheme(ElementTheme theme)
    {
        // Toggle to force ThemeResource references to re-resolve against the current resources.
        RootGrid.RequestedTheme = ElementTheme.Light;
        RootGrid.RequestedTheme = ElementTheme.Dark;
        RootGrid.RequestedTheme = theme;
    }

    private static Windows.UI.Color Rgb(byte a, byte r, byte g, byte b) => new() { A = a, R = r, G = g, B = b };

    private void ApplyClickMode()
    {
        // Single-click → ItemClick opens; double-click (default) → items select, double-tap opens.
        ExplorerIconsView.IsItemClickEnabled = _state.SingleClickToOpen;
        ExplorerDetailsList.IsItemClickEnabled = _state.SingleClickToOpen;
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
        _state.Save();
    }

    private void ShuffleSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _state.SlideshowShuffle = ShuffleSwitch.IsOn;
        _state.Save();
    }

    private void LoopSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _state.SlideshowLoop = LoopSwitch.IsOn;
        _state.Save();
    }

    private void TransitionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings) return;
        _state.SlideshowTransition = (SlideshowTransition)Math.Max(0, TransitionCombo.SelectedIndex);
        _state.Save();
    }

    private void IconSizeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings) return;
        _iconSize = IconSizeCombo.SelectedIndex switch { 0 => 72, 2 => 160, _ => 110 };
        if (_explorerViewMode == "Details") { _explorerViewMode = "Large"; ApplyViewMode(); }
        IconSizeSlider.Value = _iconSize; // also updates _state.IconSize via the slider handler
        ApplyIconSize();
    }

    private void FolderPreviewSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _state.FolderPreviews = FolderPreviewSwitch.IsOn;
        ExplorerItem.ShowFolderPreviews = _state.FolderPreviews;
        _state.Save();
        LoadCurrentFolder(); // re-render icons with/without previews
    }

    private void ShowExtensionsSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _state.ShowExtensions = ShowExtensionsSwitch.IsOn;
        ExplorerItem.ShowExtensions = _state.ShowExtensions;
        _state.Save();
        LoadCurrentFolder(); // re-render names
    }

    private void CollageLayoutCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings) return;
        _collagePreset = (CollagePreset)Math.Max(0, CollageLayoutCombo.SelectedIndex);
        _state.CollagePreset = _collagePreset.ToString();
        _state.Save();
    }

    private static CollagePreset ParseCollagePreset(string? s) =>
        Enum.TryParse<CollagePreset>(s, out var p) ? p : CollagePreset.Justified;

    // ===================== Keyboard =====================

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.F5:
                if (ExplorerView.Visibility == Visibility.Visible) LoadCurrentFolder();
                else StartSlideshow();
                e.Handled = true; break;
            case VirtualKey.Delete when ExplorerView.Visibility == Visibility.Visible:
                _ = DeleteSelectedExplorerAsync(); e.Handled = true; break;
            case VirtualKey.Back when ExplorerView.Visibility == Visibility.Visible
                    && FocusManager.GetFocusedElement(RootGrid.XamlRoot) is not TextBox:
                NavBack_Click(sender, e); e.Handled = true; break;
            case VirtualKey.H when InViewer:
                ToggleObscure(); e.Handled = true; break;
            case VirtualKey.Left when InViewer:
                Navigate(-1); e.Handled = true; break;
            case VirtualKey.Right when InViewer:
                Navigate(+1); e.Handled = true; break;
            case VirtualKey.Escape when SettingsOverlay.Visibility == Visibility.Visible:
                CloseSettings(); e.Handled = true; break;
            case VirtualKey.Escape when InCollage:
                ShowExplorer(); e.Handled = true; break;
            case VirtualKey.Escape when InViewer:
                if (_isFullScreen) ToggleFullScreen(); else ShowExplorer();
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
