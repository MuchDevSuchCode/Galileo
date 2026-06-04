# Galileo

A modern, native **Windows Explorer + Photos** alternative — built with **WinUI 3 / .NET 8**. Galileo is a fast, local-first file manager and photo viewer with a clean Fluent UI.

> **Branding note:** the user-facing app and the built executable are **Galileo** (`Galileo.exe`, set via `<AssemblyName>`). Internal identifiers (the `PhotosPlus` namespace, the `%LocalAppData%\PhotosPlus` data folder, the registered ProgID) keep the `PhotosPlus` name to keep the build and default-app registration stable.

Highlights over the stock apps:

1. **👁 Eye toggle** — a one-click eye icon (shortcut **H**) that instantly **blacks out the photo in the viewer** for privacy, plus an optional **Hidden album** for photos you want kept out of the gallery.
2. **▶ Slideshow** — a full-screen, configurable slideshow with adjustable timing, shuffle, loop, and transitions.

> **Status:** working application. The file explorer, photo viewer, gallery, collage, embedded **video player**, eye toggle, slideshow, a full **Settings** panel (5 themes, sort/group, and more), and default-photo-app registration are all implemented and building. Image editing remains on the roadmap — see **[tasks.md](./tasks.md)**.

---

## Why Galileo

Windows Photos and File Explorer are capable but cluttered and increasingly cloud-driven. Galileo is a **fast, local-first, privacy-respecting** file manager and viewer:

- **Local-first** — no account, no cloud sync; your library stays on disk.
- **Fast** — GPU-composited viewer, async/virtualized thumbnails.
- **Native** — Win11 Fluent, Mica backdrop, extended title bar, dark/light theme.
- **Private** — the eye toggle and Hidden album make it trivial to conceal sensitive images. Hidden state lives in app data; original files are never modified.

---

## File Explorer (home)

Galileo opens into a **Windows-Explorer-style file manager** (Win11 layout):

- **Sidebar** — Home (This PC), Quick access (Desktop/Downloads/Documents/Pictures/Music/Videos), and drives.
- **Navigation** — back / forward / up, a clickable **breadcrumb**, and an editable **address bar** (pencil button or type a path + Enter). **Backspace** goes back; **F5** refreshes.
- **Views** — Large / Medium / Small icons with a **size slider**, plus a **Details** view (Name · Date modified · Type · Size). Real shell thumbnails/icons for every file type, with optional **folder content previews** (the first image painted onto the folder icon).
- **Sort & Group** — sort by Name / Date modified / Type / Size (ascending or descending) and **group** by the same keys, mirroring Explorer's defaults. Saved across sessions.
- **Show / hide file extensions** — toggle in Settings (on by default); affects display only — the real filename is preserved for rename, copy, and open.
- **Open** — folders navigate in; images open in the photo viewer; **videos open in the embedded player**; other files open in their default app. **Slideshow** and **Collage** buttons act on the current folder's images. Single- or double-click to open (configurable).
- **File operations** — New folder (with immediate rename), Copy, Copy path, Paste, Rename, Delete (Recycle Bin), **Shift+Delete** (permanent), drag files out to other apps, and the native **Properties** dialog (right-click items or empty space).
- **⭐ Hide folder** — the **Hide folder** button (or a folder's right-click) makes a folder **appear empty when opened** and excludes it from its parent. Toggle **Show app-hidden** to reveal hidden folders (dimmed); **Unhide** to restore. App-only and reversible — the folder on disk is never modified.

> Planned next: cut/move, drag-and-drop *between* folders, click-to-sort column headers in Details, search, and an expandable folder tree in the sidebar.

---

## Features (implemented)

**Viewing**
- Open a **file**, a **folder**, or **drag-and-drop** onto the window (single image → opens in viewer; multiple images → builds a gallery; folder → loads it).
- Virtualized gallery grid with async thumbnails; favorite (★) and hidden badges.
- Full-bleed single-image viewer that **scales any photo to fit the window** (up or down), with:
  - **Mouse-wheel zoom** toward the cursor (no modifier), plus +/- buttons and double-tap.
  - **Drag to pan** when zoomed in.
  - **Rotate** (auto re-fits and re-centres so the rotated image stays fully visible).
  - Fit / next / previous / full screen.
- Remembers and reopens your last folder.

**Formats** — JPEG, PNG, GIF, BMP, TIFF, WEBP, HEIC/HEIF, AVIF, and common RAW (CR2/CR3, NEF, ARW, DNG…) decoded via the platform `Windows.Storage` / `BitmapImage` codecs (RAW/HEIC depend on the OS codec being installed).

**Organize & act** — Favorites (★) with a "Favorites only" filter; per-photo metadata panel (dimensions, size, dates, camera); delete to Recycle Bin; reveal in Explorer.

**Right-click menu** (on the viewer image *and* gallery thumbnails) — Copy (image to clipboard), Copy as file, Copy file path, Open with…, Print…, Set as desktop background, Favorite, Hide, Rename…, Show in Explorer, Delete, and the native Windows **Properties** dialog.

**Collage** — a **Collage** button builds an auto-arranged collage that fills the screen.
- **Layout presets:** **Justified** (aspect-preserving rows, fit to screen), **Grid** (uniform cropped cells), **Hero** (one big image + the rest justified beside/below).
- **Choose what's in it:** *Select photos* in the gallery's More menu to hand-pick images, or **drag-and-drop** image files onto an open collage to add them.
- **Shuffle** re-arranges to a fresh fit; a **− N +** stepper sets how many photos; **Save** exports to PNG; clicking a tile opens it in the viewer. Re-fits on window resize.

**Video** — an **embedded video player** complements the image viewer. Single-click the file (or it opens automatically from the explorer) to play MP4/M4V/MOV/MKV/AVI/WMV/WEBM and more, with **single-click mute/unmute** and a **repeat** toggle. A back bar returns to the explorer.

**Settings** — a Settings panel (gear in the title bar / command strip) with:
- **Theme** — System, Light, Dark, **Terminal (green)**, or **Gray**.
- **Open items with** — double-click (default) or single-click.
- **Default icon size** — Small / Medium / Large.
- **Folder content previews** — on/off.
- **Show file extensions** — on/off (on by default).
- **Default collage layout** — Justified / Grid / Hero.
- **Slideshow** — seconds per photo (2–30 s), shuffle, loop, transition.

All settings persist across sessions (`%LocalAppData%\PhotosPlus\state.json`). The panel header stays pinned and the body scrolls, so it never clips on small windows.

### ✨ The two headline features

#### 1. Eye toggle — hide / un-hide the current photo
- **Black-out (default):** the eye icon (or **H**) instantly covers the current photo with a solid black curtain — a glance over your shoulder reveals nothing. Press again to reveal. The image is never moved or deleted.
- **Hidden album (persistent):** the eye button's flyout → *Hide permanently* flags the photo so it's excluded from the gallery and slideshows, and collected into a **Hidden album** (toggle *Show Hidden album* in the gallery's More menu).
- Hidden/favorite state is stored as JSON in `%LocalAppData%\PhotosPlus`, never by altering originals.

#### 2. Slideshow
- Launches from the toolbar or **F5**; full-screen on the active monitor.
- **Per-slide duration** (2–30 s, set in Settings), **shuffle**, **loop**.
- **Transitions:** none, crossfade, **Ken Burns** (slow zoom/pan).
- Auto-hiding controls; caption (filename + position).
- Controls: play/pause (**Space**), prev/next (**←/→**), speed (**↑/↓**), exit (**Esc**).
- **Hidden photos are skipped**, so the eye toggle and slideshow cooperate.

---

## Modern UI

- **Mica** backdrop and **extended title bar** for a seamless Win11 look — no chunky command bar.
- **Floating, auto-hiding controls:** a translucent pill toolbar in the gallery; back / actions / nav-zoom pills in the viewer that fade out after a few seconds of inactivity.
- Rounded thumbnails, a proper empty-state, dark/light aware.

---

## Tech Stack

- **UI:** WinUI 3 (Windows App SDK **1.6**), Fluent Design, Mica backdrop. Unpackaged, self-contained desktop app.
- **Runtime:** .NET 8, C# 12.
- **Imaging:** `Windows.Storage` thumbnails + `BitmapImage`; GPU-composited transform-based viewer (zoom/pan/rotate via `CompositeTransform`).
- **MVVM:** CommunityToolkit.Mvvm (observable `PhotoItem`).
- **Storage:** JSON app-state (`%LocalAppData%\PhotosPlus\state.json`) for hidden/favorite flags and slideshow settings.

### Project layout (current)

```
PhotosPlus/
├─ global.json                 # pins .NET SDK 8.0.300
├─ src/
│  └─ PhotosPlus.App/          # WinUI 3 app (single project; builds Galileo.exe)
│     ├─ App.xaml(.cs)         # app + shared resources (GlyphButton, PillBrush, explorer templates)
│     ├─ MainWindow.xaml(.cs)  # explorer + gallery + viewer + video + collage + settings + title bar
│     ├─ SlideshowWindow.xaml(.cs)
│     ├─ Models/               # PhotoItem, ExplorerItem, ExplorerGroup
│     ├─ Services/             # AppState, PhotoLibrary, FileSystemService,
│     │                        #   ShellImaging (icons via IShellItemImageFactory + GetDIBits),
│     │                        #   ShellOps (clipboard / properties / wallpaper), ImageCompositor, CollageLayout
│     ├─ Converters/           # BoolToVisibilityConverter
│     └─ Assets/               # galileo.ico / galileo.png (app + taskbar icon)
├─ tools/                      # install.ps1, register-default.ps1, unregister-default.ps1
├─ README.md
└─ tasks.md
```

---

## Getting Started

**Prerequisites**
- Windows 11
- .NET SDK 8 (`winget install Microsoft.DotNet.SDK.8`) — repo pins `8.0.300` via `global.json`.

**Build & run**

```powershell
dotnet build src/PhotosPlus.App
# then run the produced exe, or:
dotnet run --project src/PhotosPlus.App
```

The build produces **`Galileo.exe`** under `src/PhotosPlus.App/bin/Debug/net8.0-windows10.0.19041.0/win-x64/`.

> ⚠️ Close any running Galileo window before rebuilding — Windows locks the `.exe` while it runs (otherwise the build fails with an `MSB3021` file-lock error). PowerShell: `Get-Process Galileo -ErrorAction SilentlyContinue | Stop-Process -Force`.

**Build notes (already configured in the `.csproj`):**
- `<WindowsSdkPackageVersion>10.0.19041.38</WindowsSdkPackageVersion>` — Windows App SDK 1.6 requires SDK.NET.Ref ≥ `.38`; the .NET 8.0.300 SDK ships `.31`.
- `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` — the CsWinRT AOT source generator emits unsafe code for generic WinRT calls (e.g. drag-drop's `GetStorageItemsAsync`).
- Shared XAML styles live in **`App.xaml`**, not `Window.Resources` — the WinUI 1.6 markup compiler crashes on `Style` defined in `Window.Resources`.

---

## Set as your default photo app

Galileo opens a file or folder passed on the command line (`Galileo.exe "<file>"`), so it works as a Windows file handler.

1. **Install (and update).** One command publishes a stable, self-contained copy to
   `%LocalAppData%\PhotosPlus\app` and registers it with Windows (per-user, no admin, reversible):
   ```powershell
   .\tools\install.ps1
   ```
   Re-run `install.ps1` any time to push your latest code to the installed copy — it stops the
   running app, re-publishes, and re-registers. (Registration adds a ProgID, an *Open with*
   entry, and a *Default apps* capability for ~23 image extensions.)
2. **Make it the default.** Windows 10/11 doesn't let an app silently take over defaults, so do it once:
   - **Settings → Apps → Default apps** → pick Galileo per file type, **or**
   - right-click a photo → **Open with → Choose another app → Galileo → Always**.

**Keep the default always on your latest build (dev mode).** Point the registration at the
`bin\Debug` exe instead of the published copy — then a normal `dotnet build` updates the very
exe Windows launches, with no re-publish/re-register:
```powershell
.\tools\register-default.ps1 -ExePath "src\PhotosPlus.App\bin\Debug\net8.0-windows10.0.19041.0\win-x64\Galileo.exe"
```
Trade-offs: the repo must stay in place (the path is registered), and the Debug exe needs the
.NET 8 Desktop Runtime installed (fine on a dev machine). To go back to the stable, fully
self-contained copy, just run `.\tools\install.ps1` again.

**Undo:** `.\tools\unregister-default.ps1` removes the registration (Windows reverts to the previous app).

> Helper scripts live in `tools/`: `install.ps1` (publish + register), `register-default.ps1` /
> `unregister-default.ps1` (registry only, e.g. to point at a custom `-ExePath`).
> Each opened file currently launches its own window (no single-instance reuse yet).

---

## Keyboard Shortcuts

| Key | Action |
|-----|--------|
| `←` / `→` | Previous / next image (in viewer) |
| Mouse wheel | Zoom in / out (toward cursor) |
| `+` / `-` | Zoom in / out |
| `0` | Fit to window |
| `R` | Rotate 90° (auto-fit) |
| `H` | **Black out / reveal current photo (eye toggle)** |
| `F` | Toggle full screen (viewer only) |
| `F11` | Toggle full screen (anywhere) |
| `Del` | Delete (to Recycle Bin) |
| `Backspace` | Back (in explorer) |
| `F5` | Refresh folder (explorer) · **start slideshow** (viewer/gallery) |
| `Space` | Slideshow play / pause |
| `←` `→` `↑` `↓` | (in slideshow) prev / next / speed |
| `Esc` | Close settings · exit slideshow / full screen · back to explorer |

---

## Roadmap

See **[tasks.md](./tasks.md)** for the full phased breakdown. Not yet implemented: image editing (crop/adjust/filters/markup), cut/move and drag-and-drop *between* folders, in-explorer search, MSIX packaging, single-instance window reuse, a Windows Hello gate on the Hidden album, slideshow background music, and splitting into `Core`/`Tests` projects.

## License

TBD (MIT recommended).
