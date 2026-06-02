# PhotosPlus — Task Breakdown

Phased plan to reach Windows Photos parity plus the **eye hide/un-hide toggle** and **slideshow** features.

Legend: `[ ]` todo · `[~]` in progress · `[x]` done

---

## Phase 0 — Project setup

- [ ] Create WinUI 3 (.NET 8) solution: `PhotosPlus.App`, `PhotosPlus.Core`, `PhotosPlus.Tests`.
- [ ] Add packages: `CommunityToolkit.Mvvm`, `Microsoft.Extensions.DependencyInjection`, `Microsoft.Graphics.Win2D`, SQLite/LiteDB.
- [ ] Configure MVVM + DI host; app theme (Mica, dark/light).
- [ ] Set up CI (build + test) and linting/formatting (`dotnet format`).
- [ ] MSIX packaging project with photo file-type associations declared.

## Phase 1 — Core viewer (parity)

- [ ] Image decode service over `Windows.Graphics.Imaging` (JPEG, PNG, GIF, BMP, TIFF, WEBP, HEIC, AVIF, RAW).
- [ ] GPU-accelerated viewer control: zoom (wheel/pinch/+/-), pan, fit-to-window, 1:1.
- [ ] Rotate / flip.
- [ ] Animated GIF playback.
- [ ] Next/previous navigation within a folder; preload neighbors.
- [ ] Open single file (file association + "Open with") and open folder.
- [ ] Full-screen mode (F11/F).

## Phase 2 — Library & collections (parity)

- [ ] Folder picker + persisted library locations.
- [ ] Async thumbnail pipeline + on-disk thumbnail cache.
- [ ] Gallery grid view with adjustable thumbnail size; virtualization for large folders.
- [ ] Favorites (★) stored in local index.
- [ ] "Recent" and "All photos" virtual collections.
- [ ] Search by filename; filters by date and folder.
- [ ] EXIF/IPTC metadata panel (camera, lens, date, GPS, dimensions).

## Phase 3 — ⭐ Eye toggle: hide / un-hide current photo

- [ ] Add **eye icon** to the viewer command bar with stateful glyph (eye ⇄ eye-off) and tooltip.
- [ ] **Obscure mode:** toggle replaces the displayed image with a blur + "hidden" privacy overlay; toggle again to reveal. Bind to shortcut **H**.
- [ ] Ensure obscure is view-only — never moves, edits, or deletes the original file.
- [ ] **Persistent hide:** "Hide permanently" flags the image in the local index; excluded from gallery + search.
- [ ] **Hidden album** view that lists hidden images; itself gated behind the eye toggle.
- [ ] Optional **Windows Hello** gate before revealing the Hidden album.
- [ ] Hidden-count badge on the eye icon.
- [ ] Persist hidden state across sessions via sidecar index (no original-file mutation).
- [ ] Unit tests: toggle state machine, persistence round-trip, gallery/search exclusion.

## Phase 4 — ⭐ Slideshow

- [ ] Slideshow service: ordered/shuffled queue from current folder/album/selection.
- [ ] Full-screen slideshow shell on the active monitor (multi-monitor aware).
- [ ] Per-slide duration (2–30 s) and loop toggle.
- [ ] Transitions: none, crossfade, slide, **Ken Burns** (slow zoom/pan).
- [ ] Controls overlay (auto-hide): play/pause (Space), prev/next (←/→), speed, exit (Esc).
- [ ] Optional caption (filename/date).
- [ ] Optional background music (audio file/playlist).
- [ ] **Skip hidden images** in slideshows (cooperate with Phase 3).
- [ ] Launch entry points: command bar button + **F5** + right-click → Slideshow.
- [ ] Unit/UI tests: queue ordering, shuffle, hidden-skip, timing.

## Phase 5 — Editing (parity)

- [ ] Non-destructive edit pipeline with "Save a copy".
- [ ] Crop & straighten; aspect-ratio presets.
- [ ] Adjustments: brightness, contrast, exposure, saturation, warmth, auto-enhance.
- [ ] Filters.
- [ ] Red-eye and spot fix.
- [ ] Markup / ink draw.

## Phase 6 — Actions & shell integration (parity)

- [ ] Print.
- [ ] Share via Windows share sheet.
- [ ] Copy to clipboard.
- [ ] Set as background / lock screen.
- [ ] Delete to Recycle Bin; rename; reveal in Explorer; Open with….
- [ ] Register as default photo handler; jump list.

## Phase 7 — Video (parity)

- [ ] Play/pause/scrub common video formats.
- [ ] Basic trim; export frame.

## Phase 8 — Polish & release

- [ ] Settings page (theme, default folder, slideshow defaults, Hello gate, eye behavior).
- [ ] Keyboard shortcut map + accessibility (narrator, high contrast, keyboard nav).
- [ ] Performance pass: cold-open time, memory on large folders.
- [ ] Localization scaffolding.
- [ ] App icon, store assets, README polish.
- [ ] Package & sign MSIX; release notes.

---

## Milestones

1. **M1 – Viewable:** Phases 0–1 (open and view any supported image).
2. **M2 – Library + Eye:** Phases 2–3 (gallery, favorites, **eye hide/un-hide**).
3. **M3 – Slideshow:** Phase 4.
4. **M4 – Editor + Actions:** Phases 5–6.
5. **M5 – 1.0:** Phases 7–8.

## Open questions

- "Hide" default behavior — obscure-only, or always offer permanent-hide? (Current plan: obscure by default, permanent via menu.)
- RAW codec coverage — rely on Microsoft Raw Image Extension, or bundle LibRaw?
- Storage engine — SQLite vs LiteDB for the local index.
- License choice (README suggests MIT).
