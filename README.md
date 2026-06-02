# PhotosPlus

A modern, native replacement for the **Windows 11 Photos** app — built with **WinUI 3 / .NET 8** for full Fluent Design integration. PhotosPlus aims for feature parity with Microsoft Photos, plus two headline additions:

1. **👁 Eye toggle** — a one-click "eye" icon that instantly **hides / un-hides the photo currently in the viewer** (privacy obscure), with an optional persistent **Hidden** album.
2. **▶ Slideshow** — a full-featured, configurable slideshow mode with transitions, timing, shuffle, and music.

---

## Why PhotosPlus

Windows Photos is capable but cluttered, increasingly cloud-driven, and slow to open large folders. PhotosPlus is a **fast, local-first, privacy-respecting** viewer and light editor that keeps the parts people use every day and removes the friction.

**Design principles**

- **Local-first** — no account, no forced cloud sync; your library stays on disk.
- **Fast** — GPU-accelerated rendering, async thumbnail pipeline, instant folder open.
- **Native** — Win11 Fluent UI, Mica/Acrylic, dark/light theme, proper file associations.
- **Private** — the eye toggle and Hidden album make it trivial to conceal sensitive images.

---

## Feature Set

### Parity with Windows Photos

| Area | Features |
|------|----------|
| **Viewing** | Open single image or folder; gallery grid with adjustable thumbnail size; single-image viewer; next/previous navigation; zoom (wheel, pinch, +/-), pan, fit-to-window, 1:1, rotate, flip. |
| **Formats** | JPEG, PNG, GIF (animated), BMP, TIFF, WEBP, HEIC/HEIF, AVIF, and common RAW (CR2/CR3, NEF, ARW, DNG) via `Windows.Graphics.Imaging` codecs. |
| **Collections** | Folder browsing, "Recent", "Favorites" (★), and an indexed library across user-chosen folders. |
| **Editing** | Crop & straighten, rotate/flip, aspect-ratio presets, brightness/contrast/exposure/saturation/warmth, auto-enhance, filters, red-eye, spot fix, markup/draw (ink). Non-destructive with "Save a copy". |
| **Metadata** | EXIF/IPTC view (camera, lens, date, GPS, dimensions), rename, file info. |
| **Video** | Play/pause/scrub common video formats; basic trim; frame export. |
| **Actions** | Print, Share (Win share sheet), Copy, Set as background / lock screen, Delete (with recycle), Open with…, reveal in Explorer. |
| **Search** | Filename and metadata search; date and folder filters. |
| **Shell** | Register as a photo-handler so PhotosPlus can be set as the default photo app; jump list / "Open with". |

### ✨ Added in PhotosPlus

#### 1. Eye toggle — hide / un-hide the current photo

A persistent **eye icon** in the viewer command bar (and keyboard shortcut **H**).

- **Obscure mode (default):** clicking the eye instantly replaces the on-screen image with a privacy overlay (blur + 👁‍🗨 "hidden" placeholder). The image is *not* deleted or moved — it is concealed in the current view so a glance over your shoulder reveals nothing. Click again (or press **H**) to reveal.
- **Hidden album (persistent):** holding the eye / choosing "Hide permanently" flags the image so it is excluded from the gallery and search, and collected into a **Hidden** album. The Hidden album is itself gated behind the eye (and optionally Windows Hello).
- **Stateful icon:** the glyph switches between `` (eye) and `` (eye-off) to reflect current state; a subtle badge shows hidden count.
- Hidden state is stored in app data (a sidecar index), never by altering the original files.

#### 2. Slideshow

Launch from the command bar (**F5**) or right-click → *Slideshow*.

- Plays the current folder/album/selection in order or **shuffled**.
- **Per-slide duration** (2–30 s) and **loop** toggle.
- **Transitions:** none, crossfade, slide, Ken Burns (slow zoom/pan).
- **Controls:** play/pause (Space), next/prev (←/→), speed, exit (Esc); auto-hiding overlay; optional caption (filename/date).
- **Full-screen** on the active monitor; multi-monitor aware.
- Optional **background music** from a chosen audio file/playlist.
- **Hidden images are skipped** in slideshows, so the eye toggle and slideshow cooperate.

---

## Tech Stack

- **UI:** WinUI 3 (Windows App SDK 1.5+), Fluent Design, Mica backdrop.
- **Runtime:** .NET 8, C# 12.
- **Imaging:** `Windows.Graphics.Imaging` (BitmapDecoder/Encoder), Win2D / Composition for GPU rendering, async thumbnail cache.
- **Architecture:** MVVM (CommunityToolkit.Mvvm), dependency injection (`Microsoft.Extensions.DependencyInjection`).
- **Storage:** local SQLite (or LiteDB) index for library, favorites, hidden flags, and settings.
- **Packaging:** MSIX; declares photo file-type associations so it can be set as the default app.

```
PhotosPlus/
├─ src/
│  ├─ PhotosPlus.App/          # WinUI 3 app head (App.xaml, MainWindow)
│  ├─ PhotosPlus.Core/         # models, services (library, hidden, slideshow, codecs)
│  └─ PhotosPlus.Tests/        # unit tests
├─ assets/                     # icons, sample images
├─ README.md
└─ tasks.md
```

---

## Getting Started (planned)

> The repository currently contains design docs (`README.md`, `tasks.md`). The steps below describe the intended developer workflow once scaffolding lands — see `tasks.md` for current status.

**Prerequisites**

- Windows 11 (22H2 or later)
- Visual Studio 2022 with the **Windows App SDK / WinUI** workload, or `winget install Microsoft.DotNet.SDK.8`
- Windows App SDK 1.5+ runtime

**Build & run**

```powershell
git clone <repo> PhotosPlus
cd PhotosPlus
dotnet restore
dotnet build
dotnet run --project src/PhotosPlus.App
```

---

## Keyboard Shortcuts

| Key | Action |
|-----|--------|
| `←` / `→` | Previous / next image |
| `+` / `-` | Zoom in / out |
| `0` | Fit to window |
| `1` | Actual size (1:1) |
| `R` | Rotate 90° |
| `H` | **Hide / un-hide current photo (eye toggle)** |
| `F5` | **Start slideshow** |
| `Space` | Slideshow play / pause |
| `Esc` | Exit slideshow / full screen |
| `F` / `F11` | Toggle full screen |
| `Del` | Delete (to Recycle Bin) |

---

## Roadmap

See **[tasks.md](./tasks.md)** for the full, phased task breakdown and current progress.

## License

TBD (recommend MIT for an open-source viewer).
