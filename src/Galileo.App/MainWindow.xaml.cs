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
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Galileo.Models;
using Galileo.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Media.Core;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.System;

namespace Galileo;

public sealed partial class MainWindow : Window
{
    // Segoe Fluent Icons glyphs for the eye toggle.
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
    private int _loadToken;           // bumped per LoadCurrentAsync; lets a stale decode bail out
    private double _rotation;
    private bool _isFullScreen;
    private bool _showHiddenAlbum;
    private bool _favoritesOnly;

    private readonly DispatcherTimer _chromeTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool _loadingSettings;
    private AppState? _settingsSnapshot;   // pre-edit copy, restored on Cancel

    // Spacebar Peek (Quick Look) state.
    private ExplorerItem? _peekItem;
    private int _peekToken;            // bumped per preview load so a fast nav cancels a stale decode

    // Rubber-band (marquee) selection state for the icon view.
    private bool _marqueeActive;
    private Windows.Foundation.Point _marqueeStart;

    // Open archives: maps a zip's extracted temp dir -> (zip path, display name) for breadcrumb labels.
    private readonly Dictionary<string, (string ZipPath, string Name)> _openZips = new(StringComparer.OrdinalIgnoreCase);

    // Live folder refresh: auto-show files added/removed/renamed outside the app.
    private FileSystemWatcher? _folderWatcher;
    private string? _watchedPath;
    private readonly DispatcherTimer _watchDebounce = new() { Interval = TimeSpan.FromMilliseconds(400) };

    // Developer-mode embedded terminal.
    private TerminalSession? _term;
    private bool _termWebReady;
    private short _termCols, _termRows;
    private readonly List<(string Label, string Exe)> _shells = new();

    // Secure vault state.
    private readonly VaultManager _vaults = new();
    private readonly GoogleDriveBackup _drive = new();
    private readonly ObservableCollection<Models.VaultInfo> _vaultList = new();
    private readonly DispatcherTimer _vaultIdleTimer = new();
    private bool _closingForVaultLock;  // guards the re-entrant AppWindow.Closing lock flow

    // Polls for mounted/removed drives so the sidebar and This PC view stay current
    // (WinUI 3 doesn't surface WM_DEVICECHANGE directly).
    private readonly DispatcherTimer _driveWatcher = new() { Interval = TimeSpan.FromSeconds(2) };
    private HashSet<string> _knownDrives = new(StringComparer.OrdinalIgnoreCase);

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

    // Search
    private string _searchQuery = "";
    private bool _searchRecursive;
    private List<ExplorerItem> _searchResults = new();
    private bool _suppressSearchEvent;

    // Tabs
    private bool _switchingTabs;

    // Privacy gate (unlocked once per session after a successful Hello check)
    private bool _helloUnlocked;

    // True between a gallery click and the viewer image opening, to run the connected animation.
    private bool _pendingConnectedAnim;

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
        ExplorerItem.FolderThumbnails = _state.FolderThumbnails;

        PopulateSidebar();
        PopulatePinned();

        // Secure vault: wipe any decrypted working folder left by a crash, list vaults, and arm the
        // idle auto-lock + app-exit lock.
        _vaults.WipeOrphanWorkDirs();
        ArchiveService.WipeOrphans(); // clear any leftover extracted-zip temp dirs from a prior run
        VaultsList.ItemsSource = _vaultList;
        RefreshVaults();
        _vaultIdleTimer.Tick += VaultIdle_Tick;
        _appWindow.Closing += AppWindow_Closing;

        // Stay signed in to Google Drive across launches (silent token refresh; no browser).
        if (GoogleDriveBackup.IsConfigured) _ = SilentReconnectDriveAsync();

        ApplyDeveloperMode(); // show/hide the Terminal button per the saved setting
        SetResizeCursor(TerminalSplitter); // ↔ cursor on the explorer/terminal divider
        SetResizeCursor(SidebarSplitter);  // ↔ cursor on the sidebar/file-pane divider
        SidebarCol.Width = new GridLength(Math.Clamp(_state.SidebarWidth is > 0 ? _state.SidebarWidth : 240, 160, 560));

        IconSizeSlider.Value = _iconSize;
        ApplyIconSize();
        ApplyTheme();
        ApplyClickMode();
        SyncSortGroupRadios();
        UpdateSortHeaders();

        // Watch for drives being mounted/removed and keep the UI in sync.
        _knownDrives = CurrentDriveSignature();
        _driveWatcher.Tick += DriveWatcher_Tick;
        _driveWatcher.Start();

        // Debounced reload when the current folder changes on disk (downloads, other apps, etc.).
        _watchDebounce.Tick += (_, _) =>
        {
            _watchDebounce.Stop();
            if (ExplorerView.Visibility == Visibility.Visible
                && string.IsNullOrEmpty(_searchQuery)
                && string.Equals(_currentFolder, _watchedPath, StringComparison.OrdinalIgnoreCase))
                RefreshFolderIncremental();
        };

        // Windows may launch us with a file (default app) or folder to open.
        if (!string.IsNullOrEmpty(initialPath) && System.IO.File.Exists(initialPath))
        {
            var dir = System.IO.Path.GetDirectoryName(initialPath);
            NewTab(dir);
            OpenPathInCurrentTab(initialPath);
        }
        else if (!string.IsNullOrEmpty(initialPath) && System.IO.Directory.Exists(initialPath))
        {
            NewTab(initialPath);
        }
        else
        {
            NewTab(null); // This PC / home
        }
    }

    /// <summary>Opens a file path that lives in the current folder (image → viewer, video → player, else default app).</summary>
    private void OpenPathInCurrentTab(string path)
    {
        var match = _explorerItems.FirstOrDefault(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));
        if (match is not null) OpenExplorerItem(match);
    }

    /// <summary>Opens a file/folder handed to us by another (redirected) instance, in a new tab.</summary>
    public void OpenExternalPath(string path)
    {
        try
        {
            if (System.IO.Directory.Exists(path)) NewTab(path);
            else if (System.IO.File.Exists(path))
            {
                NewTab(System.IO.Path.GetDirectoryName(path));
                OpenPathInCurrentTab(path);
            }
        }
        catch (Exception ex) { App.Log("OpenExternalPath", ex); }
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
            try { PhotoGrid.PrepareConnectedAnimation("toViewer", item, "PhotoThumb"); _pendingConnectedAnim = true; }
            catch { _pendingConnectedAnim = false; }
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

        // Generation token: if the user flips to the next photo while this one is still
        // decoding, the older (possibly slower) decode must not overwrite the newer image.
        var token = ++_loadToken;

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(item.Path);
            if (token != _loadToken) return;

            // Cap the decoded size on the longer side. Decoding at full resolution can exceed the
            // GPU's max texture size on large images (panoramas/huge screenshots) and crash the
            // render thread — a failure the try/catch here can't see. 8000px is well under the
            // ~16384 D3D limit and still sharp for screen + zoom.
            const int maxSide = 8000;
            var props = await file.Properties.GetImagePropertiesAsync();
            if (token != _loadToken) return;
            uint w = props.Width, h = props.Height;

            using var stream = await file.OpenReadAsync();
            if (token != _loadToken) return;
            var bmp = new BitmapImage { DecodePixelType = DecodePixelType.Logical };
            if (w > 0 && h > 0 && Math.Max(w, h) > maxSide)
            {
                if (w >= h) bmp.DecodePixelWidth = maxSide;
                else bmp.DecodePixelHeight = maxSide;
            }
            await bmp.SetSourceAsync(stream);
            if (token != _loadToken) return; // a newer photo won the race — drop this one
            ViewerImage.Source = bmp;
            _bmpW = bmp.PixelWidth;
            _bmpH = bmp.PixelHeight;
        }
        catch (Exception ex)
        {
            if (token != _loadToken) return;
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
        if (_pendingConnectedAnim)
        {
            _pendingConnectedAnim = false;
            try { ConnectedAnimationService.GetForCurrentView().GetAnimation("toViewer")?.TryStart(ViewerImage); }
            catch { /* animation is best-effort */ }
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

    private async void HiddenAlbum_Click(object sender, RoutedEventArgs e)
    {
        if (HiddenAlbumButton.IsChecked == true && !await EnsureHiddenUnlockedAsync())
        {
            HiddenAlbumButton.IsChecked = false;
            return;
        }
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

        _knownDrives = CurrentDriveSignature();

        // Ctrl + mouse wheel resizes the thumbnails (handledEventsToo so it fires even though
        // the list scrolls the wheel internally).
        ExplorerIconsView.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(Explorer_PointerWheelChanged), true);
        ExplorerDetailsList.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(Explorer_PointerWheelChanged), true);

        // Spacebar Peek: handledEventsToo so we still see Space/arrows after the list consumes them
        // for selection — lets Space open the preview and arrows drive it while it's open.
        RootGrid.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(Peek_KeyDown), true);

        // Any pointer/key activity resets the vault idle auto-lock countdown.
        RootGrid.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler((_, _) => ResetVaultIdle()), true);

        // Rubber-band selection on the icon view (handledEventsToo so it fires even though the
        // GridView handles pointer events for its own scrolling/selection).
        ExplorerIconsView.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(ExplorerIcons_PointerPressed), true);
        ExplorerIconsView.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(ExplorerIcons_PointerMoved), true);
        ExplorerIconsView.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(ExplorerIcons_PointerReleased), true);
        ExplorerIconsView.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(ExplorerIcons_PointerCaptureLost), true);
    }

    /// <summary>A cheap fingerprint of the current drives (letter + ready state) to detect changes.</summary>
    private static HashSet<string> CurrentDriveSignature()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var d in System.IO.DriveInfo.GetDrives())
            {
                bool ready;
                try { ready = d.IsReady; } catch { ready = false; }
                set.Add(d.Name + (ready ? "+" : "-"));
            }
        }
        catch { /* enumeration can momentarily fail while a device settles */ }
        return set;
    }

    /// <summary>Polls for drive arrival/removal and refreshes the sidebar + This PC view on change.</summary>
    private async void DriveWatcher_Tick(object? sender, object e)
    {
        HashSet<string> sig;
        List<ExplorerItem> drives;
        try
        {
            sig = await Task.Run(CurrentDriveSignature); // IsReady can block, so poll off the UI thread
            if (sig.SetEquals(_knownDrives)) return;     // nothing changed
            drives = await Task.Run(() => _fs.GetDrives());
        }
        catch { return; }

        // Marshal the UI updates explicitly onto the UI thread. Relying on the await-captured
        // context is unsafe here (a cross-thread ItemsSource assignment hard-crashes the XAML core).
        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                _knownDrives = sig;
                DrivesList.ItemsSource = drives;
                foreach (var i in drives) _ = i.LoadIconAsync(32);

                // If This PC is on screen (not searching), refresh it so the drive shows there too.
                if (_currentFolder is null && ExplorerView.Visibility == Visibility.Visible && string.IsNullOrEmpty(_searchQuery))
                    LoadCurrentFolder();

                StatusText.Text = "Drives updated";
            }
            catch (Exception ex) { App.Log("DriveWatcher", ex); }
        });
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

    // ---- Pinned sidebar locations (local folders, UNC shares, WSL paths) ----

    private void PopulatePinned()
    {
        var items = _state.PinnedPaths
            .Select(p => new ExplorerItem(p, ExplorerItemKind.Folder, 0, default, "Folder", FriendlyPinName(p)))
            .ToList();
        PinnedList.ItemsSource = items;
        foreach (var i in items) _ = i.LoadIconAsync(32);
    }

    private static string FriendlyPinName(string path)
    {
        var trimmed = path.TrimEnd('\\', '/');
        var name = System.IO.Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? trimmed : name;
    }

    private async void AddLocation_Click(object sender, RoutedEventArgs e)
    {
        var box = new TextBox { PlaceholderText = @"e.g. \\server\share  or  \\wsl.localhost\Ubuntu\home" };
        box.Loaded += (_, _) => box.Focus(FocusState.Programmatic);
        var note = new TextBlock
        {
            Text = "Pin a local folder, a network share, or a WSL path. Paste a full path.",
            Opacity = 0.6, FontSize = 12, TextWrapping = TextWrapping.Wrap,
        };
        var panel = new StackPanel { Spacing = 10, MinWidth = 380 };
        panel.Children.Add(note);
        panel.Children.Add(box);

        var dlg = new ContentDialog
        {
            Title = "Add location",
            Content = panel,
            PrimaryButtonText = "Add",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        var path = box.Text.Trim().Trim('"');
        if (!string.IsNullOrEmpty(path)) AddPinnedPath(path);
    }

    private void AddPinnedPath(string path)
    {
        path = path.TrimEnd();
        if (_state.PinnedPaths.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)))
        {
            StatusText.Text = "That location is already pinned.";
        }
        else
        {
            _state.PinnedPaths.Add(path);
            _state.Save();
            PopulatePinned();
            StatusText.Text = $"Pinned {FriendlyPinName(path)}";
        }
        // Jump to it if it's reachable right now (network/WSL may be offline — still pinned).
        if (Directory.Exists(path)) { ShowExplorer(); NavigateTo(path); }
    }

    private void PinnedList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not ExplorerItem item) return;
        var menu = new MenuFlyout();
        var remove = new MenuFlyoutItem { Text = "Remove from sidebar", Icon = new SymbolIcon(Symbol.UnPin) };
        remove.Click += (_, _) => RemovePinnedPath(item.Path);
        menu.Items.Add(remove);
        var target = (FrameworkElement)sender;
        menu.ShowAt(target, new FlyoutShowOptions { Position = e.GetPosition(target) });
        e.Handled = true;
    }

    private void RemovePinnedPath(string path)
    {
        _state.PinnedPaths.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        _state.Save();
        PopulatePinned();
        StatusText.Text = "Removed from sidebar.";
    }

    private void NavigateTo(string? path, bool addHistory = true)
    {
        ClearSearch();
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
        SyncActiveTab();
    }

    private void LoadCurrentFolder()
    {
        HiddenFolderPlaceholder.Visibility = Visibility.Collapsed;
        ExplorerEmpty.Visibility = Visibility.Collapsed;
        UpdateFolderWatch();

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
            UpdateHideFolderButton();
            StatusText.Text = "0 item(s)";
            return;
        }

        _explorerRaw = _fs.List(_currentFolder, showWindowsHidden: false, _showAppHidden);
        ApplySortAndGroup();
        ApplyViewMode();
        UpdateHideFolderButton();
        StatusText.Text = $"{_explorerRaw.Count} item(s)";
    }

    // ---- Live folder refresh ----

    /// <summary>Watches the current real folder for outside changes; debounced reload keeps the
    /// listing (and sort order) current as files are downloaded/added/removed.</summary>
    private void UpdateFolderWatch()
    {
        var path = _currentFolder is not null
                   && string.IsNullOrEmpty(_searchQuery)
                   && !(_state.HiddenFolders.Contains(_currentFolder) && !_showAppHidden)
                   && Directory.Exists(_currentFolder)
            ? _currentFolder
            : null;

        if (string.Equals(path, _watchedPath, StringComparison.OrdinalIgnoreCase) && _folderWatcher is not null)
            return; // already watching it

        StopFolderWatch();
        _watchedPath = path;
        if (path is null) return;

        try
        {
            _folderWatcher = new FileSystemWatcher(path)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = false,
            };
            void Bump(object? _, FileSystemEventArgs __) => DispatcherQueue.TryEnqueue(RestartWatchDebounce);
            _folderWatcher.Created += Bump;
            _folderWatcher.Deleted += Bump;
            _folderWatcher.Changed += Bump;
            _folderWatcher.Renamed += (_, _) => DispatcherQueue.TryEnqueue(RestartWatchDebounce);
            _folderWatcher.Error += (_, _) => DispatcherQueue.TryEnqueue(RestartWatchDebounce);
            _folderWatcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            App.Log("FolderWatch", ex); // some network/WSL shares don't support change notifications
            _folderWatcher = null;
            _watchedPath = null;
        }
    }

    private void RestartWatchDebounce() { _watchDebounce.Stop(); _watchDebounce.Start(); }

    private void StopFolderWatch()
    {
        if (_folderWatcher is null) return;
        try { _folderWatcher.EnableRaisingEvents = false; _folderWatcher.Dispose(); } catch { }
        _folderWatcher = null;
    }

    /// <summary>Reloads the current folder while preserving the current selection (by path).</summary>
    private void ReloadKeepingSelection()
    {
        var selected = ActiveExplorerList().SelectedItems.OfType<ExplorerItem>()
            .Select(i => i.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        LoadCurrentFolder();
        if (selected.Count == 0) return;
        var list = ActiveExplorerList();
        foreach (var it in _explorerItems)
            if (selected.Contains(it.Path)) list.SelectedItems.Add(it);
    }

    /// <summary>Applies on-disk changes to the live list in place — inserting new items at their
    /// sorted position and removing deleted ones — so scroll position and selection are kept.
    /// Falls back to a full reload for grouped/search views.</summary>
    private void RefreshFolderIncremental()
    {
        if (_currentFolder is null || !Directory.Exists(_currentFolder)) return;

        // Grouped or search views: patching grouped sources in place is fiddly — reload (keeps selection).
        if (_state.GroupBy != "None" || !string.IsNullOrEmpty(_searchQuery)) { ReloadKeepingSelection(); return; }

        _explorerRaw = _fs.List(_currentFolder, showWindowsHidden: false, _showAppHidden);
        var target = SortItems(_explorerRaw);
        ReconcileExplorerItems(target);

        if (ActiveExplorerList().SelectedItems.Count == 0)
            StatusText.Text = $"{_explorerRaw.Count} item(s)";
        UpdateExplorerEmptyState();
    }

    /// <summary>Mutates <see cref="_explorerItems"/> to match <paramref name="target"/> (ordered by the
    /// current sort) using minimal insert/move/remove ops, keeping existing item objects so their loaded
    /// icons and selection survive.</summary>
    private void ReconcileExplorerItems(List<ExplorerItem> target)
    {
        var coll = _explorerItems;
        var targetPaths = new HashSet<string>(target.Select(t => t.Path), StringComparer.OrdinalIgnoreCase);

        // Remove items that are gone.
        for (var i = coll.Count - 1; i >= 0; i--)
            if (!targetPaths.Contains(coll[i].Path)) coll.RemoveAt(i);

        // Index the survivors by path.
        var existing = new Dictionary<string, ExplorerItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in coll) existing[it.Path] = it;

        // Walk the target order, fixing position i each step.
        for (var i = 0; i < target.Count; i++)
        {
            var path = target[i].Path;
            if (i < coll.Count && string.Equals(coll[i].Path, path, StringComparison.OrdinalIgnoreCase))
                continue;

            if (existing.TryGetValue(path, out var item))
            {
                var from = coll.IndexOf(item);
                if (from != i) coll.Move(from, i);
            }
            else
            {
                coll.Insert(i, target[i]); // new item — its icon loads lazily when realized
                existing[path] = target[i];
            }
        }
    }

    // ---- Sort & group ----

    private void ApplySortAndGroup()
    {
        var basis = SearchBasis();
        var sorted = SortItems(basis);

        // Keep the flat collection current (used by image/collage/slideshow code).
        _explorerItems.Clear();
        foreach (var it in sorted) _explorerItems.Add(it);

        if (_state.GroupBy == "None")
        {
            ExplorerIconsView.ItemsSource = _explorerItems;
            ExplorerDetailsList.ItemsSource = _explorerItems;
        }
        else
        {
            var groups = BuildGroups(sorted);
            ExplorerIconsView.ItemsSource = new CollectionViewSource { IsSourceGrouped = true, Source = groups }.View;
            ExplorerDetailsList.ItemsSource = new CollectionViewSource { IsSourceGrouped = true, Source = groups }.View;
        }

        UpdateSortHeaders();
        UpdateExplorerEmptyState();
    }

    /// <summary>The items to display: the current folder, or search results when a query is active.</summary>
    private List<ExplorerItem> SearchBasis()
    {
        if (string.IsNullOrEmpty(_searchQuery)) return _explorerRaw;
        return _searchRecursive
            ? _searchResults
            : _explorerRaw.Where(i => i.Name.IndexOf(_searchQuery, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
    }

    private void UpdateExplorerEmptyState()
    {
        // The hidden-folder placeholder, when shown, owns the empty area instead.
        if (HiddenFolderPlaceholder.Visibility == Visibility.Visible)
        {
            ExplorerEmpty.Visibility = Visibility.Collapsed;
            return;
        }
        var searching = !string.IsNullOrEmpty(_searchQuery);
        var empty = _explorerItems.Count == 0 && _currentFolder is not null;
        ExplorerEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (!empty) return;
        if (searching)
        {
            ExplorerEmptyTitle.Text = "No matches";
            ExplorerEmptySubtitle.Text = $"Nothing here matches “{_searchQuery}”.";
        }
        else
        {
            ExplorerEmptyTitle.Text = "This folder is empty";
            ExplorerEmptySubtitle.Text = "Drop files here, or use New folder to get started.";
        }
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

    /// <summary>Expand/collapse a group section when its header is clicked.</summary>
    private void GroupHeader_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ExplorerGroup g) g.Toggle();
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
            g.AddItem(it);
        }
        groups.Sort((a, b) => a.Rank.CompareTo(b.Rank) is var c && c != 0 ? c
            : string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase));
        foreach (var g in groups) g.Finish(); // populate visible items now that the group is complete
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

        // Inside a vault or an opened zip, root the trail at the friendly name and hide the temp path.
        if (_currentFolder is not null && SpecialRootFor(_currentFolder) is { } sr)
        {
            var work = sr.root;
            AddCrumb(sr.label, work);
            var rel = System.IO.Path.GetRelativePath(work, _currentFolder);
            if (rel != ".")
            {
                var acc = work;
                foreach (var part in rel.Split(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar))
                {
                    if (string.IsNullOrEmpty(part)) continue;
                    acc = System.IO.Path.Combine(acc, part);
                    Breadcrumb.Children.Add(new TextBlock { Text = "›", Opacity = 0.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 2, 0) });
                    AddCrumb(part, acc);
                }
            }
            BreadcrumbScroller.UpdateLayout();
            BreadcrumbScroller.ChangeView(BreadcrumbScroller.ScrollableWidth, null, null, true);
            return;
        }

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

        e.Data.RequestedOperation = DataPackageOperation.Copy | DataPackageOperation.Move;
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

    /// <summary>Extracts a .zip to a temp folder and navigates into it (browse like a folder).</summary>
    private async Task OpenArchiveAsync(ExplorerItem item)
    {
        StatusText.Text = $"Opening {item.Name}…";
        string tmp;
        try { tmp = await ArchiveService.ExtractToTempAsync(item.Path); }
        catch (Exception ex) { StatusText.Text = ex.Message; return; }
        _openZips[tmp] = (item.Path, item.Name);
        ShowExplorer();
        NavigateTo(tmp);
        StatusText.Text = item.Name;
    }

    private async Task ExtractArchiveHereAsync(ExplorerItem item)
    {
        var parent = System.IO.Path.GetDirectoryName(item.Path);
        if (parent is null) return;
        var dest = UniquePath(System.IO.Path.Combine(parent, System.IO.Path.GetFileNameWithoutExtension(item.Path)), isDir: true);
        StatusText.Text = $"Extracting {item.Name}…";
        try { await ArchiveService.ExtractToFolderAsync(item.Path, dest); }
        catch (Exception ex) { StatusText.Text = ex.Message; return; }
        LoadCurrentFolder();
        StatusText.Text = $"Extracted to {System.IO.Path.GetFileName(dest)}";
    }

    private async Task ExtractArchiveToAsync(ExplorerItem item)
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;
        var dest = UniquePath(System.IO.Path.Combine(folder.Path, System.IO.Path.GetFileNameWithoutExtension(item.Path)), isDir: true);
        StatusText.Text = $"Extracting {item.Name}…";
        try { await ArchiveService.ExtractToFolderAsync(item.Path, dest); }
        catch (Exception ex) { StatusText.Text = ex.Message; return; }
        if (string.Equals(folder.Path, _currentFolder, StringComparison.OrdinalIgnoreCase)) LoadCurrentFolder();
        StatusText.Text = $"Extracted to {dest}";
    }

    private void OpenExplorerItem(ExplorerItem item)
    {
        if (item.IsFolder) NavigateTo(item.Path);
        else if (item.IsImage) OpenImageFromExplorer(item);
        else if (PhotoLibrary.IsMedia(item.Path)) OpenVideoFromExplorer(item);
        else if (ArchiveService.IsArchive(item.Path)) _ = OpenArchiveAsync(item);
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
            var isAudio = PhotoLibrary.IsAudio(item.Path);
            AudioOverlay.Visibility = isAudio ? Visibility.Visible : Visibility.Collapsed;
            AudioTitle.Text = isAudio ? item.Name : "";
            if (isAudio) _ = LoadAlbumArtAsync(file);
            VideoPlayer.Source = MediaSource.CreateFromStorageFile(file);
            var mp = VideoPlayer.MediaPlayer;
            if (mp is not null)
            {
                // Movie category → full multichannel output (no stereo downmix); Windows' spatial
                // engine (Dolby Atmos / DTS:X / Windows Sonic) on the output device renders surround.
                mp.AudioCategory = Windows.Media.Playback.MediaPlayerAudioCategory.Movie;
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

    private int _audioArtToken;

    /// <summary>Shows embedded album art for an audio file (falls back to the music glyph).</summary>
    private async Task LoadAlbumArtAsync(StorageFile file)
    {
        var token = ++_audioArtToken;
        AudioArt.Source = null;
        AudioArtHost.Visibility = Visibility.Collapsed;
        AudioGlyph.Visibility = Visibility.Visible;
        if (!_state.ShowAlbumArt) return;

        try
        {
            using var thumb = await file.GetThumbnailAsync(
                Windows.Storage.FileProperties.ThumbnailMode.MusicView, 480,
                Windows.Storage.FileProperties.ThumbnailOptions.ResizeThumbnail);
            if (token != _audioArtToken) return;
            if (thumb is null || thumb.Type == Windows.Storage.FileProperties.ThumbnailType.Icon) return; // no embedded art

            var bmp = new BitmapImage();
            await bmp.SetSourceAsync(thumb);
            if (token != _audioArtToken) return;

            AudioArt.Source = bmp;
            AudioArtHost.Visibility = Visibility.Visible;
            AudioGlyph.Visibility = Visibility.Collapsed;
        }
        catch { /* keep the music glyph */ }
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
        AudioOverlay.Visibility = Visibility.Collapsed; // set by the caller when the file is audio
    }

    private void EnterImageMode()
    {
        StopVideo();
        VideoPlayer.Visibility = Visibility.Collapsed;
        VideoBackBar.Visibility = Visibility.Collapsed;
        VideoControlsBar.Visibility = Visibility.Collapsed;
        AudioOverlay.Visibility = Visibility.Collapsed;
        ImageHost.Visibility = Visibility.Visible;
        ViewerChrome.Visibility = Visibility.Visible;
    }

    private void StopVideo()
    {
        try
        {
            VideoPlayer.MediaPlayer?.Pause();
            // CreateFromStorageFile hands us a MediaSource we own; the element won't dispose it,
            // so release it here or we leak one native source per video opened.
            var previous = VideoPlayer.Source as MediaSource;
            VideoPlayer.Source = null;
            previous?.Dispose();
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
        // Preserve the explorer's current sort order (LoadFiles re-sorts by name) so the viewer's
        // arrow-key navigation follows whatever sort the user has chosen.
        var paths = _explorerItems.Where(i => i.IsImage).Select(i => i.Path).ToList();
        var byPath = _library.LoadFiles(paths).ToDictionary(p => p.Path, StringComparer.OrdinalIgnoreCase);
        _allPhotos.Clear();
        foreach (var path in paths)
            if (byPath.TryGetValue(path, out var item)) _allPhotos.Add(item);
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

    private async void ShowAppHidden_Click(object sender, RoutedEventArgs e)
    {
        if (ShowHiddenToggle.IsChecked == true && !await EnsureHiddenUnlockedAsync())
        {
            ShowHiddenToggle.IsChecked = false;
            return;
        }
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
            menu.Items.Add(SMI("Cut", Symbol.Cut, async (_, _) => await CutToClipboardAsync(item.Path)));
            menu.Items.Add(SMI("Copy", Symbol.Copy, async (_, _) => await CopyFileToClipboardAsync(item.Path)));
            menu.Items.Add(SMI("Copy path", Symbol.Link, (_, _) => CopyTextToClipboard(item.Path)));
            menu.Items.Add(SMI("Paste", Symbol.Paste, async (_, _) => await PasteIntoCurrentAsync()));
            if (item.IsImage)
            {
                menu.Items.Add(new MenuFlyoutSeparator());
                menu.Items.Add(SMI("Set as desktop background", null, (_, _) => SetWallpaperPath(item.Path)));
                menu.Items.Add(SMI("Set as lock screen", null, async (_, _) => await SetLockScreenAsync(item.Path)));
                menu.Items.Add(SMI("Set as Thumbnail", Symbol.Pictures, (_, _) => SetFolderThumbnail(item.Path)));
            }
            if (!item.IsFolder && ArchiveService.IsArchive(item.Path))
            {
                menu.Items.Add(new MenuFlyoutSeparator());
                menu.Items.Add(SMI("Extract Here", null, async (_, _) => await ExtractArchiveHereAsync(item)));
                menu.Items.Add(SMI("Extract All…", null, async (_, _) => await ExtractArchiveToAsync(item)));
            }
            menu.Items.Add(new MenuFlyoutSeparator());
            if (_vaults.IsAnyUnlocked && !ItemInsideOpenVault(item))
            {
                menu.Items.Add(SMI("Send to Vault", null, async (_, _) =>
                {
                    var sel = SelectedExplorerItems();
                    if (sel.All(s => s != item)) sel = new List<ExplorerItem> { item };
                    await SendToVaultAsync(sel);
                }));
            }
            menu.Items.Add(SMI("Move to new vault…", null, async (_, _) =>
            {
                var sel = SelectedExplorerItems();
                if (sel.All(s => s != item)) sel = new List<ExplorerItem> { item };
                await MoveToNewVaultAsync(sel);
            }));
            menu.Items.Add(new MenuFlyoutSeparator());
            if (item.IsFolder)
            {
                var hidden = _state.HiddenFolders.Contains(item.Path);
                menu.Items.Add(SMI(hidden ? "Unhide folder" : "Hide folder", null, (_, _) => { ToggleFolderHidden(item.Path); LoadCurrentFolder(); }));
                menu.Items.Add(SMI("Pin to sidebar", Symbol.Pin, (_, _) => AddPinnedPath(item.Path)));
                if (_state.DeveloperMode)
                    menu.Items.Add(SMI("Open terminal here", null, async (_, _) => await OpenTerminalHereAsync(item.Path)));
            }
            menu.Items.Add(SMI("Rename…", Symbol.Rename, async (_, _) =>
            {
                var sel = SelectedExplorerItems();
                if (sel.Count > 1 && sel.Any(s => s == item)) await BulkRenameExplorerAsync(item, sel);
                else await RenameExplorerAsync(item);
            }));
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
            var move = content.RequestedOperation.HasFlag(DataPackageOperation.Move); // set by Cut
            var items = await content.GetStorageItemsAsync();
            var paths = items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
            var dest = _currentFolder;
            var count = await Task.Run(() => TransferInto(dest, paths, move));
            LoadCurrentFolder();
            StatusText.Text = $"{(move ? "Moved" : "Pasted")} {count} item(s)";
        }
        catch (Exception ex) { StatusText.Text = $"Paste failed: {ex.Message}"; }
    }

    private async System.Threading.Tasks.Task RenameExplorerAsync(ExplorerItem item)
    {
        var box = new TextBox { Text = item.Name };
        box.Loaded += (_, _) =>
        {
            // Preselect only the base name so the extension is kept by default (Explorer behavior).
            var ext = item.IsFolder ? "" : System.IO.Path.GetExtension(item.Name);
            var baseLen = item.Name.Length - ext.Length;
            if (baseLen > 0) box.Select(0, baseLen); else box.SelectAll();
        };
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

    /// <summary>Bulk-renames a multi-selection like Explorer, but with dash numbering: the primary
    /// item becomes "name", the rest "name-1", "name-2", … (each keeping its own extension).</summary>
    private async System.Threading.Tasks.Task BulkRenameExplorerAsync(ExplorerItem primary, List<ExplorerItem> selection)
    {
        var items = selection.Where(i => i.Kind != ExplorerItemKind.Drive).ToList();
        if (items.Count <= 1) { await RenameExplorerAsync(primary); return; }
        var dir = _currentFolder;
        if (string.IsNullOrEmpty(dir)) return;

        // Primary first, then the rest in their current order.
        items = new[] { primary }.Concat(items.Where(i => i != primary)).ToList();

        var box = new TextBox { Text = primary.Name };
        box.Loaded += (_, _) =>
        {
            // Show the extension but preselect only the base name.
            var ext = primary.IsFolder ? "" : System.IO.Path.GetExtension(primary.Name);
            var baseLen = primary.Name.Length - ext.Length;
            if (baseLen > 0) box.Select(0, baseLen); else box.SelectAll();
        };
        var dlg = new ContentDialog
        {
            Title = $"Rename {items.Count} items",
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = "They'll be named “name”, “name-1”, “name-2”, … keeping each file's extension (or the one you type).",
                                    Opacity = 0.7, FontSize = 12, TextWrapping = TextWrapping.Wrap },
                    box,
                },
            },
            PrimaryButtonText = "Rename",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        var typed = box.Text.Trim();
        if (string.IsNullOrEmpty(typed)) return;
        var typedExt = System.IO.Path.GetExtension(typed); // if the user typed an extension, apply it to all
        var baseName = System.IO.Path.GetFileNameWithoutExtension(typed);
        if (string.IsNullOrEmpty(baseName)) baseName = typed;

        // Resolve storage items + each one's extension.
        var resolved = new List<(IStorageItem si, string ext)>();
        foreach (var it in items)
        {
            try
            {
                IStorageItem si = it.IsFolder
                    ? await StorageFolder.GetFolderFromPathAsync(it.Path)
                    : await StorageFile.GetFileFromPathAsync(it.Path);
                resolved.Add((si, it.IsFolder ? "" : System.IO.Path.GetExtension(it.Name)));
            }
            catch { /* skip unreadable */ }
        }

        // Phase 1: move everything to temp names so target names can't collide with current ones.
        foreach (var (si, _) in resolved)
        {
            try { await si.RenameAsync("__galileo_" + Guid.NewGuid().ToString("N"), NameCollisionOption.GenerateUniqueName); }
            catch { }
        }

        // Phase 2: assign final names with a monotonic counter, skipping names already on disk.
        var counter = 0;
        var ok = 0;
        foreach (var (si, ext) in resolved)
        {
            var useExt = string.IsNullOrEmpty(typedExt) ? ext : typedExt; // typed ext wins, else keep own
            string name;
            while (true)
            {
                name = (counter == 0 ? baseName : $"{baseName}-{counter}") + useExt;
                counter++;
                var full = System.IO.Path.Combine(dir, name);
                if (!File.Exists(full) && !Directory.Exists(full)) break;
            }
            try { await si.RenameAsync(name, NameCollisionOption.FailIfExists); ok++; }
            catch (Exception ex) { StatusText.Text = $"Rename failed: {ex.Message}"; }
        }

        LoadCurrentFolder();
        StatusText.Text = $"Renamed {ok} item(s)";
    }

    /// <summary>True while Shift is held — used to bypass the Recycle Bin (permanent delete).</summary>
    private static bool IsShiftDown()
    {
        var state = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
        return (state & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
    }

    /// <summary>True while Ctrl is held — used to detect clipboard / select-all shortcuts.</summary>
    private static bool IsCtrlDown()
    {
        var state = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
        return (state & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
    }

    /// <summary>True when keyboard focus is in a text field, so explorer shortcuts (Ctrl+A/C/V…)
    /// don't hijack normal text editing in the address bar or a rename box.</summary>
    private bool IsTextInputFocused() =>
        FocusManager.GetFocusedElement(RootGrid.XamlRoot) is TextBox or RichEditBox;

    /// <summary>The explorer list currently on screen (icon grid or details list).</summary>
    private ListViewBase ActiveExplorerList() =>
        ExplorerIconsView.Visibility == Visibility.Visible ? ExplorerIconsView : ExplorerDetailsList;

    /// <summary>The ExplorerItems currently selected in the active explorer list.</summary>
    private List<ExplorerItem> SelectedExplorerItems() =>
        ActiveExplorerList().SelectedItems.OfType<ExplorerItem>().ToList();

    /// <summary>Shows the selection count (and total size) in the status bar; falls back to the item
    /// count when nothing is selected.</summary>
    private void ExplorerSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListViewBase list) return;
        var sel = list.SelectedItems.OfType<ExplorerItem>().ToList();
        if (sel.Count == 0)
        {
            StatusText.Text = list.Items.Count > 0 ? $"{list.Items.Count} item{(list.Items.Count == 1 ? "" : "s")}" : "Ready";
            return;
        }
        var bytes = sel.Where(i => i.Kind == ExplorerItemKind.File).Sum(i => i.Size);
        StatusText.Text = bytes > 0
            ? $"{sel.Count} selected · {FormatBytes(bytes)}"
            : $"{sel.Count} selected";
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return $"{v:0.#} {units[i]}";
    }

    /// <summary>Copies (or cuts) the selected explorer items to the clipboard as files/folders,
    /// so they can be pasted here or into Windows Explorer.</summary>
    private async System.Threading.Tasks.Task CopySelectedExplorerAsync(bool cut)
    {
        // Drives can't be copied/moved — only real files and folders.
        var selection = SelectedExplorerItems().Where(i => i.Kind != ExplorerItemKind.Drive).ToList();
        if (selection.Count == 0) return;
        try
        {
            var items = new List<IStorageItem>();
            foreach (var it in selection)
                items.Add(it.IsFolder
                    ? await StorageFolder.GetFolderFromPathAsync(it.Path)
                    : await StorageFile.GetFileFromPathAsync(it.Path));

            var data = new DataPackage { RequestedOperation = cut ? DataPackageOperation.Move : DataPackageOperation.Copy };
            data.SetStorageItems(items);
            Clipboard.SetContent(data);
            StatusText.Text = $"{(cut ? "Cut" : "Copied")} {items.Count} item(s)";
        }
        catch (Exception ex) { StatusText.Text = $"{(cut ? "Cut" : "Copy")} failed: {ex.Message}"; }
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

        var seg = new FontFamily("Segoe Fluent Icons");
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
        menu.Items.Add(MI("Set as lock screen", "", async (_, _) => await SetLockScreenAsync(item.Path)));
        menu.Items.Add(MI("Set as Thumbnail", "", (_, _) => SetFolderThumbnail(item.Path)));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(MI(item.IsFavorite ? "" : "", item.IsFavorite ? "" : "", (_, _) => FavoriteItem(item)));
        if (!item.IsHidden)
            menu.Items.Add(MI("Hide (Hidden album)", "", (_, _) => HideItemPermanently(item)));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(MI("Rename…", "", async (_, _) => await RenameItemAsync(item)));
        menu.Items.Add(MI("Show in Explorer", "", (_, _) => RevealItem(item)));
        menu.Items.Add(MI("Edit…", "", async (_, _) => await EnterEditModeAsync(item)));
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

    private void SetWallpaper(PhotoItem item) => SetWallpaperPath(item.Path);

    private void SetWallpaperPath(string path)
    {
        StatusText.Text = ShellOps.SetWallpaper(path)
            ? "Set as desktop background"
            : "Couldn't set the desktop background for this image.";
    }

    /// <summary>Sets the current user's lock-screen image (WinRT UserProfile API).</summary>
    private async Task SetLockScreenAsync(string path)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            await Windows.System.UserProfile.LockScreen.SetImageFileAsync(file);
            StatusText.Text = "Set as lock screen";
        }
        catch (Exception ex) { StatusText.Text = "Couldn't set the lock screen: " + ex.Message; App.Log("LockScreen", ex); }
    }

    private void ShowProperties(PhotoItem item)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        ShellOps.ShowProperties(hwnd, item.Path);
    }

    /// <summary>Makes <paramref name="imagePath"/> the preview thumbnail for its parent folder.</summary>
    private void SetFolderThumbnail(string imagePath)
    {
        var folder = System.IO.Path.GetDirectoryName(imagePath);
        if (string.IsNullOrEmpty(folder)) { StatusText.Text = "Couldn't set the folder thumbnail."; return; }
        _state.FolderThumbnails[folder] = imagePath;
        _state.Save();
        RefreshFolderIcon(folder);
        StatusText.Text = $"Folder thumbnail set to {System.IO.Path.GetFileName(imagePath)}";
    }

    /// <summary>Regenerates a folder's icon in the current listing (if it's visible) so a new
    /// thumbnail shows immediately.</summary>
    private void RefreshFolderIcon(string folderPath)
    {
        var match = _explorerItems.FirstOrDefault(i =>
            i.IsFolder && string.Equals(i.Path, folderPath, StringComparison.OrdinalIgnoreCase));
        if (match is null) return;
        match.ResetIcon();
        _ = match.LoadIconAsync((uint)Math.Clamp(_iconSize, 48, 256));
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
        box.Loaded += (_, _) =>
        {
            var ext = System.IO.Path.GetExtension(item.FileName);
            var baseLen = item.FileName.Length - ext.Length;
            if (baseLen > 0) box.Select(0, baseLen); else box.SelectAll();
        };
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

    // ===================== Sortable column headers (Details) =====================

    private void SortHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string key) return;
        if (_state.SortBy == key) _state.SortDescending = !_state.SortDescending;
        else { _state.SortBy = key; _state.SortDescending = false; }
        _state.Save();
        ApplySortAndGroup();
        SyncSortGroupRadios();
    }

    private void UpdateSortHeaders()
    {
        void Set(Button b, string label, string key)
        {
            var arrow = _state.SortDescending ? " ▾" : " ▴"; // ▾ / ▴
            b.Content = _state.SortBy == key ? label + arrow : label;
        }
        Set(HdrName, "Name", "Name");
        Set(HdrDate, "Date modified", "Date");
        Set(HdrType, "Type", "Type");
        Set(HdrSize, "Size", "Size");
    }

    // ===================== Search =====================

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (_suppressSearchEvent) return;
        _searchQuery = sender.Text?.Trim() ?? "";
        _ = RunSearchAsync();
    }

    private void SearchRecursive_Click(object sender, RoutedEventArgs e)
    {
        _searchRecursive = SearchRecursiveToggle.IsChecked == true;
        if (!string.IsNullOrEmpty(_searchQuery)) _ = RunSearchAsync();
    }

    /// <summary>Clears the search box quietly (no reload); callers reload as needed.</summary>
    private void ClearSearch()
    {
        _searchResults = new();
        if (string.IsNullOrEmpty(_searchQuery) && (SearchBox is null || SearchBox.Text.Length == 0)) return;
        _searchQuery = "";
        if (SearchBox is not null && SearchBox.Text.Length > 0)
        {
            _suppressSearchEvent = true;
            SearchBox.Text = "";
            _suppressSearchEvent = false;
        }
    }

    private async Task RunSearchAsync()
    {
        if (string.IsNullOrEmpty(_searchQuery))
        {
            _searchResults = new();
            ApplySortAndGroup();
            ApplyViewMode();
            StatusText.Text = _currentFolder is null ? "This PC" : $"{_explorerRaw.Count} item(s)";
            return;
        }

        if (_searchRecursive && _currentFolder is not null)
        {
            var q = _searchQuery;
            var root = _currentFolder;
            StatusText.Text = $"Searching {System.IO.Path.GetFileName(root.TrimEnd('\\'))}…";
            var results = await Task.Run(() => _fs.Search(root, q));
            if (q != _searchQuery || root != _currentFolder) return; // a newer query/folder superseded us
            _searchResults = results;
        }

        ApplySortAndGroup();
        ApplyViewMode();
        StatusText.Text = $"{_explorerItems.Count} result(s) for “{_searchQuery}”";
    }

    // ===================== Folder tabs =====================

    private sealed class ExplorerTab
    {
        public List<string?> History { get; } = new();
        public int Index { get; set; } = -1;
        public string? Current => Index >= 0 && Index < History.Count ? History[Index] : null;
    }

    private void NewTab(string? path)
    {
        var tvi = new TabViewItem { Tag = new ExplorerTab(), Header = "This PC", IconSource = new SymbolIconSource { Symbol = Symbol.Folder } };
        _switchingTabs = true;
        ExplorerTabs.TabItems.Add(tvi);
        ExplorerTabs.SelectedItem = tvi;
        _switchingTabs = false;

        // Fresh navigation state for the new tab, then go.
        _navHistory.Clear();
        _navIndex = -1;
        _currentFolder = null;
        ShowExplorer();
        NavigateTo(path);
    }

    private void SyncActiveTab()
    {
        if (ExplorerTabs?.SelectedItem is not TabViewItem tvi || tvi.Tag is not ExplorerTab tab) return;
        tab.History.Clear();
        tab.History.AddRange(_navHistory);
        tab.Index = _navIndex;
        tvi.Header = TabHeaderFor(_currentFolder);
    }

    private string TabHeaderFor(string? folder)
    {
        if (folder is null) return "This PC";
        // Inside a vault or an opened zip, show the friendly name at its root (not the temp GUID path).
        if (SpecialRootFor(folder) is { } sr)
        {
            return string.Equals(folder.TrimEnd('\\'), sr.root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                ? sr.label
                : System.IO.Path.GetFileName(folder.TrimEnd('\\'));
        }
        var name = System.IO.Path.GetFileName(folder.TrimEnd('\\'));
        return string.IsNullOrEmpty(name) ? folder : name;
    }

    /// <summary>If the path is inside a special root (unlocked vault working dir or an opened zip's
    /// temp dir), returns the friendly label + that root path; otherwise null.</summary>
    private (string label, string root)? SpecialRootFor(string? path)
    {
        if (path is null) return null;
        if (_vaults.Current?.WorkingDir is { } w && path.StartsWith(w, StringComparison.OrdinalIgnoreCase))
            return (_vaults.Current.Name, w);
        foreach (var kv in _openZips)
            if (path.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase))
                return (kv.Value.Name, kv.Key);
        return null;
    }

    private void ExplorerTabs_AddClick(TabView sender, object args) => NewTab(null);

    private void ExplorerTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_switchingTabs) return;
        if (ExplorerTabs.SelectedItem is not TabViewItem tvi || tvi.Tag is not ExplorerTab tab) return;

        // Load the selected tab's navigation state into the live fields.
        _navHistory.Clear();
        _navHistory.AddRange(tab.History);
        _navIndex = tab.Index;
        _currentFolder = tab.Current;
        ClearSearch();
        ShowExplorer();
        LoadCurrentFolder();
        UpdateNavButtons();
        BuildBreadcrumb();
    }

    private void ExplorerTabs_CloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        if (sender.TabItems.Count <= 1) return; // always keep one tab
        sender.TabItems.Remove(args.Tab);       // removal re-selects a neighbour → SelectionChanged loads it
    }

    // ===================== Cut / move / drop between folders =====================

    private async Task CutToClipboardAsync(string path)
    {
        try
        {
            IStorageItem si = Directory.Exists(path)
                ? await StorageFolder.GetFolderFromPathAsync(path)
                : await StorageFile.GetFileFromPathAsync(path);
            var data = new DataPackage { RequestedOperation = DataPackageOperation.Move };
            data.SetStorageItems(new[] { si });
            Clipboard.SetContent(data);
            StatusText.Text = "Cut to clipboard";
        }
        catch (Exception ex) { StatusText.Text = $"Cut failed: {ex.Message}"; }
    }

    // ===================== Rubber-band (marquee) selection =====================

    private static GridViewItem? FindAncestorGridViewItem(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is GridViewItem gvi) return gvi;
            node = VisualTreeHelper.GetParent(node);
        }
        return null;
    }

    private void ExplorerIcons_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(ExplorerIconsView);
        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse && !pt.Properties.IsLeftButtonPressed)
            return;
        // A press on an item belongs to selection/drag; only empty space starts a marquee.
        if (FindAncestorGridViewItem(e.OriginalSource as DependencyObject) is not null) return;

        _marqueeActive = true;
        _marqueeStart = e.GetCurrentPoint(ExplorerContentArea).Position;
        if (!IsCtrlDown()) ExplorerIconsView.SelectedItems.Clear();
        ExplorerIconsView.CapturePointer(e.Pointer);
        UpdateMarquee(_marqueeStart);
        MarqueeRect.Visibility = Visibility.Visible;
        e.Handled = true;
    }

    private void ExplorerIcons_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_marqueeActive) return;
        UpdateMarquee(e.GetCurrentPoint(ExplorerContentArea).Position);
        SelectWithinMarquee();
        e.Handled = true;
    }

    private void ExplorerIcons_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_marqueeActive) return;
        EndMarquee(e.Pointer);
        e.Handled = true;
    }

    private void ExplorerIcons_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_marqueeActive) EndMarquee(null);
    }

    private void EndMarquee(Pointer? pointer)
    {
        _marqueeActive = false;
        MarqueeRect.Visibility = Visibility.Collapsed;
        if (pointer is not null) ExplorerIconsView.ReleasePointerCapture(pointer);
    }

    private void UpdateMarquee(Windows.Foundation.Point cur)
    {
        var x = Math.Min(_marqueeStart.X, cur.X);
        var y = Math.Min(_marqueeStart.Y, cur.Y);
        MarqueeRect.Margin = new Thickness(x, y, 0, 0);
        MarqueeRect.Width = Math.Abs(cur.X - _marqueeStart.X);
        MarqueeRect.Height = Math.Abs(cur.Y - _marqueeStart.Y);
    }

    private void SelectWithinMarquee()
    {
        var box = new Windows.Foundation.Rect(MarqueeRect.Margin.Left, MarqueeRect.Margin.Top, MarqueeRect.Width, MarqueeRect.Height);
        foreach (var item in _explorerItems)
        {
            if (ExplorerIconsView.ContainerFromItem(item) is not GridViewItem c) continue; // realized containers only
            var b = c.TransformToVisual(ExplorerContentArea).TransformBounds(new Windows.Foundation.Rect(0, 0, c.ActualWidth, c.ActualHeight));
            var hit = !(b.Right < box.Left || b.Left > box.Right || b.Bottom < box.Top || b.Top > box.Bottom);
            var selected = ExplorerIconsView.SelectedItems.Contains(item);
            if (hit && !selected) ExplorerIconsView.SelectedItems.Add(item);
            else if (!hit && selected) ExplorerIconsView.SelectedItems.Remove(item);
        }
    }

    /// <summary>Finds the folder/drive whose item sits under the drop point (the drag event's
    /// OriginalSource is the list itself, not the row, so we hit-test by position instead). Falls
    /// back to the current folder when the drop isn't over a folder.</summary>
    private string? DropTargetFolder(DragEventArgs e)
    {
        var view = ExplorerIconsView.Visibility == Visibility.Visible
            ? (ItemsControl)ExplorerIconsView : ExplorerDetailsList;
        var pos = e.GetPosition((UIElement)view);
        foreach (var item in _explorerItems)
        {
            if (!item.IsFolder) continue;
            if (view.ContainerFromItem(item) is not FrameworkElement c) continue; // realized rows only
            var tl = c.TransformToVisual((UIElement)view).TransformPoint(new Windows.Foundation.Point(0, 0));
            if (pos.X >= tl.X && pos.X <= tl.X + c.ActualWidth && pos.Y >= tl.Y && pos.Y <= tl.Y + c.ActualHeight)
                return item.Path; // dropped onto this folder/drive
        }
        return _currentFolder;
    }

    private void ExplorerList_DragOver(object sender, DragEventArgs e)
    {
        var target = DropTargetFolder(e);
        if (target is null || !e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }
        // Explorer convention: dragging into a folder MOVES by default; hold Ctrl to copy.
        var copy = IsCtrlDown();
        e.AcceptedOperation = copy ? DataPackageOperation.Copy : DataPackageOperation.Move;
        if (e.DragUIOverride is not null)
        {
            e.DragUIOverride.Caption = copy ? "Copy here" : "Move here";
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.IsGlyphVisible = true;
        }
        e.Handled = true; // keep RootGrid's "open" drop from also firing
    }

    private async void ExplorerList_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var target = DropTargetFolder(e);
        if (target is null) return;
        var move = !IsCtrlDown(); // default move; Ctrl copies
        e.Handled = true;

        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var paths = items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
            if (paths.Count == 0) return;
            var count = await Task.Run(() => TransferInto(target, paths, move));
            LoadCurrentFolder();
            StatusText.Text = $"{(move ? "Moved" : "Copied")} {count} item(s) to {TabHeaderFor(target)}";
        }
        catch (Exception ex) { StatusText.Text = $"Drop failed: {ex.Message}"; App.Log("Drop", ex); }
        finally { deferral.Complete(); }
    }

    /// <summary>Copies or moves files/folders into a destination directory. Runs off the UI thread.</summary>
    private static int TransferInto(string destDir, List<string> paths, bool move)
    {
        var n = 0;
        foreach (var src in paths)
        {
            try
            {
                var name = System.IO.Path.GetFileName(src.TrimEnd('\\', '/'));
                if (string.IsNullOrEmpty(name)) continue;

                if (Directory.Exists(src))
                {
                    var srcParent = System.IO.Path.GetDirectoryName(src.TrimEnd('\\', '/'));
                    if (string.Equals(srcParent, destDir, StringComparison.OrdinalIgnoreCase) && move) continue; // no-op
                    var dest = UniquePath(System.IO.Path.Combine(destDir, name), isDir: true);
                    if (IsSubPath(src, dest)) continue; // don't move a folder into itself
                    if (move)
                    {
                        try { Directory.Move(src, dest); }
                        catch { CopyDir(src, dest); Directory.Delete(src, true); } // cross-volume fallback
                    }
                    else CopyDir(src, dest);
                    n++;
                }
                else if (File.Exists(src))
                {
                    var srcParent = System.IO.Path.GetDirectoryName(src);
                    if (string.Equals(srcParent, destDir, StringComparison.OrdinalIgnoreCase) && move) continue; // no-op
                    var dest = UniquePath(System.IO.Path.Combine(destDir, name), isDir: false);
                    if (move) File.Move(src, dest);
                    else File.Copy(src, dest);
                    n++;
                }
            }
            catch { /* skip the offending item, keep going */ }
        }
        return n;
    }

    private static string UniquePath(string path, bool isDir)
    {
        if (isDir ? !Directory.Exists(path) : !File.Exists(path)) return path;
        var dir = System.IO.Path.GetDirectoryName(path)!;
        var stem = isDir ? System.IO.Path.GetFileName(path) : System.IO.Path.GetFileNameWithoutExtension(path);
        var ext = isDir ? "" : System.IO.Path.GetExtension(path);
        for (var i = 2; i < 10000; i++)
        {
            var candidate = System.IO.Path.Combine(dir, $"{stem} ({i}){ext}");
            if (isDir ? !Directory.Exists(candidate) : !File.Exists(candidate)) return candidate;
        }
        return path;
    }

    private static void CopyDir(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, System.IO.Path.Combine(dest, System.IO.Path.GetFileName(file)), overwrite: false);
        foreach (var dir in Directory.EnumerateDirectories(source))
            CopyDir(dir, System.IO.Path.Combine(dest, System.IO.Path.GetFileName(dir)));
    }

    private static bool IsSubPath(string parent, string child)
    {
        var p = System.IO.Path.GetFullPath(parent).TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar;
        var c = System.IO.Path.GetFullPath(child).TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar;
        return c.StartsWith(p, StringComparison.OrdinalIgnoreCase);
    }

    // ===================== Privacy gate (Windows Hello) =====================

    /// <summary>
    /// Returns true if the Hidden album / app-hidden folders may be revealed. When the lock is on,
    /// prompts Windows Hello (falling back to a confirmation if Hello isn't available).
    /// </summary>
    private async Task<bool> EnsureHiddenUnlockedAsync()
    {
        if (!_state.LockHiddenAlbum || _helloUnlocked) return true;

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var hello = await HelloAuth.VerifyAsync(hwnd, "Verify your identity to reveal hidden items");
        bool ok;
        if (hello.HasValue) ok = hello.Value;
        else
        {
            // No Hello on this device — fall back to an explicit confirmation.
            var dialog = new ContentDialog
            {
                Title = "Reveal hidden items?",
                Content = "Windows Hello isn't set up, so identity can't be verified. Reveal hidden items anyway?",
                PrimaryButtonText = "Reveal",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = RootGrid.XamlRoot
            };
            ok = await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        if (ok) _helloUnlocked = true;
        return ok;
    }

    // ===================== Entrance animations =====================

    private void AnimateSettingsIn()
    {
        // Guaranteed final state first, so the card is always visible even if the animation no-ops.
        SettingsCard.Opacity = 1;
        SettingsCardTransform.ScaleX = SettingsCardTransform.ScaleY = 1;
        SettingsCardTransform.TranslateY = 0;

        try
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var sb = new Storyboard();

            // Animations must target the element with a full property path; targeting a bare
            // CompositeTransform object does not resolve in WinUI 3 (and can failfast the render thread).
            void Add(string path, double from, double to)
            {
                var anim = new DoubleAnimation
                {
                    From = from,
                    To = to,
                    Duration = TimeSpan.FromMilliseconds(180),
                    EasingFunction = ease
                };
                Storyboard.SetTarget(anim, SettingsCard);
                Storyboard.SetTargetProperty(anim, path);
                sb.Children.Add(anim);
            }
            Add("Opacity", 0, 1);
            Add("(UIElement.RenderTransform).(CompositeTransform.ScaleX)", 0.97, 1);
            Add("(UIElement.RenderTransform).(CompositeTransform.ScaleY)", 0.97, 1);
            Add("(UIElement.RenderTransform).(CompositeTransform.TranslateY)", 14, 0);
            sb.Begin();
        }
        catch (Exception ex)
        {
            App.Log("AnimateSettingsIn", ex); // final state is already applied above
        }
    }

    // ===================== Google Drive backup =====================

    private void UpdateBackupUi()
    {
        var configured = GoogleDriveBackup.IsConfigured;
        var connected = _drive.IsConnected;
        BackupStatusText.Text = !configured ? "Sign-in unavailable — no Google OAuth client configured"
            : connected
                ? (string.IsNullOrEmpty(_drive.ConnectedEmail) ? "Signed in" : $"Signed in as {_drive.ConnectedEmail}")
                : "Not signed in";
        BackupConnectBtn.Content = connected ? "Sign out" : "Sign in with Google";
        BackupNowBtn.IsEnabled = connected;
        BackupRestoreBtn.IsEnabled = connected;
        LastBackupText.Text = _state.LastVaultBackupUtcTicks > 0
            ? $"Last backup: {new DateTime(_state.LastVaultBackupUtcTicks, DateTimeKind.Utc).ToLocalTime():yyyy-MM-dd HH:mm}"
            : "";
    }

    private async Task SilentReconnectDriveAsync()
    {
        try { await _drive.TryReconnectAsync(); }
        catch (Exception ex) { App.Log("DriveReconnect", ex); }
        DispatcherQueue.TryEnqueue(() =>
        {
            if (SettingsOverlay.Visibility == Visibility.Visible) UpdateBackupUi();
        });
    }

    private async void BackupConnect_Click(object sender, RoutedEventArgs e)
    {
        if (!GoogleDriveBackup.IsConfigured) { await ShowBackupSetupHelpAsync(); return; }
        if (_drive.IsConnected) { await _drive.DisconnectAsync(); UpdateBackupUi(); return; }

        BackupStatusText.Text = "Opening your browser to sign in…";
        try { await _drive.ConnectAsync(forcePrompt: true); }
        catch (OperationCanceledException) { BackupStatusText.Text = "Sign-in canceled or timed out."; }
        catch (Exception ex) { BackupStatusText.Text = "Connect failed: " + ex.Message; App.Log("DriveConnect", ex); }
        UpdateBackupUi();
    }

    private async void BackupNow_Click(object sender, RoutedEventArgs e)
    {
        if (!_drive.IsConnected) return;
        var vaults = _vaults.List();
        if (vaults.Count == 0) { BackupStatusText.Text = "No vaults to back up."; return; }

        BackupNowBtn.IsEnabled = false;
        var progress = new Progress<string>(m => BackupStatusText.Text = m);
        try
        {
            var n = 0;
            foreach (var v in vaults)
            {
                BackupStatusText.Text = $"Backing up “{v.Name}” ({++n}/{vaults.Count})…";
                await _drive.BackupVaultAsync(v, progress);
            }
            _state.LastVaultBackupUtcTicks = DateTime.UtcNow.Ticks;
            ForceSaveState();
            UpdateBackupUi();
            BackupStatusText.Text = $"Backed up {vaults.Count} vault(s).";
        }
        catch (Exception ex) { BackupStatusText.Text = "Backup failed: " + ex.Message; App.Log("DriveBackup", ex); }
        finally { BackupNowBtn.IsEnabled = _drive.IsConnected; }
    }

    private async void BackupRestore_Click(object sender, RoutedEventArgs e)
    {
        if (!_drive.IsConnected) return;

        BackupStatusText.Text = "Listing backups…";
        IReadOnlyList<RemoteVault> backups;
        try { backups = await _drive.ListBackupsAsync(); }
        catch (Exception ex) { BackupStatusText.Text = "Couldn't list backups: " + ex.Message; return; }
        if (backups.Count == 0) { BackupStatusText.Text = "No backups found in Drive."; return; }

        var list = new ListView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 300 };
        foreach (var b in backups)
        {
            var local = Directory.Exists(System.IO.Path.Combine(VaultManager.VaultsRoot, b.Id));
            list.Items.Add(new TextBlock { Text = $"{b.Id}  ·  {b.FileCount} files{(local ? "  (already on this PC)" : "")}" });
        }
        list.SelectedIndex = 0;

        var dlg = new ContentDialog
        {
            Title = "Restore vault from Drive",
            Content = list,
            PrimaryButtonText = "Restore",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary || list.SelectedIndex < 0) return;

        var chosen = backups[list.SelectedIndex];
        BackupStatusText.Text = "Restoring…";
        try
        {
            await _drive.RestoreVaultAsync(chosen.Id, new Progress<string>(m => BackupStatusText.Text = m));
            RefreshVaults();
            BackupStatusText.Text = "Restored — unlock it from the sidebar.";
        }
        catch (Exception ex) { BackupStatusText.Text = "Restore failed: " + ex.Message; App.Log("DriveRestore", ex); }
    }

    private async Task BackupSingleVaultAsync(string vaultId)
    {
        Vault v;
        try
        {
            v = _vaults.Current?.Id == vaultId ? _vaults.Current
                : Vault.Load(System.IO.Path.Combine(VaultManager.VaultsRoot, vaultId));
        }
        catch (Exception ex) { StatusText.Text = "Backup failed: " + ex.Message; return; }

        if (!_drive.IsConnected)
        {
            if (!GoogleDriveBackup.IsConfigured) { await ShowBackupSetupHelpAsync(); return; }
            StatusText.Text = "Connecting to Google Drive…";
            try { await _drive.ConnectAsync(); }
            catch (Exception ex) { StatusText.Text = "Connect failed: " + ex.Message; return; }
        }

        StatusText.Text = $"Backing up “{v.Name}”…";
        try
        {
            await _drive.BackupVaultAsync(v, new Progress<string>(m => StatusText.Text = m));
            _state.LastVaultBackupUtcTicks = DateTime.UtcNow.Ticks;
            ForceSaveState();
            StatusText.Text = $"Backed up “{v.Name}” to Google Drive.";
        }
        catch (Exception ex) { StatusText.Text = "Backup failed: " + ex.Message; App.Log("DriveBackup", ex); }
    }

    /// <summary>Persists state even while the Settings dialog has Save suppressed (for the backup timestamp).</summary>
    private void ForceSaveState()
    {
        var prev = _state.SuppressSave;
        _state.SuppressSave = false;
        _state.Save();
        _state.SuppressSave = prev;
    }

    private async Task ShowBackupSetupHelpAsync()
    {
        var msg = "Sign-in needs a Google OAuth client. Galileo ships one in Assets\\google-oauth.json; " +
                  "to use your own instead:\n\n" +
                  "1. Create a project at console.cloud.google.com\n" +
                  "2. Enable the Google Drive API\n" +
                  "3. Create an OAuth client ID of type “Desktop app”\n" +
                  "4. Download its JSON and save it as:\n" +
                  GoogleDriveBackup.OAuthConfigPath + "\n\n" +
                  "Then reopen Settings and click Sign in with Google.";
        await new ContentDialog
        {
            Title = "Set up Google Drive sign-in",
            Content = new TextBlock { Text = msg, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "OK",
            XamlRoot = RootGrid.XamlRoot,
        }.ShowAsync();
    }

    // ===================== Developer-mode terminal =====================

    private void ApplyDeveloperMode()
    {
        TerminalBtn.Visibility = _state.DeveloperMode ? Visibility.Visible : Visibility.Collapsed;
        if (!_state.DeveloperMode)
        {
            HideTerminal();
            try { _term?.Dispose(); } catch { }
            _term = null;
        }
    }

    private void DeveloperModeSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _state.DeveloperMode = DeveloperModeSwitch.IsOn;
        _state.Save();
        ApplyDeveloperMode();
    }

    private async void TerminalToggle_Click(object sender, RoutedEventArgs e)
    {
        if (TerminalPane.Visibility == Visibility.Visible) HideTerminal();
        else await ShowTerminalAsync();
    }

    private void TerminalClose_Click(object sender, RoutedEventArgs e) => HideTerminal();

    private async Task OpenTerminalHereAsync(string folder)
    {
        NavigateTo(folder);
        await ShowTerminalAsync();
        if (_termWebReady) StartTerminalSession(_termCols, _termRows); // (re)start the shell in this folder
    }

    private void HideTerminal()
    {
        TerminalPane.Visibility = Visibility.Collapsed;
        TerminalSplitter.Visibility = Visibility.Collapsed;
        TerminalCol.Width = new GridLength(0);
    }

    private async Task ShowTerminalAsync()
    {
        TerminalCol.Width = new GridLength(Math.Clamp(ExplorerView.ActualWidth * 0.4, 280, 640));
        TerminalSplitter.Visibility = Visibility.Visible;
        TerminalPane.Visibility = Visibility.Visible;

        if (ShellCombo.Items.Count == 0) PopulateShells();
        if (_termWebReady) return; // the session keeps running across hide/show

        try
        {
            await TerminalWeb.EnsureCoreWebView2Async();
            var assets = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "terminal");
            TerminalWeb.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "galileo.terminal", assets, Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);
            TerminalWeb.CoreWebView2.WebMessageReceived += Terminal_WebMessageReceived;
            TerminalWeb.CoreWebView2.Navigate("https://galileo.terminal/index.html");
            _termWebReady = true;
        }
        catch (Exception ex) { StatusText.Text = "Terminal failed to start: " + ex.Message; App.Log("Terminal", ex); }
    }

    private void Terminal_WebMessageReceived(Microsoft.Web.WebView2.Core.CoreWebView2 sender,
        Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs args)
    {
        string msg;
        try { msg = args.TryGetWebMessageAsString(); } catch { return; }
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(msg);
            var root = doc.RootElement;
            switch (root.GetProperty("t").GetString())
            {
                case "in":
                    var b64 = root.GetProperty("d").GetString();
                    if (!string.IsNullOrEmpty(b64)) _term?.Write(Convert.FromBase64String(b64));
                    break;
                case "size":
                    _termCols = (short)root.GetProperty("cols").GetInt32();
                    _termRows = (short)root.GetProperty("rows").GetInt32();
                    if (_term is null) StartTerminalSession(_termCols, _termRows);
                    else _term.Resize(_termCols, _termRows);
                    break;
            }
        }
        catch { /* ignore malformed messages */ }
    }

    private void StartTerminalSession(short cols, short rows)
    {
        if (cols <= 0) cols = 80;
        if (rows <= 0) rows = 24;
        try { _term?.Dispose(); } catch { }
        _term = null;

        var exe = ShellCombo.SelectedIndex >= 0 && ShellCombo.SelectedIndex < _shells.Count
            ? _shells[ShellCombo.SelectedIndex].Exe
            : Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        var cwd = _currentFolder is not null && Directory.Exists(_currentFolder)
            ? _currentFolder
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var session = new TerminalSession();
        session.Output += OnTerminalOutput;
        try { session.Start(exe, null, cwd, cols, rows); _term = session; }
        catch (Exception ex) { StatusText.Text = "Couldn't start the shell: " + ex.Message; App.Log("Terminal", ex); session.Dispose(); }
    }

    private void OnTerminalOutput(byte[] data)
    {
        var b64 = Convert.ToBase64String(data);
        DispatcherQueue.TryEnqueue(() =>
        {
            try { TerminalWeb.CoreWebView2?.PostWebMessageAsString("{\"t\":\"out\",\"d\":\"" + b64 + "\"}"); }
            catch { }
        });
    }

    private void ShellCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ShellCombo.SelectedIndex < 0 || ShellCombo.SelectedIndex >= _shells.Count) return;
        _state.TerminalShell = LabelToKey(_shells[ShellCombo.SelectedIndex].Label);
        _state.Save();
        if (_term is not null) StartTerminalSession(_termCols, _termRows); // restart in the chosen shell
    }

    private void PopulateShells()
    {
        _shells.Clear();
        _shells.Add(("Command Prompt", Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe"));
        var ps = FindOnPath("pwsh.exe") ?? FindOnPath("powershell.exe");
        if (ps is not null) _shells.Add(("PowerShell", ps));
        var wsl = FindOnPath("wsl.exe");
        if (wsl is not null) _shells.Add(("WSL", wsl));

        ShellCombo.Items.Clear();
        foreach (var s in _shells) ShellCombo.Items.Add(s.Label);
        var want = SavedShellLabel();
        var idx = _shells.FindIndex(s => s.Label == want);
        ShellCombo.SelectedIndex = idx >= 0 ? idx : 0;
    }

    private string SavedShellLabel() => _state.TerminalShell switch
    {
        "powershell" => "PowerShell",
        "wsl" => "WSL",
        _ => "Command Prompt",
    };

    private static string LabelToKey(string label) => label switch
    {
        "PowerShell" => "powershell",
        "WSL" => "wsl",
        _ => "cmd",
    };

    private static string? FindOnPath(string exe)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(System.IO.Path.PathSeparator))
        {
            try { var full = System.IO.Path.Combine(dir.Trim(), exe); if (File.Exists(full)) return full; }
            catch { }
        }
        return null;
    }

    /// <summary>Gives an element the ↔ resize cursor. (WinUI's ProtectedCursor is protected and
    /// Border is sealed, so we set it via reflection.)</summary>
    private static void SetResizeCursor(UIElement element)
    {
        try
        {
            var prop = typeof(UIElement).GetProperty("ProtectedCursor",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            prop?.SetValue(element, Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.SizeWestEast));
        }
        catch { /* cosmetic only */ }
    }

    private void TerminalSplitter_ManipulationDelta(object sender, ManipulationDeltaRoutedEventArgs e)
    {
        var w = TerminalCol.ActualWidth - e.Delta.Translation.X; // drag left → wider terminal
        var max = Math.Max(280, ExplorerView.ActualWidth - 360);
        TerminalCol.Width = new GridLength(Math.Clamp(w, 240, max));
    }

    private void SidebarSplitter_ManipulationDelta(object sender, ManipulationDeltaRoutedEventArgs e)
    {
        var w = SidebarCol.ActualWidth + e.Delta.Translation.X; // drag right → wider sidebar
        SidebarCol.Width = new GridLength(Math.Clamp(w, 160, 560));
    }

    private void SidebarSplitter_ManipulationCompleted(object sender, ManipulationCompletedRoutedEventArgs e)
    {
        _state.SidebarWidth = SidebarCol.ActualWidth;
        _state.Save();
    }

    // ===================== Settings =====================

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        // Snapshot current state and suppress writes; edits apply live for preview but only persist
        // when the user clicks Save (Cancel reverts to this snapshot).
        _settingsSnapshot = _state.Clone();
        _state.SuppressSave = true;

        _loadingSettings = true;
        ThemeCombo.SelectedIndex = _state.Theme switch { "Light" => 1, "Dark" => 2, "Terminal" => 3, "Gray" => 4, _ => 0 };
        OpenModeCombo.SelectedIndex = _state.SingleClickToOpen ? 1 : 0;
        IconSizeCombo.SelectedIndex = _iconSize <= 85 ? 0 : _iconSize >= 140 ? 2 : 1;
        FolderPreviewSwitch.IsOn = _state.FolderPreviews;
        ShowExtensionsSwitch.IsOn = _state.ShowExtensions;
        PeekSwitch.IsOn = _state.PeekEnabled;
        AlbumArtSwitch.IsOn = _state.ShowAlbumArt;
        SingleInstanceSwitch.IsOn = _state.SingleInstance;
        LockHiddenSwitch.IsOn = _state.LockHiddenAlbum;
        var vaultIdleMin = Math.Clamp(_state.VaultIdleSeconds / 60, 0, 60);
        VaultIdleSlider.Value = vaultIdleMin;
        VaultIdleValue.Text = vaultIdleMin == 0 ? "Never" : $"{vaultIdleMin} min";
        VaultHelloSwitch.IsOn = _state.VaultDefaultUseHello;
        VaultWipeSwitch.IsOn = _state.VaultWipeOnFailure;
        VaultWipeCountBox.Value = _state.VaultWipeAfterAttempts;
        VaultWipeCountRow.Visibility = _state.VaultWipeOnFailure ? Visibility.Visible : Visibility.Collapsed;
        DeveloperModeSwitch.IsOn = _state.DeveloperMode;
        CollageLayoutCombo.SelectedIndex = (int)_collagePreset;
        UpdateBackupUi();
        SlideshowSecondsSlider.Value = Math.Clamp(_state.SlideshowSeconds, 2, 30);
        SlideshowSecondsValue.Text = $"{_state.SlideshowSeconds}s";
        ShuffleSwitch.IsOn = _state.SlideshowShuffle;
        LoopSwitch.IsOn = _state.SlideshowLoop;
        TransitionCombo.SelectedIndex = (int)_state.SlideshowTransition;
        _loadingSettings = false;

        // Cap the card to the current window height (so it scrolls on short windows) using a
        // known-laid-out element — ActualHeight bindings don't update reliably in WinUI.
        SettingsCard.MaxHeight = Math.Max(320, RootGrid.ActualHeight - 40);
        SettingsOverlay.Visibility = Visibility.Visible;
        AnimateSettingsIn();
    }

    private void SingleInstanceSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _state.SingleInstance = SingleInstanceSwitch.IsOn;
        _state.Save();
    }

    private void LockHiddenSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _state.LockHiddenAlbum = LockHiddenSwitch.IsOn;
        if (!_state.LockHiddenAlbum) _helloUnlocked = false; // re-arm the gate when turned off
        _state.Save();
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

    private void CloseSettings() => SettingsOverlay.Visibility = Visibility.Collapsed;

    private void SettingsSave_Click(object sender, RoutedEventArgs e)
    {
        _state.SuppressSave = false;
        _state.Save();
        _settingsSnapshot = null;
        CloseSettings();
    }

    private void SettingsCancel_Click(object sender, RoutedEventArgs e) => CancelSettings();

    /// <summary>Reverts any live-applied edits to the pre-open snapshot and closes without saving.</summary>
    private void CancelSettings()
    {
        if (_settingsSnapshot is not null)
        {
            _state.CopySettingsFrom(_settingsSnapshot);
            _settingsSnapshot = null;
            ReapplyAllSettings();   // push the reverted values back to the live UI
        }
        _state.SuppressSave = false;
        CloseSettings();
    }

    /// <summary>Pushes the current <see cref="_state"/> values into the live app (theme, icon size,
    /// explorer flags, idle timer). Used to revert on Cancel.</summary>
    private void ReapplyAllSettings()
    {
        _iconSize = _state.IconSize is > 0 and <= 240 ? _state.IconSize : 110;
        _collagePreset = ParseCollagePreset(_state.CollagePreset);
        ExplorerItem.ShowFolderPreviews = _state.FolderPreviews;
        ExplorerItem.ShowExtensions = _state.ShowExtensions;
        ApplyTheme();
        ApplyClickMode();
        IconSizeSlider.Value = _iconSize;
        ApplyIconSize();
        ResetVaultIdle();
        ApplyDeveloperMode();
        if (ExplorerView.Visibility == Visibility.Visible) LoadCurrentFolder();
    }

    // The X and the dim scrim both cancel (discard edits); a tap inside the card is swallowed.
    private void SettingsClose_Click(object sender, RoutedEventArgs e) => CancelSettings();
    private void SettingsScrim_Tapped(object sender, TappedRoutedEventArgs e) => CancelSettings();
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

    private void PeekSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _state.PeekEnabled = PeekSwitch.IsOn;
        _state.Save();
        if (!_state.PeekEnabled) ClosePeek();
    }

    private void AlbumArtSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _state.ShowAlbumArt = AlbumArtSwitch.IsOn;
        _state.Save();
    }

    private void VaultIdleSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        var min = (int)Math.Round(e.NewValue);
        if (VaultIdleValue is not null) VaultIdleValue.Text = min == 0 ? "Never" : $"{min} min";
        if (_loadingSettings) return;
        _state.VaultIdleSeconds = min * 60;
        _state.Save();
        ResetVaultIdle(); // apply immediately to an open vault
    }

    private void VaultHelloSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _state.VaultDefaultUseHello = VaultHelloSwitch.IsOn;
        _state.Save();
    }

    private void VaultWipeSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        VaultWipeCountRow.Visibility = VaultWipeSwitch.IsOn ? Visibility.Visible : Visibility.Collapsed;
        if (_loadingSettings) return;
        _state.VaultWipeOnFailure = VaultWipeSwitch.IsOn;
        _state.Save();
    }

    private void VaultWipeCount_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loadingSettings || double.IsNaN(args.NewValue)) return;
        _state.VaultWipeAfterAttempts = Math.Clamp((int)args.NewValue, 1, 50);
        _state.Save();
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
        // While the Peek overlay is open it owns the keyboard (handled in Peek_KeyDown) — don't let
        // the explorer/viewer shortcuts below also fire (e.g. Enter opening the item a second time).
        if (PeekOverlay.Visibility == Visibility.Visible) return;

        switch (e.Key)
        {
            case VirtualKey.F5:
                if (ExplorerView.Visibility == Visibility.Visible) LoadCurrentFolder();
                else StartSlideshow();
                e.Handled = true; break;
            case VirtualKey.Delete when ExplorerView.Visibility == Visibility.Visible:
                _ = DeleteSelectedExplorerAsync(); e.Handled = true; break;

            // ---- Explorer clipboard / selection shortcuts (skip while typing in a text field) ----
            case VirtualKey.C when ExplorerView.Visibility == Visibility.Visible && IsCtrlDown() && !IsTextInputFocused():
                _ = CopySelectedExplorerAsync(cut: false); e.Handled = true; break;
            case VirtualKey.X when ExplorerView.Visibility == Visibility.Visible && IsCtrlDown() && !IsTextInputFocused():
                _ = CopySelectedExplorerAsync(cut: true); e.Handled = true; break;
            case VirtualKey.V when ExplorerView.Visibility == Visibility.Visible && IsCtrlDown() && !IsTextInputFocused():
                _ = PasteIntoCurrentAsync(); e.Handled = true; break;
            case VirtualKey.A when ExplorerView.Visibility == Visibility.Visible && IsCtrlDown() && !IsTextInputFocused():
                ActiveExplorerList().SelectAll(); e.Handled = true; break;
            case VirtualKey.F2 when ExplorerView.Visibility == Visibility.Visible && !IsTextInputFocused():
            {
                var sel = SelectedExplorerItems();
                var primary = FocusedExplorerItem() ?? sel.FirstOrDefault();
                if (sel.Count > 1 && primary is not null) _ = BulkRenameExplorerAsync(primary, sel);
                else if (sel.Count > 0) _ = RenameExplorerAsync(sel[0]);
                e.Handled = true; break;
            }
            case VirtualKey.Enter when ExplorerView.Visibility == Visibility.Visible && !IsTextInputFocused():
            {
                var sel = SelectedExplorerItems();
                if (sel.Count > 0) OpenExplorerItem(sel[0]);
                e.Handled = true; break;
            }

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
                CancelSettings(); e.Handled = true; break;
            case VirtualKey.Escape when InCollage:
                ShowExplorer(); e.Handled = true; break;
            case VirtualKey.Escape when InViewer:
                if (_isFullScreen) ToggleFullScreen(); else ShowExplorer();
                e.Handled = true; break;
            case VirtualKey.F11:
                ToggleFullScreen(); e.Handled = true; break;
            case VirtualKey.F when InViewer: // 'f' elsewhere is just typing (e.g. the address bar)
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

    // ===================== Peek (Quick Look) =====================

    /// <summary>Extensions previewed as plain text/code in the Peek overlay.</summary>
    private static readonly HashSet<string> PeekTextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".log", ".csv", ".tsv", ".json", ".xml", ".yaml", ".yml",
        ".ini", ".cfg", ".conf", ".config", ".toml", ".env", ".gitignore", ".gitattributes",
        ".cs", ".xaml", ".js", ".ts", ".jsx", ".tsx", ".html", ".htm", ".css", ".scss",
        ".py", ".java", ".c", ".cpp", ".h", ".hpp", ".go", ".rs", ".rb", ".php", ".sql",
        ".sh", ".bat", ".cmd", ".ps1", ".psm1", ".bib", ".tex"
    };

    private static bool IsTextPreviewable(string path) =>
        PeekTextExtensions.Contains(System.IO.Path.GetExtension(path));

    /// <summary>The explorer item under keyboard focus (the row the user is "on"), or the
    /// selected item if focus can't be resolved.</summary>
    private ExplorerItem? FocusedExplorerItem()
    {
        var node = FocusManager.GetFocusedElement(RootGrid.XamlRoot) as DependencyObject;
        while (node is not null)
        {
            if (node is FrameworkElement { DataContext: ExplorerItem item }) return item;
            node = VisualTreeHelper.GetParent(node);
        }
        return SelectedExplorerItems().FirstOrDefault();
    }

    /// <summary>Global key hook (handledEventsToo): Space opens Peek; while open, Space/Esc close
    /// it and the arrows step through the folder.</summary>
    private void Peek_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        ResetVaultIdle(); // keyboard counts as activity for the vault idle timer

        if (PeekOverlay.Visibility == Visibility.Visible)
        {
            switch (e.Key)
            {
                case VirtualKey.Space:
                case VirtualKey.Escape:
                    ClosePeek(); e.Handled = true; break;
                case VirtualKey.Left:
                case VirtualKey.Up:
                    PeekNavigate(-1); e.Handled = true; break;
                case VirtualKey.Right:
                case VirtualKey.Down:
                    PeekNavigate(+1); e.Handled = true; break;
                case VirtualKey.Enter:
                {
                    var cur = _peekItem;
                    ClosePeek();
                    if (cur is not null) OpenExplorerItem(cur);
                    e.Handled = true; break;
                }
            }
            return;
        }

        if (e.Key == VirtualKey.Space
            && ExplorerView.Visibility == Visibility.Visible
            && _state.PeekEnabled
            && !IsTextInputFocused())
        {
            var item = FocusedExplorerItem();
            if (item is not null && item.Kind != ExplorerItemKind.Drive)
            {
                OpenPeek(item);
                e.Handled = true;
            }
        }
    }

    private void OpenPeek(ExplorerItem item)
    {
        // Anchor the selection so arrow navigation has a starting index, then take focus off the
        // list (onto the overlay) so arrows drive Peek rather than moving the list underneath.
        ActiveExplorerList().SelectedItem = item;
        PeekOverlay.Visibility = Visibility.Visible;
        PeekOverlay.Focus(FocusState.Programmatic);
        ShowPeekFor(item);
    }

    private void ClosePeek()
    {
        if (PeekOverlay.Visibility != Visibility.Visible) return;
        _peekToken++;            // cancel any in-flight load
        StopPeekVideo();
        PeekImage.Source = null;
        PeekOverlay.Visibility = Visibility.Collapsed;
        _peekItem = null;
        ActiveExplorerList().Focus(FocusState.Programmatic); // hand focus back for continued nav
    }

    private void PeekNavigate(int delta)
    {
        var list = ActiveExplorerList();
        var count = list.Items.Count;
        if (count == 0) return;
        var cur = list.SelectedIndex;
        if (cur < 0) cur = _peekItem is not null ? list.Items.IndexOf(_peekItem) : 0;
        var next = Math.Clamp(cur + delta, 0, count - 1);
        if (next == cur && list.Items[next] == _peekItem) return;
        list.SelectedIndex = next;
        list.ScrollIntoView(list.Items[next]);
        if (list.Items[next] is ExplorerItem it) ShowPeekFor(it);
    }

    private async void ShowPeekFor(ExplorerItem item)
    {
        var token = ++_peekToken;
        _peekItem = item;
        PeekTitle.Text = item.Name;
        PeekInfo.Text = BuildPeekInfo(item);

        // Reset every content surface and release any playing video before loading the next item.
        StopPeekVideo();
        PeekImage.Source = null;
        PeekImage.Visibility = Visibility.Collapsed;
        PeekVideo.Visibility = Visibility.Collapsed;
        PeekTextScroller.Visibility = Visibility.Collapsed;
        PeekFallback.Visibility = Visibility.Collapsed;

        try
        {
            if (item.Kind == ExplorerItemKind.File && PhotoLibrary.IsSupported(item.Path))
            {
                var bmp = new BitmapImage();
                using (var s = await (await StorageFile.GetFileFromPathAsync(item.Path)).OpenReadAsync())
                {
                    if (token != _peekToken) return;
                    await bmp.SetSourceAsync(s);
                }
                if (token != _peekToken) return;
                PeekImage.Source = bmp;
                PeekImage.Visibility = Visibility.Visible;
            }
            else if (item.Kind == ExplorerItemKind.File && PhotoLibrary.IsMedia(item.Path))
            {
                var file = await StorageFile.GetFileFromPathAsync(item.Path);
                if (token != _peekToken) return;
                PeekVideo.Source = MediaSource.CreateFromStorageFile(file);
                PeekVideo.Visibility = Visibility.Visible;
                if (PeekVideo.MediaPlayer is not null)
                    PeekVideo.MediaPlayer.AudioCategory = Windows.Media.Playback.MediaPlayerAudioCategory.Movie;
                PeekVideo.MediaPlayer?.Play();
            }
            else if (item.Kind == ExplorerItemKind.File && IsTextPreviewable(item.Path))
            {
                var text = await ReadTextPreviewAsync(item.Path);
                if (token != _peekToken) return;
                PeekText.Text = text;
                PeekTextScroller.ChangeView(0, 0, null, true);
                PeekTextScroller.Visibility = Visibility.Visible;
            }
            else
            {
                await ShowPeekFallbackAsync(item, token);
            }
        }
        catch (Exception ex)
        {
            if (token != _peekToken) return;
            PeekFallbackImage.Source = null;
            PeekFallbackText.Text = $"Can't preview this file.\n{ex.Message}";
            PeekFallback.Visibility = Visibility.Visible;
        }
    }

    private async Task ShowPeekFallbackAsync(ExplorerItem item, int token)
    {
        PeekFallbackText.Text = string.IsNullOrEmpty(item.TypeName) ? "No preview available" : item.TypeName;
        PeekFallback.Visibility = Visibility.Visible;

        var (pixels, w, h) = await Task.Run(() => ShellImaging.GetPixels(item.Path, 256, iconOnly: false));
        if (pixels is null) (pixels, w, h) = await Task.Run(() => ShellImaging.GetPixels(item.Path, 256, iconOnly: true));
        if (token != _peekToken || pixels is null || w <= 0 || h <= 0) return;

        var wb = new WriteableBitmap(w, h);
        using (var st = wb.PixelBuffer.AsStream()) st.Write(pixels, 0, pixels.Length);
        PeekFallbackImage.Source = wb;
        PeekFallbackImage.Width = Math.Min(256, w);
        PeekFallbackImage.Height = Math.Min(256, h);
    }

    private static async Task<string> ReadTextPreviewAsync(string path)
    {
        const int maxBytes = 256 * 1024; // cap so a huge log doesn't freeze the UI
        var file = await StorageFile.GetFileFromPathAsync(path);
        using var stream = await file.OpenStreamForReadAsync();
        var len = (int)Math.Min(stream.Length, maxBytes);
        var buf = new byte[len];
        var read = await stream.ReadAsync(buf, 0, len);
        var text = System.Text.Encoding.UTF8.GetString(buf, 0, read);
        if (stream.Length > maxBytes) text += "\n\n… (truncated preview)";
        return text;
    }

    private static string BuildPeekInfo(ExplorerItem item)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(item.TypeName)) parts.Add(item.TypeName);
        if (item.Kind == ExplorerItemKind.File && !string.IsNullOrEmpty(item.SizeText)) parts.Add(item.SizeText);
        if (!string.IsNullOrEmpty(item.ModifiedText)) parts.Add(item.ModifiedText);
        return string.Join("   ·   ", parts);
    }

    private void StopPeekVideo()
    {
        try
        {
            PeekVideo.MediaPlayer?.Pause();
            var previous = PeekVideo.Source as MediaSource;
            PeekVideo.Source = null;
            previous?.Dispose();
        }
        catch { /* ignore */ }
    }

    private void PeekScrim_Tapped(object sender, TappedRoutedEventArgs e) => ClosePeek();
    private void PeekCard_Tapped(object sender, TappedRoutedEventArgs e) => e.Handled = true; // keep clicks on the card from closing
    private void PeekClose_Click(object sender, RoutedEventArgs e) => ClosePeek();

    private void PeekOpen_Click(object sender, RoutedEventArgs e)
    {
        var cur = _peekItem;
        ClosePeek();
        if (cur is not null) OpenExplorerItem(cur);
    }

    // ===================== Secure vault =====================

    private void RefreshVaults()
    {
        _vaultList.Clear();
        foreach (var v in _vaults.List())
            _vaultList.Add(new Models.VaultInfo(v.Id, v.Name, _vaults.Current?.Id == v.Id));

        // The vault list only appears once something is unlocked; otherwise a single discreet entry.
        var open = _vaults.IsAnyUnlocked;
        VaultsSection.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        VaultsLockedEntry.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
        UpdateVaultLockButton();
    }

    private async void VaultsLockedEntry_Click(object sender, RoutedEventArgs e) => await ShowVaultPickerAsync();

    /// <summary>Locked-state entry point: lists vaults to unlock (or create one) without revealing
    /// them in the sidebar.</summary>
    private async Task ShowVaultPickerAsync()
    {
        var vaults = _vaults.List();
        var panel = new StackPanel { Spacing = 10, MinWidth = 280 };

        ListView? list = null;
        if (vaults.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "No vaults yet. Create one to get started.",
                Opacity = 0.7, TextWrapping = TextWrapping.Wrap,
            });
        }
        else
        {
            list = new ListView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 300, ItemsSource = vaults, DisplayMemberPath = "Name" };
            list.SelectedIndex = 0;
            panel.Children.Add(list);
        }

        var dlg = new ContentDialog
        {
            Title = "Vaults",
            Content = panel,
            PrimaryButtonText = vaults.Count > 0 ? "Unlock" : null,
            SecondaryButtonText = "New vault…",
            CloseButtonText = "Cancel",
            DefaultButton = vaults.Count > 0 ? ContentDialogButton.Primary : ContentDialogButton.Secondary,
            XamlRoot = RootGrid.XamlRoot,
        };

        Vault? chosen = null;
        if (list is not null)
        {
            list.DoubleTapped += (_, _) => { if (list.SelectedItem is Vault) { chosen = (Vault)list.SelectedItem; dlg.Hide(); } };
            dlg.PrimaryButtonClick += (_, args) =>
            {
                if (list.SelectedItem is Vault v) chosen = v; else args.Cancel = true;
            };
        }

        var result = await dlg.ShowAsync();
        if (chosen is not null) { await TryUnlockVaultAsync(chosen); return; }
        if (result == ContentDialogResult.Secondary) await CreateVaultDialogAsync(null);
    }

    private async void VaultsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not Models.VaultInfo vi) return;

        // Already unlocked → just browse its decrypted working folder.
        if (_vaults.Current?.Id == vi.Id && _vaults.Current.WorkingDir is not null)
        {
            ShowExplorer();
            NavigateTo(_vaults.Current.WorkingDir);
            return;
        }

        Vault v;
        try { v = Vault.Load(System.IO.Path.Combine(VaultManager.VaultsRoot, vi.Id)); }
        catch (Exception ex) { StatusText.Text = "Couldn't open vault: " + ex.Message; return; }
        await TryUnlockVaultAsync(v);
    }

    private async void NewVault_Click(object sender, RoutedEventArgs e) => await CreateVaultDialogAsync(null);

    private void VaultsList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not Models.VaultInfo vi) return;
        var menu = new MenuFlyout();

        if (_vaults.Current?.Id == vi.Id)
        {
            var lockItem = new MenuFlyoutItem
            {
                Text = "Lock",
                Icon = new FontIcon { Glyph = char.ConvertFromUtf32(0xE72E), FontFamily = new FontFamily("Segoe Fluent Icons") },
            };
            lockItem.Click += (_, _) => _ = LockActiveVaultAsync();
            menu.Items.Add(lockItem);
        }

        var rename = new MenuFlyoutItem { Text = "Rename…", Icon = new SymbolIcon(Symbol.Rename) };
        rename.Click += async (_, _) => await RenameVaultAsync(vi);
        menu.Items.Add(rename);

        if (GoogleDriveBackup.IsConfigured)
        {
            var backup = new MenuFlyoutItem
            {
                Text = "Back up to Google Drive",
                Icon = new FontIcon { Glyph = char.ConvertFromUtf32(0xE753), FontFamily = new FontFamily("Segoe Fluent Icons") },
            };
            backup.Click += async (_, _) => await BackupSingleVaultAsync(vi.Id);
            menu.Items.Add(backup);
        }

        var target = (FrameworkElement)sender;
        menu.ShowAt(target, new FlyoutShowOptions { Position = e.GetPosition(target) });
        e.Handled = true;
    }

    private async Task RenameVaultAsync(Models.VaultInfo vi)
    {
        var box = new TextBox { Text = vi.Name, PlaceholderText = "Vault name" };
        box.Loaded += (_, _) => box.SelectAll();
        var dlg = new ContentDialog
        {
            Title = "Rename vault",
            Content = box,
            PrimaryButtonText = "Rename",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot,
        };
        dlg.PrimaryButtonClick += (_, args) => { if (string.IsNullOrWhiteSpace(box.Text)) args.Cancel = true; };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        var newName = box.Text.Trim();
        if (newName == vi.Name) return;
        try
        {
            // Use the live unlocked instance if it's the same vault, so its in-memory name updates too.
            var v = _vaults.Current?.Id == vi.Id
                ? _vaults.Current
                : Vault.Load(System.IO.Path.Combine(VaultManager.VaultsRoot, vi.Id));
            v.Rename(newName);
        }
        catch (Exception ex) { StatusText.Text = "Rename failed: " + ex.Message; return; }

        RefreshVaults();
        UpdateVaultLockButton();
        StatusText.Text = $"Vault renamed to “{newName}”.";
    }

    private async Task TryUnlockVaultAsync(Vault v)
    {
        var pw = new PasswordBox { PlaceholderText = "Passphrase" };
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = $"Unlock “{v.Name}”",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        panel.Children.Add(pw);

        var dlg = new ContentDialog
        {
            Title = "Unlock vault",
            Content = panel,
            PrimaryButtonText = "Unlock",
            SecondaryButtonText = v.HasHello ? "Windows Hello" : null,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot,
        };

        var result = await dlg.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            StatusText.Text = "Unlocking…";
            VaultUnlockOutcome outcome;
            try { outcome = await _vaults.UnlockWithPassphraseAsync(v, pw.Password, _state.VaultWipeOnFailure, _state.VaultWipeAfterAttempts); }
            catch (Exception ex) { StatusText.Text = "Unlock failed: " + ex.Message; return; }

            switch (outcome)
            {
                case VaultUnlockOutcome.Success:
                    OnVaultOpened(v);
                    break;
                case VaultUnlockOutcome.Wiped:
                    RefreshVaults();
                    await new ContentDialog
                    {
                        Title = "Vault wiped",
                        Content = $"“{v.Name}” was permanently erased after {_state.VaultWipeAfterAttempts} incorrect attempts.",
                        CloseButtonText = "OK",
                        XamlRoot = RootGrid.XamlRoot,
                    }.ShowAsync();
                    break;
                default:
                    if (_state.VaultWipeOnFailure)
                    {
                        var left = Math.Max(0, _state.VaultWipeAfterAttempts - v.FailedAttempts);
                        StatusText.Text = $"Wrong passphrase — {left} attempt(s) left before this vault is wiped.";
                    }
                    else StatusText.Text = "Wrong passphrase.";
                    break;
            }
        }
        else if (result == ContentDialogResult.Secondary)
        {
            StatusText.Text = "Waiting for Windows Hello…";
            bool ok;
            try { ok = await _vaults.UnlockWithHelloAsync(v); } catch { ok = false; }
            if (ok) OnVaultOpened(v); else StatusText.Text = "Windows Hello unlock failed.";
        }
    }

    private void OnVaultOpened(Vault v)
    {
        RefreshVaults();
        ResetVaultIdle();
        ShowExplorer();
        NavigateTo(v.WorkingDir);
        StatusText.Text = $"Vault “{v.Name}” unlocked";
    }

    private async Task CreateVaultDialogAsync(IList<string>? importPaths)
    {
        var suggested = importPaths is { Count: 1 }
            ? System.IO.Path.GetFileName(importPaths[0].TrimEnd('\\', '/'))
            : "";
        var name = new TextBox { PlaceholderText = "Vault name", Text = suggested };
        var pw = new PasswordBox { PlaceholderText = "Passphrase (min 8 characters)" };
        var pw2 = new PasswordBox { PlaceholderText = "Confirm passphrase" };
        var hello = new CheckBox { Content = "Also unlock with Windows Hello", IsChecked = _state.VaultDefaultUseHello };
        var error = new TextBlock
        {
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.IndianRed),
            Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap, FontSize = 12,
        };
        var hint = new TextBlock
        {
            Text = "Your passphrase is the only recovery key — there is no reset. Hello is an optional convenience.",
            Opacity = 0.6, FontSize = 12, TextWrapping = TextWrapping.Wrap,
        };
        // Passphrase strength meter (red → green bars + label), live as the user types.
        var bars = new Border[4];
        var barRow = new Grid { Height = 6, Margin = new Thickness(0, 2, 0, 0) };
        Brush Neutral() => new SolidColorBrush(Microsoft.UI.Colors.Gray) { Opacity = 0.25 };
        for (var i = 0; i < 4; i++)
        {
            barRow.ColumnDefinitions.Add(new ColumnDefinition());
            var b = new Border { CornerRadius = new CornerRadius(3), Margin = new Thickness(i == 0 ? 0 : 4, 0, 0, 0), Background = Neutral() };
            Grid.SetColumn(b, i);
            barRow.Children.Add(b);
            bars[i] = b;
        }
        var strengthLabel = new TextBlock { FontSize = 12, Opacity = 0.85 };
        void UpdateStrength()
        {
            if (pw.Password.Length == 0)
            {
                for (var i = 0; i < 4; i++) bars[i].Background = Neutral();
                strengthLabel.Text = "";
                return;
            }
            var (score, label, color) = EvaluatePassphrase(pw.Password);
            var brush = new SolidColorBrush(color);
            for (var i = 0; i < 4; i++) bars[i].Background = i < score ? brush : Neutral();
            strengthLabel.Text = label;
            strengthLabel.Foreground = brush;
        }
        pw.PasswordChanged += (_, _) => UpdateStrength();
        UpdateStrength();

        var panel = new StackPanel { Spacing = 10 };
        foreach (var c in new UIElement[] { name, pw, barRow, strengthLabel, pw2, hello, hint, error }) panel.Children.Add(c);

        var dlg = new ContentDialog
        {
            Title = importPaths is null ? "New vault" : "Move to new vault",
            Content = panel,
            PrimaryButtonText = importPaths is null ? "Create" : "Move",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot,
        };
        dlg.PrimaryButtonClick += (_, args) =>
        {
            void Fail(string m) { error.Text = m; error.Visibility = Visibility.Visible; args.Cancel = true; }
            if (string.IsNullOrWhiteSpace(name.Text)) { Fail("Enter a vault name."); return; }
            if (pw.Password.Length < 8) { Fail("Use a passphrase of at least 8 characters."); return; }
            if (pw.Password != pw2.Password) { Fail("Passphrases don't match."); return; }
        };

        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        StatusText.Text = "Encrypting… this can take a moment for large folders.";
        try
        {
            await _vaults.CreateAsync(name.Text.Trim(), pw.Password, hello.IsChecked == true, importPaths);
        }
        catch (Exception ex) { StatusText.Text = "Vault creation failed: " + ex.Message; App.Log("VaultCreate", ex); return; }

        RefreshVaults();
        if (importPaths is not null) LoadCurrentFolder(); // originals were removed
        StatusText.Text = "Vault created.";
    }

    private bool ItemInsideOpenVault(ExplorerItem item) =>
        _vaults.Current?.WorkingDir is { } w
        && item.Path.StartsWith(w, StringComparison.OrdinalIgnoreCase);

    /// <summary>Encrypts the selected items into the open vault now (durable blob + visible in the
    /// working folder) and securely wipes the clear-space originals.</summary>
    private async Task SendToVaultAsync(IList<ExplorerItem> items)
    {
        var cur = _vaults.Current;
        if (cur?.WorkingDir is not { } work) { StatusText.Text = "Unlock a vault first."; return; }

        var paths = items.Select(i => i.Path)
            .Where(p => !string.IsNullOrEmpty(p) && !p.StartsWith(work, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (paths.Count == 0) return;

        StatusText.Text = "Encrypting into vault…";
        int n;
        try { n = await _vaults.AddToCurrentAsync(paths); }
        catch (Exception ex) { StatusText.Text = "Send to vault failed: " + ex.Message; App.Log("SendToVault", ex); return; }

        LoadCurrentFolder(); // originals are gone; new items appear if browsing the vault
        ResetVaultIdle();
        StatusText.Text = $"Sent {n} item(s) to vault “{cur.Name}” — encrypted, originals wiped.";
    }

    /// <summary>Scores a passphrase 1–4 from length + character-class variety, with a label and a
    /// red→green colour for the strength meter.</summary>
    private static (int score, string label, Windows.UI.Color color) EvaluatePassphrase(string pw)
    {
        var score = 0;
        if (pw.Length >= 8) score++;
        if (pw.Length >= 12) score++;
        if (pw.Length >= 16) score++;
        var classes = 0;
        if (pw.Any(char.IsLower)) classes++;
        if (pw.Any(char.IsUpper)) classes++;
        if (pw.Any(char.IsDigit)) classes++;
        if (pw.Any(c => !char.IsLetterOrDigit(c))) classes++;
        if (classes >= 2) score++;
        if (classes >= 3) score++;
        score = Math.Clamp(score, 0, 4);

        static Windows.UI.Color C(byte r, byte g, byte b) => Windows.UI.Color.FromArgb(255, r, g, b);
        return score switch
        {
            <= 1 => (1, "Weak", C(0xE7, 0x4C, 0x3C)),   // red
            2 => (2, "Fair", C(0xE6, 0x7E, 0x22)),       // orange
            3 => (3, "Good", C(0xF1, 0xC4, 0x0F)),       // amber
            _ => (4, "Strong", C(0x2E, 0xCC, 0x71)),     // green
        };
    }

    /// <summary>Creates a new vault from the selected items (encrypt + securely remove originals).</summary>
    private async Task MoveToNewVaultAsync(IList<ExplorerItem> items)
    {
        var paths = items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
        if (paths.Count == 0) return;
        await CreateVaultDialogAsync(paths);
    }

    private void VaultLock_Click(object sender, RoutedEventArgs e) => _ = LockActiveVaultAsync();

    private async Task LockActiveVaultAsync()
    {
        var work = _vaults.Current?.WorkingDir;
        StopVaultIdle();
        try { await _vaults.LockCurrentAsync(); }
        catch (Exception ex) { StatusText.Text = "Lock failed: " + ex.Message; App.Log("VaultLock", ex); }
        RefreshVaults();

        // If we were browsing/viewing inside the vault, leave it (its folder is now wiped).
        if (work is not null && (_currentFolder?.StartsWith(work, StringComparison.OrdinalIgnoreCase) ?? false))
        {
            if (InViewer || InCollage) ShowExplorer();
            NavigateTo(null);
        }
        StatusText.Text = "Vault locked.";
    }

    private void UpdateVaultLockButton()
    {
        var open = _vaults.IsAnyUnlocked;
        VaultLockBtn.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        VaultLockText.Text = open && _vaults.Current is not null ? $"Lock “{_vaults.Current.Name}”" : "Lock vault";
    }

    // ---- Idle auto-lock ----

    private void ResetVaultIdle()
    {
        if (!_vaults.IsAnyUnlocked) return;
        var secs = _state.VaultIdleSeconds;
        _vaultIdleTimer.Stop();
        if (secs <= 0) return; // 0 = never auto-lock
        _vaultIdleTimer.Interval = TimeSpan.FromSeconds(secs);
        _vaultIdleTimer.Start();
    }

    private void StopVaultIdle() => _vaultIdleTimer.Stop();

    private void VaultIdle_Tick(object? sender, object e)
    {
        _vaultIdleTimer.Stop();
        if (_vaults.IsAnyUnlocked) _ = LockActiveVaultAsync();
    }

    // ---- App-exit lock (re-encrypt + wipe before the window closes) ----

    private async void AppWindow_Closing(Microsoft.UI.Windowing.AppWindow sender,
        Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        try { _term?.Dispose(); _term = null; } catch { } // kill any terminal shell on close
        StopFolderWatch();
        if (_closingForVaultLock) return;       // second pass: let the close proceed
        if (!_vaults.IsAnyUnlocked) return;
        args.Cancel = true;                      // defer close until the vault is secured
        try { await _vaults.LockCurrentAsync(); } catch (Exception ex) { App.Log("VaultCloseLock", ex); }
        _closingForVaultLock = true;
        Close();
    }
}
