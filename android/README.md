# Galileo for Android

A lightweight companion to the Windows app: a simple **file manager**, a **photo viewer**, and a viewer for a
friend's **shared secure vault** over the same end-to-end-encrypted relay protocol.

## Features

- **Files** — pick a folder (Storage Access Framework) and browse it; tap an image to view it full-screen.
- **Shared vault** — enter your Galileo **recovery phrase** (Settings) and a friend's **owner ID (UUID)**, connect,
  and browse what they share with you. Images stream into the app's cache and open in the viewer. The owner's
  access log records the same events as the desktop client (opened/viewed/downloaded/closed) and additionally
  calls out that the connection came from **the Android app**.
- **Settings** — store your recovery phrase (kept in `EncryptedSharedPreferences`, never leaves the device),
  optional BIP39 passphrase, and the relay URL. Your derived **ID is shown so you can confirm it matches your
  desktop identity** (it will, if you enter the same phrase).

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

To browse a friend's share: that friend must have **linked you and granted your ID** on the desktop app, and
must be **online with the vault unlocked**. Use the **same recovery phrase** here as your desktop identity so
your UUID (and therefore the grant) matches.

## Build & run

Prereqs: Android Studio (or the Android SDK + JDK 17). The Gradle wrapper targets Gradle 8.9 / AGP 8.5.

```bash
# from the android/ folder
./gradlew :app:assembleDebug          # gradlew.bat on Windows
adb install -r app/build/outputs/apk/debug/app-debug.apk
```

`local.properties` (with `sdk.dir`) is created per-machine and is gitignored.

## Notes / limitations

- Viewer is **read-only** for now (no upload/create/delete from Android yet — that's desktop-only).
- Only **images** open in the in-app viewer from a share; other types are listed but not opened.
- Empty folders aren't shown (the vault index is file-only — same as desktop).
- The crypto port is interop-tested by design but if a handshake ever fails, first confirm the **IDs match**
  on both ends (Settings shows yours).
