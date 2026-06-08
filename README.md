# Galileo

A modern, native **Windows Explorer + Photos** alternative — built with **WinUI 3 / .NET 8**. Galileo is a fast, local-first file manager and photo viewer with a clean Fluent UI.

> **Naming:** everything is **Galileo** — the app, the executable (`Galileo.exe`, set via `<AssemblyName>`), the `Galileo` namespace, the `Galileo.App` project, the `%LocalAppData%\Galileo` data folder, and the registered ProgID. (The project was formerly *PhotosPlus*; on first launch it migrates an existing `%LocalAppData%\PhotosPlus\state.json` so old settings carry over.)

Highlights over the stock apps:

1. **🔐 Secure vault** — move folders into an encrypted, Windows-hidden vault (**AES-256-GCM** + **Argon2id**, optional **Windows Hello**), with idle auto-lock, optional self-wipe on repeated wrong passphrases, and encrypted **Google Drive backup**.
2. **👁 Eye toggle** — a one-click eye icon (shortcut **H**) that instantly **blacks out the photo in the viewer** for privacy, plus an optional **Hidden album** for photos kept out of the gallery.
3. **▶ Slideshow** — a full-screen, configurable slideshow with adjustable timing, shuffle, loop, and transitions (incl. Ken Burns).
4. **💻 Developer Mode** — dock a real **cmd / PowerShell / WSL** terminal (ConPTY) beside the explorer, in the current folder.
5. **🔄 Live, local-first** — folders update in place as files change on disk (no manual refresh), with network-share / WSL pinning and a resizable layout.

> **Status:** working application. The tabbed file explorer (search, sort/group with collapsible sections, cut-move, drag-drop, bulk rename, live updates, drive auto-detect, pinned network/WSL locations, Windows Hello gate), photo viewer, gallery, collage, embedded **video + audio player** (album art, multichannel/Atmos), **Spacebar Peek**, **`.zip` archives**, **secure vault** with **Google Drive backup**, an embedded **terminal (Developer Mode)**, an **image editor** (crop, rotate, adjustments, filters, markup), a full **Settings** panel (5 themes and more), and default-photo-app registration are all implemented and building. See **[tasks.md](./tasks.md)** for the roadmap.

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

- **Tabs** — Win11-style **folder tabs**: open multiple locations at once, each with its own back/forward history. New-tab (`+`) and close buttons included.
- **Sidebar** — Home (This PC), Quick access (Desktop/Downloads/Documents/Pictures/Music/Videos), and drives. **Newly mounted/removed drives are detected automatically** and appear without a manual refresh. In **This PC**, each drive shows a **capacity bar with "X free of Y"** like Explorer.
- **Pinned locations** — pin custom paths to the sidebar's **Pinned** section: a local folder, a **network share** (`\\server\share`), or a **WSL** path (`\\wsl.localhost\<distro>\…`). Use **Add location** in the sidebar (paste a path) or right-click a folder → **Pin to sidebar**; right-click a pin → **Remove from sidebar**. (You can also just type any of these paths into the address bar to navigate.)
- **Resizable sidebar** — drag the divider between the sidebar and the file pane to resize it (the width is remembered across sessions).
- **Live updates** — the open folder updates automatically when files change on disk (downloads, other apps, etc.). New items are **inserted in place at their correct sorted position** (and deleted ones removed) without reloading the view, so your **scroll position and selection are kept**. (Uses a file-system watcher; some network/WSL shares don't emit change events — press **F5** there.)
- **Navigation** — back / forward / up, a clickable **breadcrumb**, and an editable **address bar** (pencil button or type a path + Enter). **Backspace** goes back; **F5** refreshes.
- **Search** — a search box filters the current folder by name, with a toggle to **include subfolders** (recursive).
- **Views** — Large / Medium / Small icons with a **size slider**, plus a **Details** view (Name · Date modified · Type · Size). Real shell thumbnails/icons for every file type, with optional **folder content previews** (the first image painted onto the folder icon).
- **Sort & Group** — sort by Name / Date modified / Type / Size (ascending or descending) and **group** by the same keys, mirroring Explorer's defaults. Grouped sections have **collapsible headers** (click the chevron to expand/collapse each group, e.g. "JPG File (12)"). **Click a Details column header** to sort by it (arrow shows direction). **Each folder remembers its own sort, direction, and grouping** (one folder by Date, another by Type, …); folders you haven't sorted inherit your last-used choice. Saved across sessions.
- **Show / hide file extensions** — toggle in Settings (on by default); affects display only — the real filename is preserved for rename, copy, and open.
- **Open** — folders navigate in; images open in the photo viewer; **audio & video open in the embedded player**; **`.zip` archives open in place** (browse like a folder); other files open in their default app.
- **Archives** — double-click a **.zip** to browse it like a folder (extracted to a temp area and opened read-only; the temp copy is wiped on next launch). Right-click a `.zip` for **Extract Here** or **Extract All…**. Password-protected archives aren't supported. **Slideshow** and **Collage** buttons act on the current folder's images. Single- or double-click to open (configurable).
- **File operations** — New folder (with immediate rename), **Cut / Copy / Paste** (move-aware), Copy path, Rename, Delete (Recycle Bin), **Shift+Delete** (permanent), **drag files between folders** (drop onto a folder to copy, hold **Shift** to move) or out to other apps, and the native **Properties** dialog (right-click items or empty space).
- **Keyboard shortcuts** — standard Windows file-management keys: **Ctrl+C / Ctrl+X / Ctrl+V** (copy / cut / paste, move-aware and interoperable with Windows Explorer's clipboard), **Ctrl+A** (select all), **F2** (rename), **Enter** (open), **Del / Shift+Del** (recycle / permanent delete).
- **Selection count** — selecting items shows **how many are selected** (and their total size) in the status bar; with nothing selected it shows the folder's item count. Marquee-drag, Ctrl/Shift-click, and Ctrl+A all update it live.
- **Bulk rename** — select multiple items and Rename (F2 or right-click): pick a base name and they become **`name`, `name-1`, `name-2`, …** (dash numbering), each keeping its own extension. Done collision-safe via a temp-rename pass.
- **Recycle Bin** — a command-strip button to **Open** the Windows Recycle Bin or **Empty** it (with a confirmation).
- **Open in a new window** — **Alt+click** or **Alt+double-click** an image (matching your open-items setting), **middle-click** it, or right-click → **Open in new window**, to view it in a separate Galileo window (works even with "Reuse one window" on). (Shift/Ctrl are reserved for multi-select.)
- **Spacebar Peek (Quick Look)** — press **Space** on the selected file for an instant, dismissible preview (images, video, text/code, or a large thumbnail + details for anything else). Arrow keys step through the folder with the preview open; **Space/Esc** closes; **Enter** opens it for real. Toggle off in **Settings → Spacebar Peek** (on by default).
- **Set as Thumbnail** — right-click any image (in the explorer or the photo grid) → **Set as Thumbnail** to pin it as the parent folder's preview icon.
- **⭐ Hide folder** — the **Hide folder** button (or a folder's right-click) makes a folder **appear empty when opened** and excludes it from its parent. Toggle **Show app-hidden** to reveal hidden folders (dimmed); **Unhide** to restore. App-only and reversible — the folder on disk is never modified. A **Windows Hello** gate can be required before hidden items are revealed (see Settings → Privacy).
- **🔒 Secure vault** — right-click a folder → **Move to new vault…** to encrypt it into a hidden vault, or **Send to Vault** to add items to the vault that's currently unlocked (passphrase and/or Windows Hello). See [Secure vault](#secure-vault) below.

> Planned next: an expandable folder tree in the sidebar, in-place Details column resizing, and a recents/pinned list.

---

## Features (implemented)

**Viewing**
- Open a **file**, a **folder**, or **drag-and-drop** onto the window (single image → opens in viewer; multiple images → builds a gallery; folder → loads it).
- Virtualized gallery grid with async thumbnails; favorite (★) and hidden badges.
- Full-bleed single-image viewer that **scales any photo to fit the window** (up or down), with:
  - **Mouse-wheel zoom** toward the cursor (no modifier), plus +/- buttons and double-tap.
  - **Drag to pan** when zoomed in.
  - **Rotate** (auto re-fits and re-centres so the rotated image stays fully visible).
  - Fit / next / previous / full screen. **←/→ navigation follows the explorer's current sort order** (Name/Date/Type/Size, asc or desc).
- Remembers and reopens your last folder.

**Formats** — JPEG, PNG, GIF, BMP, TIFF, WEBP, HEIC/HEIF, AVIF, and common RAW (CR2/CR3, NEF, ARW, DNG…) decoded via the platform `Windows.Storage` / `BitmapImage` codecs (RAW/HEIC depend on the OS codec being installed).

**Organize & act** — Favorites (★) with a "Favorites only" filter; per-photo metadata panel (dimensions, size, dates, camera); delete to Recycle Bin; reveal in Explorer.

**Right-click menu** (on the viewer image *and* gallery thumbnails) — Copy (image to clipboard), Copy as file, Copy file path, Open with…, Print…, **Set as desktop background**, **Set as lock screen**, **Set as Thumbnail** (folder preview), Favorite, Hide, Rename…, Show in Explorer, Delete, and the native Windows **Properties** dialog. The explorer's right-click menu on an image offers the same **Set as desktop background / lock screen / Thumbnail** actions.

**Collage** — a **Collage** button builds an auto-arranged collage that fills the screen.
- **Layout presets:** **Justified** (aspect-preserving rows, fit to screen), **Grid** (uniform cropped cells), **Hero** (one big image + the rest justified beside/below).
- **Choose what's in it:** *Select photos* in the gallery's More menu to hand-pick images, or **drag-and-drop** image files onto an open collage to add them.
- **Shuffle** re-arranges to a fresh fit; a **− N +** stepper sets how many photos; **Save** exports to PNG; clicking a tile opens it in the viewer. Re-fits on window resize.

**Video & audio** — an **embedded media player** complements the image viewer. Open a file from the explorer to play **video** (MP4/M4V/MOV/MKV/AVI/WMV/WEBM and more) or **audio** (MP3, WAV, FLAC, M4A, AAC, OGG, OPUS, WMA, AIFF…) natively, with transport controls plus **mute/unmute** and a **repeat** toggle. **Spacebar** plays/pauses video, **←/→ step one frame** at a time, and **Ctrl+C copies the current frame** to the clipboard. Audio shows a "now playing" panel with the track name and, when present, **embedded album art** (toggle in **Settings → Media**); a back bar returns to the explorer. Spacebar **Peek** previews media too. Audio/video play in **full multichannel** (5.1/7.1/Atmos) with no forced stereo downmix — enable **Dolby Atmos / DTS:X / Windows Sonic** on your output device and Windows renders the surround/height channels.

**Settings** — a Settings panel (gear in the title bar / command strip) with:
- **Appearance → Theme** — System, Light, Dark, **Terminal (green)**, or **Gray**.
- **Appearance → Default icon size** — Small / Medium / Large; **Folder content previews** on/off.
- **Explorer → Open items with** — double-click (default) or single-click.
- **Explorer → Show file extensions** — on/off (on by default).
- **Explorer → Spacebar Peek (Quick Look)** — on/off (on by default).
- **Explorer → Reuse one window** — single-instance mode: open shell-launched files in the running window instead of a new one (off by default).
- **Privacy → Lock Hidden album** — require **Windows Hello / PIN** before revealing the Hidden album or app-hidden folders (falls back to a confirmation when Hello isn't set up).
- **Secure vault** — idle auto-lock timeout (0 = never), enroll Windows Hello by default, and **wipe-on-failed-unlocks** (enable + attempt count).
- **Media** — show embedded **album art** when playing audio (on/off).
- **Collage → Default layout** — Justified / Grid / Hero.
- **Slideshow** — seconds per photo (2–30 s), shuffle, loop, transition.
- **Backup** — **Sign in with Google** for encrypted Google Drive vault backup (shows the connected account).
- **Developer → Developer Mode** — show the embedded terminal pane (cmd / PowerShell / WSL) beside the explorer.

The panel has **Save / Cancel** buttons, so live edits only persist when you click **Save** (Cancel reverts). All settings persist across sessions (`%LocalAppData%\Galileo\state.json`). The panel opens with a fade/scale animation; its header stays pinned and the body scrolls, so it never clips on small windows.

### ✨ The two headline features

#### 1. Eye toggle — hide / un-hide the current photo
- **Black-out (default):** the eye icon (or **H**) instantly covers the current photo with a solid black curtain — a glance over your shoulder reveals nothing. Press again to reveal. The image is never moved or deleted.
- **Hidden album (persistent):** the eye button's flyout → *Hide permanently* flags the photo so it's excluded from the gallery and slideshows, and collected into a **Hidden album** (toggle *Show Hidden album* in the gallery's More menu).
- Hidden/favorite state is stored as JSON in `%LocalAppData%\Galileo`, never by altering originals.

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
- **Segoe Fluent Icons** throughout for crisp, native Win11 glyphs.
- **Floating, auto-hiding controls:** a translucent pill toolbar in the gallery; back / actions / nav-zoom pills in the viewer that fade out after a few seconds of inactivity.
- **Motion:** a settings fade/scale entrance and a gallery→viewer connected animation.
- Rounded thumbnails, illustrated empty-states, dark/light/custom-theme aware.
- **Smooth under load:** thumbnail/icon decoding is throttled so fast-scrolling a folder of hundreds of media files stays fluid (and never overruns the render pipeline).

---

## Tech Stack

- **UI:** WinUI 3 (Windows App SDK **1.6**), Fluent Design, Mica backdrop. Unpackaged, self-contained desktop app.
- **Runtime:** .NET 8, C# 12.
- **Imaging:** `Windows.Storage` thumbnails + `BitmapImage`; GPU-composited transform-based viewer (zoom/pan/rotate via `CompositeTransform`).
- **MVVM:** CommunityToolkit.Mvvm (observable `PhotoItem`).
- **Storage:** JSON app-state (`%LocalAppData%\Galileo\state.json`) for hidden/favorite flags and slideshow settings.

### Project layout (current)

```
Galileo/
├─ global.json                 # pins .NET SDK 8.0.300
├─ src/
│  └─ Galileo.App/          # WinUI 3 app (single project; builds Galileo.exe)
│     ├─ Program.cs            # custom Main (single-instance redirection before XAML init)
│     ├─ App.xaml(.cs)         # app + shared resources (GlyphButton, PillBrush, explorer templates)
│     ├─ MainWindow.xaml(.cs)  # explorer + tabs + gallery + viewer + video + collage + settings + title bar
│     ├─ SlideshowWindow.xaml(.cs)
│     ├─ Models/               # PhotoItem, ExplorerItem, ExplorerGroup (collapsible), VaultInfo
│     ├─ Services/             # AppState, PhotoLibrary, FileSystemService,
│     │                        #   ShellImaging (icons via IShellItemImageFactory + GetDIBits),
│     │                        #   ShellOps (clipboard / properties / wallpaper / lock screen), ImageCompositor, CollageLayout,
│     │                        #   HelloAuth + HelloKey (Windows Hello), DecodeThrottle (scroll-safe thumbnail decoding),
│     │                        #   Vault / VaultManager / VaultCrypto (AES-256-GCM + Argon2id secure vault),
│     │                        #   GoogleDriveBackup (encrypted cloud backup), ArchiveService (.zip),
│     │                        #   TerminalSession (ConPTY pseudo-console for Developer Mode)
│     ├─ Converters/           # BoolToVisibilityConverter
│     └─ Assets/               # galileo.ico / galileo.png, terminal/index.html (xterm.js host),
│                              #   google-oauth.json (gitignored OAuth client, bundled into builds)
├─ tools/                      # install.ps1, update.ps1, package.ps1, register-default.ps1, unregister-default.ps1
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
dotnet build src/Galileo.App
# then run the produced exe, or:
dotnet run --project src/Galileo.App
```

The build produces **`Galileo.exe`** under `src/Galileo.App/bin/Debug/net8.0-windows10.0.19041.0/win-x64/`.

> ⚠️ Close any running Galileo window before rebuilding — Windows locks the `.exe` while it runs (otherwise the build fails with an `MSB3021` file-lock error). PowerShell: `Get-Process Galileo -ErrorAction SilentlyContinue | Stop-Process -Force`.

**Build notes (already configured in the `.csproj`):**
- `<WindowsSdkPackageVersion>10.0.19041.38</WindowsSdkPackageVersion>` — Windows App SDK 1.6 requires SDK.NET.Ref ≥ `.38`; the .NET 8.0.300 SDK ships `.31`.
- `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` — the CsWinRT AOT source generator emits unsafe code for generic WinRT calls (e.g. drag-drop's `GetStorageItemsAsync`).
- Shared XAML styles live in **`App.xaml`**, not `Window.Resources` — the WinUI 1.6 markup compiler crashes on `Style` defined in `Window.Resources`.

**Publish a self-contained `.exe`**

A self-contained publish bundles the .NET 8 runtime **and** the Windows App SDK, so the result
runs on any Windows 11 machine with nothing pre-installed. From the repo root:

```powershell
dotnet publish src/Galileo.App -c Release -r win-x64 --self-contained true -o publish
```

This produces a standalone **`publish\Galileo.exe`** (plus its runtime files) you can copy and run
anywhere. Swap the runtime identifier for other targets: `-r win-arm64` or `-r win-x86`.

Or use the helper, which stops the running app, publishes a self-contained Release copy to
`%LocalAppData%\Galileo\app`, and (without `-SkipRegister`) registers it as a default photo app:

```powershell
.\tools\install.ps1 -SkipRegister
```

> The publish prints a harmless `NETSDK1198: win-AnyCPU.pubxml was not found` warning because the
> `.csproj` names a per-platform publish profile and `$(Platform)` resolves to `AnyCPU`; it falls
> back to the `-r` settings above. Pass `-p:Platform=x64` to silence it.

**Helper scripts** (`tools/`):
- **`install.ps1`** — publish a self-contained copy to `%LocalAppData%\Galileo\app` and register it as a default photo app (`-SkipRegister` to skip registration).
- **`update.ps1`** — stop any running instance, `git pull`, and re-publish to the installed copy.
- **`package.ps1`** — publish + zip a distributable to `docs\Galileo-Latest.zip` (warns if the zip exceeds GitHub's 100 MB push limit).
- **`register-default.ps1` / `unregister-default.ps1`** — registry-only (e.g. to point the default-app registration at a custom `-ExePath`).

---

## Set as your default photo app

Galileo opens a file or folder passed on the command line (`Galileo.exe "<file>"`), so it works as a Windows file handler.

1. **Install (and update).** One command publishes a stable, self-contained copy to
   `%LocalAppData%\Galileo\app` and registers it with Windows (per-user, no admin, reversible):
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
.\tools\register-default.ps1 -ExePath "src\Galileo.App\bin\Debug\net8.0-windows10.0.19041.0\win-x64\Galileo.exe"
```
Trade-offs: the repo must stay in place (the path is registered), and the Debug exe needs the
.NET 8 Desktop Runtime installed (fine on a dev machine). To go back to the stable, fully
self-contained copy, just run `.\tools\install.ps1` again.

**Undo:** `.\tools\unregister-default.ps1` removes the registration (Windows reverts to the previous app).

> Helper scripts live in `tools/`: `install.ps1` (publish + register), `register-default.ps1` /
> `unregister-default.ps1` (registry only, e.g. to point at a custom `-ExePath`).
> By default each opened file launches its own window; enable **Settings → Reuse one window**
> for single-instance behaviour (opened files reuse the running window).

---

## Secure vault

Galileo can store folders in an encrypted **vault** that is hidden from Windows and only readable while unlocked.

- **Create** — right-click a folder → **Move to new vault…**, or use **New vault** in the sidebar. You set a vault name and a strong passphrase (rated live by a **strength meter**, optionally enrolling **Windows Hello**). The folder's files are encrypted into the vault and the originals are securely removed.
- **Discreet by default** — vaults are **not listed in the sidebar** until one is unlocked; click the **Vaults** entry to pick a vault to unlock or to create one. Once unlocked, the full list appears.
- **Send to Vault** — while a vault is unlocked, right-click any file/folder in clear space → **Send to Vault**. Each item is encrypted into the open vault immediately and the original is securely wiped from clear space.
- **Hidden from Windows** — vault contents live as opaque, random-named encrypted blobs under `%LocalAppData%\Galileo\Vaults\<id>` with an encrypted index. There is no readable folder, filename, or content in Explorer.
- **Encryption** — each file is encrypted with **AES-256-GCM** (chunked, so multi-GB videos stream). The data key is wrapped by a key derived from your passphrase with **Argon2id**, and (optionally) by a **Windows Hello / TPM** keyslot. Either factor unlocks the same vault; **the passphrase is the only recovery key — there is no reset.**
- **Full app while unlocked** — unlocking decrypts the vault into a working folder under your user profile, so the explorer, viewer, video player, gallery, slideshow, and collage all work exactly as they do for any folder. Add files with **Send to Vault**, or by copying/pasting/dragging them in while unlocked.
- **Auto-lock** — an unlocked vault re-locks (re-encrypts changes and securely wipes the working folder) when you click **Lock** (or right-click the vault → **Lock**), after an idle timeout, or when you close Galileo. Configure the idle timer in **Settings → Secure vault** (0 = never).
- **Wipe on failed unlocks** — optionally (**Settings → Secure vault**) **permanently destroy** a vault after a configurable number of wrong passphrases. This is irreversible; the attempt counter persists across restarts and resets on a successful unlock.
- **Windows Hello** — when enrolled, the unlock dialog offers a **Windows Hello** button; the passphrase always works as a fallback.
- **Rename / Lock** — right-click a vault in the sidebar → **Rename…** (display name only; works locked or unlocked) or **Lock** (re-encrypts and hides it).
- **Cloud backup (Google Drive)** — **Sign in with Google** in **Settings → Backup** (or right-click a vault → **Back up to Google Drive**) to copy your vaults off-device; the signed-in account is shown and you stay signed in across launches. Only the **encrypted blobs (obfuscated names)**, the encrypted index, and a **name-stripped manifest** are uploaded — the key never leaves your device, so Google can't read your vaults. **Restore from Drive…** re-downloads a vault; unlock it with your passphrase as usual. Uses the minimal `drive.file` scope (the app only ever sees files it created). Clicking **Sign in with Google** launches your browser for the standard OAuth consent flow. Galileo ships a *Desktop app* OAuth client as a **gitignored `Assets\google-oauth.json`** bundled into the build (so the secret never lands in source control), with a per-user override at `%LocalAppData%\Galileo\google-oauth.json` that takes precedence. The OAuth project must have the **Drive API enabled** and its consent screen **published to Production** for arbitrary accounts to sign in.

> **Security notes.** While unlocked, decrypted files exist in a working folder under `%LocalAppData%\Galileo\.work` (restricted to your Windows account); it is securely wiped on lock, and any copy left by a crash is wiped at the next launch. Secure deletion is overwrite-then-delete, which is **best-effort on SSDs** (wear-levelling/TRIM may retain remnants) and not a forensic guarantee. Windows may also cache thumbnails for files opened while unlocked. For **Google Drive backup**, only encrypted/obfuscated files and a name-stripped manifest are uploaded (Google sees the vault's random id and file sizes, never contents); the OAuth refresh token is stored under `%LocalAppData%\Galileo\gdrive-token`.

---

## Image editor

Open any image and click **Edit** (the pencil in the viewer toolbar, or right-click → **Edit…**) to open a full editor layered over the viewer, with a live GPU preview:

- **Transform** — rotate left/right, flip horizontal/vertical, **straighten** (−45…45° slider), and **crop** with aspect presets (Free / Original / 1:1 / 4:3 / 3:2 / 16:9 — drag on the image to set the region).
- **Adjustments** — Exposure, Brightness, Contrast, Saturation, Temperature, Tint, and Sharpness sliders, applied in real time.
- **Filters** — one-tap presets: Auto, B&W, Sepia, Vivid, Warm, Cool, Invert.
- **Markup** — annotate with pen/highlighter/eraser (ink), **text**, and **rectangle / ellipse / line / arrow** shapes in your choice of color.
- **Undo / Redo / Reset**, then **Save** — by default it writes a **copy** next to the original (`<name>-edited.<ext>`) so the source is never touched; the Save dropdown also offers **Save as…** and **Overwrite original**.

Rendering uses **Win2D** (GPU effect graph) for the preview and a full-resolution bake on save. Edits are non-destructive until you save, and the saved copy appears in the folder automatically (live refresh). HEIC/RAW sources are supported when the OS codec is installed.

---

## Phones & devices (MTP)

Plug in a phone or camera and it appears in the sidebar's **Devices** section and in **This PC** — even though MTP devices have no drive letter or file paths. Galileo browses them through the Windows **shell namespace**:

- **Browse** the device's folders with thumbnails; navigate via the breadcrumb / Up / tabs like any folder.
- **View** photos and play videos in-app — the file is streamed to a temp copy under `%LocalAppData%\Galileo\.mtp` (wiped at next launch), then opened in the normal viewer/player.
- **Copy to PC…** (download) the selected items to a folder you choose, and **Upload files…** from your PC onto the device.
- **New folder**, **Rename…**, and **Delete** on the device — delete is **permanent (no Recycle Bin)** and is confirmed first.

Transfers use the shell's **`IFileOperation`** (with its progress dialog); browsing/thumbnails use `IShellItem` + `IShellItemImageFactory`. *Notes:* file sizes/dates aren't populated for device items yet; the viewer's prev/next doesn't span device photos (single-file view); devices are detected when This PC is shown or on restart (not instant hot-plug).

---

## Developer Mode (embedded terminal)

Turn on **Settings → Developer → Developer Mode** to dock a real **terminal pane beside the file explorer**. A **Terminal** button then appears in the command strip (and folders get a right-click **Open terminal here**). The pane runs **Command Prompt**, **PowerShell** (`pwsh` if installed, otherwise Windows PowerShell), or **WSL** (when `wsl.exe` is present), starting in the current folder — pick the shell from the dropdown and drag the divider to resize. It's a real console: built on a Windows **pseudo-console (ConPTY)** feeding an **xterm.js** front-end hosted in **WebView2**. (xterm.js loads from a CDN on first use, then is cached; the shell process is terminated when the pane/app closes.)

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
| `Shift`+`S` | **Save a screenshot** of the window to `%USERPROFILE%\Pictures\Galileo` |
| `Space` | Play / pause (video) |
| `←` / `→` | Step one frame back / forward (video) |
| `Ctrl`+`C` | Copy the current video frame to the clipboard (video) |
| `F` | Toggle full screen (viewer only) |
| `F11` | Toggle full screen (anywhere) |
| `Del` / `Shift`+`Del` | Delete to Recycle Bin / permanently |
| `Backspace` | Back (in explorer) |
| `Ctrl`+`C` / `Ctrl`+`X` / `Ctrl`+`V` | Copy / cut / paste items (explorer) |
| `Ctrl`+`A` | Select all (explorer) |
| `F2` | Rename selected item (explorer) |
| `Enter` | Open selected item (explorer) |
| `Space` | **Peek** — preview the selected file (explorer) |
| `←` `→` `↑` `↓` | Step to prev / next file while peeking |
| `F5` | Refresh folder (explorer) · **start slideshow** (viewer/gallery) |
| `Space` | Slideshow play / pause (slideshow) |
| `←` `→` `↑` `↓` | (in slideshow) prev / next / speed |
| `Esc` | Close settings · exit slideshow / full screen · back to explorer |

---

## Roadmap

See **[tasks.md](./tasks.md)** for the full phased breakdown. Not yet implemented: an expandable folder tree in the sidebar, MSIX packaging, slideshow background music, and splitting into `Core`/`Tests` projects.

## License

TBD (MIT recommended).
