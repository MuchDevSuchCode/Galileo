# PhotosPlus

A modern, native alternative to the **Windows 11 Photos** app — built with **WinUI 3 / .NET 8**. PhotosPlus is a fast, local-first photo viewer with a clean Fluent UI, plus two headline additions over stock Photos:

1. **👁 Eye toggle** — a one-click eye icon (shortcut **H**) that instantly **blacks out the photo in the viewer** for privacy, plus an optional **Hidden album** for photos you want kept out of the gallery.
2. **▶ Slideshow** — a full-screen, configurable slideshow with adjustable timing, shuffle, loop, and transitions.

> **Status:** working application. The core viewer, gallery, eye toggle, slideshow, settings, and a modern redesigned UI are implemented and building. Editing, video, and shell/default-app integration remain on the roadmap — see **[tasks.md](./tasks.md)**.

---

## Why PhotosPlus

Windows Photos is capable but cluttered and increasingly cloud-driven. PhotosPlus is a **fast, local-first, privacy-respecting** viewer:

- **Local-first** — no account, no cloud sync; your library stays on disk.
- **Fast** — GPU-composited viewer, async/virtualized thumbnails.
- **Native** — Win11 Fluent, Mica backdrop, extended title bar, dark/light theme.
- **Private** — the eye toggle and Hidden album make it trivial to conceal sensitive images. Hidden state lives in app data; original files are never modified.

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

**Collage** — a **Collage** button builds an auto-arranged collage from the visible photos. A *justified-rows, fit-to-screen* layout keeps every photo's aspect ratio, fills the width, and binary-searches the row height so the whole thing fills the screen with even gutters (no distortion, no big gaps). **Shuffle** re-arranges to a fresh fit, a **− N +** stepper sets how many photos, **Save** exports the collage to PNG, and clicking a tile opens it in the viewer.

**Settings** — a Settings panel to configure the slideshow (seconds per photo, shuffle, loop, transition). Persisted across sessions.

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
│  └─ PhotosPlus.App/          # WinUI 3 app (single project)
│     ├─ App.xaml(.cs)         # app + shared resources (GlyphButton style, PillBrush)
│     ├─ MainWindow.xaml(.cs)  # gallery + viewer + settings + title bar
│     ├─ SlideshowWindow.xaml(.cs)
│     ├─ Models/PhotoItem.cs
│     └─ Services/             # AppState (persistence), PhotoLibrary
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

> ⚠️ Close any running PhotosPlus window before rebuilding — Windows locks the `.exe` while it runs (otherwise the build fails with an `MSB3021` file-lock error).

**Build notes (already configured in the `.csproj`):**
- `<WindowsSdkPackageVersion>10.0.19041.38</WindowsSdkPackageVersion>` — Windows App SDK 1.6 requires SDK.NET.Ref ≥ `.38`; the .NET 8.0.300 SDK ships `.31`.
- `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` — the CsWinRT AOT source generator emits unsafe code for generic WinRT calls (e.g. drag-drop's `GetStorageItemsAsync`).
- Shared XAML styles live in **`App.xaml`**, not `Window.Resources` — the WinUI 1.6 markup compiler crashes on `Style` defined in `Window.Resources`.

---

## Set as your default photo app

PhotosPlus opens a file or folder passed on the command line (`PhotosPlus.App.exe "<file>"`), so it works as a Windows file handler.

1. **Install (and update).** One command publishes a stable, self-contained copy to
   `%LocalAppData%\PhotosPlus\app` and registers it with Windows (per-user, no admin, reversible):
   ```powershell
   .\tools\install.ps1
   ```
   Re-run `install.ps1` any time to push your latest code to the installed copy — it stops the
   running app, re-publishes, and re-registers. (Registration adds a ProgID, an *Open with*
   entry, and a *Default apps* capability for ~23 image extensions.)
2. **Make it the default.** Windows 10/11 doesn't let an app silently take over defaults, so do it once:
   - **Settings → Apps → Default apps** → pick PhotosPlus per file type, **or**
   - right-click a photo → **Open with → Choose another app → PhotosPlus → Always**.

**Undo:** `.\tools\unregister-default.ps1` removes the registration (Windows reverts to the previous app).

> Helper scripts live in `tools/`: `install.ps1` (publish + register), `register-default.ps1` /
> `unregister-default.ps1` (registry only, e.g. to point at a custom `-ExePath`).
> Each opened file currently launches its own window (no single-instance reuse yet).

---

## Keyboard Shortcuts

| Key | Action |
|-----|--------|
| `←` / `→` | Previous / next image |
| Mouse wheel | Zoom in / out (toward cursor) |
| `+` / `-` | Zoom in / out |
| `0` | Fit to window |
| `R` | Rotate 90° (auto-fit) |
| `H` | **Black out / reveal current photo (eye toggle)** |
| `F5` | **Start slideshow** |
| `Space` | Slideshow play / pause |
| `←` `→` `↑` `↓` | (in slideshow) prev / next / speed |
| `Esc` | Close settings · exit slideshow / full screen · back to gallery |
| `F` / `F11` | Toggle full screen |
| `Del` | Delete (to Recycle Bin) |

---

## Roadmap

See **[tasks.md](./tasks.md)** for the full phased breakdown. Not yet implemented: image editing (crop/adjust/filters/markup), video playback, set-as-default-photo-app / MSIX packaging, Windows Hello gate on the Hidden album, slideshow background music, and splitting into `Core`/`Tests` projects.

## License

TBD (MIT recommended).
