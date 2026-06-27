# Galileo for Android

A lightweight companion to the Windows app: a simple **file manager**, a **photo viewer**, and a viewer for a
friend's **shared secure vault** over the same end-to-end-encrypted relay protocol.

## Features

- **Files** — a real file manager: requests **All-files access** (Android 11+ `MANAGE_EXTERNAL_STORAGE`;
  `READ_EXTERNAL_STORAGE` on older), then auto-lists internal storage from the root, navigates folders, and
  shows image thumbnails. Toggle **list ⇄ gallery (grid)** view. Tap an image to open the full-screen viewer.
  The app looks like (and is) a plain file manager.
- **Photo viewer** — **swipe left/right** between photos, **pinch to zoom** + drag to pan + double-tap to
  zoom, **landscape** support, and the screen **stays on** while in the foreground.
- **Photo editor** (Snapseed-style) — open a local image and tap **Edit**: one-tap **filters** (Vivid, Warm,
  Cool, Mono, Noir, Faded…), fine **tune** (brightness / contrast / saturation / warmth), **rotate / flip**,
  and **Save** writes an edited copy next to the original (falls back to `Pictures/Galileo`).
- **PDF viewer** — open a `.pdf` to read it in-app (Android's built-in `PdfRenderer`): vertically scrolling
  pages, rendered lazily for big documents, with pinch-to-zoom.
- **Modern theme** — Material 3 with **Material You** dynamic color on Android 12+ (matches your wallpaper),
  and a polished indigo brand palette (light/dark) on older devices.
- **Hidden shared vault** — there is no visible vault button. **Five quick taps on the "Galileo" title**
  reveal it (so it can't be triggered by accident). The landing page is the **shared gallery**: tap a friend
  to view what they share with you, in list or gallery view with thumbnails. Images stream into the app's cache
  and open in the swipeable viewer, where you can **favorite** media. The owner's access log records the same
  events as the desktop (opened/viewed/downloaded/closed/favorited) and calls out **the Android app**.
- **Settings** (gear icon in the vault) — your **ID** (with copy + change), your **name**, your **friends**
  (send/accept requests by ID over the relay mailbox), and the **relay URL**.
- **Auth on every background** — once an identity is set up, the vault locks itself whenever the app is
  backgrounded, so reopening requires a **fingerprint / biometric** again (where one is enrolled).
- **On-device identity** — generate a new **BIP39 recovery phrase** here, or enter an existing one. The
  identity is stored in `EncryptedSharedPreferences` and never leaves the device.
- **Launcher icon** is generated from the desktop Galileo logo (adaptive icon, navy background).

## How the secure-vault viewer interoperates

The crypto and wire protocol are a faithful Kotlin port of the desktop client, using the **same BouncyCastle**
algorithms so bytes match:

- Identity from BIP39 seed → HKDF-SHA256 → **Ed25519** (sign) + **X25519** (agree); UUID derived exactly like
  .NET's `Guid` (`net/GuidUtil.kt`).
- Relay: HTTP `register` / `lookup`, WebSocket `connect` with a signed auth frame, opaque `relay` envelopes
  (`net/RelayClient.kt`).
- Session: authenticated ephemeral-X25519 handshake signed with Ed25519, HKDF → **AES-256-GCM** with a
  counter nonce (`net/SecureSession.kt`).
- Share protocol: `list` / `open` (chunked fetch) / `view` / `close` / `favorite` / `endbrowse` / `client`
  (`net/ShareProtocol.kt`).

To browse a friend's share: that friend must have **linked you and granted your ID** on the desktop app
(linking alone is not enough — the grant is **per-vault**), and must be **online with that vault unlocked**.
If the gallery comes back empty, the most common cause is that the open vault isn't granted to your ID.

## Build & run

Prereqs: Android Studio (or the Android SDK + JDK 17). The Gradle wrapper targets Gradle 8.9 / AGP 8.5.

```bash
# from the android/ folder
./gradlew :app:assembleDebug          # gradlew.bat on Windows
adb install -r app/build/outputs/apk/debug/app-debug.apk
```

`local.properties` (with `sdk.dir`) is created per-machine and is gitignored.

## Notes / limitations

- Shared vault is **read-only** for now (no upload/create/delete from Android yet — that's desktop-only).
- Only **images** open in the in-app viewer from a share; other types are listed but not opened.
- Shared thumbnails fetch the full image (the host has no separate thumbnail), cached by size, so the first
  scroll through a large gallery pulls each image once.
- Empty folders aren't shown (the vault index is file-only — same as desktop).
- The crypto port is interop-tested by design but if a handshake ever fails, first confirm the **IDs match**
  on both ends (Settings shows yours).
