# Galileo project and source review

Review date: September 10, 2026  
Reviewed revision: `bdfb406` — On-disk thumbnail cache, sidebar folder tree, and viewer/memory perf pass  
Scope: Windows desktop Explorer replacement, its media tools, persistence, vaults, backup, and installation workflow.

## 1. Assessment

Galileo has a substantial native Windows feature set and useful work already invested in thumbnails, navigation, shell integration, media viewing, and accessibility. However, the current source contains data-integrity and lifecycle defects that should be addressed before treating it as a dependable replacement for routine file management. The highest priorities are transactional file replacement, vault persistence and recovery, process ownership of temporary data, and safe backup/restore.

The UI also makes several promises that the implementation does not consistently uphold: archive browsing is described as read-only, cancellation sounds more reversible than it is, Settings appears modal while background shortcuts remain active, and failed saves can discard editor work.

This review makes recommendations only. No application source, configuration, installed files, user data, or settings were changed. This report is the sole requested addition.

### Method and limits

- Read the README, roadmap, existing `polish-review.md`, project configuration, startup/lifecycle code, principal file services, and relevant explorer, settings, editor, backup, and terminal handlers and XAML.
- Traced failure paths and caller/callee behavior, including whether earlier review items marked complete are actually resolved.
- Used read-only repository inspection. The initial Git status showed no tracked modifications; Git emitted a permission warning about the user's global ignore file.
- Did not run the application, installers, update scripts, destructive operations, restore operations, or cloud requests. Launching this application can itself wipe shared working directories, so a live review should use isolated app data and disposable files.
- Did not build or run automated tests. This preserves the requested no-change review scope, including build outputs. Documentation claiming a clean build is historical, not a result verified in this review.
- UI findings are source-based, not observations from a rendered session. Visual contrast, focus behavior across all WinUI controls, performance measurements, device behavior, and assistive-technology compatibility still require runtime validation.
- Android, deployed binaries, AI model accuracy, and the entirety of native/COM interop are outside this review's detailed audit. This is not a cryptographic certification or an exhaustive security assessment.

Source references below use repository-relative paths and one-based line numbers at the reviewed revision. For brevity, `Services/`, `Assets/`, and unqualified application filenames are relative to `src/Galileo.App/`. Scenarios are proposed reproductions unless explicitly described otherwise; no destructive reproduction was executed.

### Priority definitions

| Priority | Meaning |
|---|---|
| P1 | High: credible data loss, privacy exposure, unsafe recovery, or severely misleading critical workflow. Resolve before relying on the affected feature. |
| P2 | Medium: incorrect behavior, impaired responsiveness, accessibility/workflow defect, or material reliability gap. |
| P3 | Improvement: maintainability, release readiness, or product clarity requiring planned work. |

## 2. Context and requirements

### Architecture and product intent

The Windows application is an unpackaged WinUI 3/.NET 8 desktop executable named `Galileo.exe`. The project targets Windows SDK APIs through `net8.0-windows10.0.19041.0`, declares a minimum platform version of `10.0.17763.0`, and publishes a self-contained Windows App SDK application. The project lists x64/x86/ARM64 runtime identifiers, while its default runtime and installation script select x64.

`MainWindow.xaml.cs` is approximately 6,986 lines and owns navigation, filesystem actions, clipboard, search, settings, terminal, backup, vault UI, and shutdown. Other partial files add image editing, AI, video editing, and tray behavior. Services provide storage operations, cryptography, shell interop, rendering, and persistence. `App.State` and `App.Vaults` are process-wide, but additional independent processes can run against the same per-user storage.

Most state lives under `%LocalAppData%\Galileo`: settings, custom Recycle Bin, encrypted vaults, decrypted working folders, archive/device temporary files, thumbnails, and logs. This makes process ownership, atomic persistence, and cleanup policy central requirements.

The README positions Galileo as a fast, local-first, privacy-respecting Explorer plus Photos alternative. The roadmap retains an earlier Photos-focused organization and stale completion markers. The review therefore treats the README's advertised behaviors and the user's Explorer-replacement objective as the intended baseline, while distinguishing recommendations from agreed requirements.

### Requirements-to-code assessment

| Area | Intended behavior | Assessment |
|---|---|---|
| Navigation | Tabs, independent histories, paths/breadcrumbs, drives, pins, tree, network/WSL access | Broad implementation exists; ordinary directory enumeration still blocks the UI and failures resemble empty folders. |
| File management | Correct copy/move/rename/delete, recoverability, collision handling, cancellation and accurate progress | Implemented, but replacement failure, link traversal, partial-success accounting, and process coordination need work. |
| Live browsing | Preserve selection and scroll during external changes; search and sort/group reliably | Watcher and incremental refresh work is present; recursive search lacks cancellation and complete-result reporting. |
| Native integration | Clipboard, drag/drop, properties, default-app launch, device access | Significant integration exists; requires disposable-device and cross-application validation. Device paste currently rejects the destination. |
| Privacy | Hidden items excluded as promised; vaults durable, lockable, recoverable, and protected | High-risk gaps in commit, shutdown, startup cleanup, missing-file handling, and backup consistency. App hiding must remain clearly distinguished from encryption. |
| Media | Viewer, slideshow, collage, image/video editing and reliable export | Rich implementation; failed overwrite and truncated pixel-undo history undermine edit recovery. |
| Usability | Responsive native UI, keyboard access, accessible controls, clear progress and errors | Accessibility labels and focus-cycle work are present, but command routing and operation feedback need correction. |
| Persistence | Preferences and file metadata survive restarts and multiple windows | Atomic settings replacement exists, but timeout handling and stale multi-process writes remain incorrect. |
| Offline use | Core functionality works locally without network dependency | Terminal assets require a CDN on first use. Optional cloud backup should remain explicitly optional. |
| Release quality | Reproducible builds, safe updates, supported platform matrix, regression tests | No tracked automated test project or CI workflow was found in the inspected inventory; update behavior is unsafe for in-flight work. |

### Decisions needed before implementation

1. Define the supported multi-process model. Separate windows can be desirable without permitting independent writers and cleanup owners for the same vault storage.
2. Specify move cancellation precisely: whether already completed items remain moved, what is rolled back, and how partial completion is reported.
3. Specify filesystem fidelity for metadata, attributes, alternate data streams, symbolic links, junctions, cloud placeholders, and permissions. A generic byte-stream copy is not automatically sufficient for an Explorer replacement.
4. Decide whether archive browsing is strictly read-only or supports an explicit extract/edit/repack flow.
5. Define recovery guarantees for vaults and backups, including missing blobs, failed commits, intentional deletion of all contents, and unsupported empty directories.
6. State minimum supported window size, text scaling, accessibility acceptance criteria, and responsiveness targets.

## 3. Core functionality, stability, and data integrity

### C01 — P1: Replacing a destination destroys the previous file before the copy succeeds

**Evidence:** `src/Galileo.App/Services/FileTransfer.cs:186,353–371`.

`CopyFile` opens the final destination with `FileMode.Create`. For Replace, this truncates the pre-existing destination immediately. Cancellation or a later read/write error deletes the partial destination; it cannot restore the file that was there before. A destination that appears after planning can also be overwritten without a new conflict decision.

**Scenario:** Replace an existing large file, then cancel or encounter a full disk partway through. The old destination is lost even though the replacement did not complete.

**Recommendation/acceptance:** Copy to a unique sibling staging file, finish and flush it, then commit with the appropriate replace/no-overwrite semantics. Revalidate collisions at commit. Fault-injection tests should prove that the old destination survives cancellation and read/write failures unchanged.

### C02 — P1: Vault commits delete old blobs before safely publishing the replacement index

**Evidence:** `Services/Vault.cs:530–545`; manifest writes at `:137`.

`SyncWorkingToBlobsAsync` securely removes superseded blobs before `SaveIndex`; the index is then written directly with `File.WriteAllBytes`. A crash or write failure can leave the previous index referencing deleted blobs, or leave a truncated index. Manifest updates similarly rewrite the file holding wrapped-key metadata in place.

**Scenario:** Modify a vault file and interrupt a flush between old-blob deletion and index persistence. The old generation is no longer recoverable, and the new generation may not be discoverable.

**Recommendation/acceptance:** Use immutable blobs and atomic index/manifest generations. Commit a durable replacement index before garbage-collecting unreferenced blobs; retain a recovery generation. Inject failures at every persistence boundary and verify the vault opens to a complete old or new generation.

### C03 — P1: A normal second launch wipes another process's live vault and temporary files

**Evidence:** `Services/AppState.cs:73`; `Program.cs:39` redirection policy; `MainWindow.xaml.cs:404–409`; `Services/VaultManager.cs`, `WipeOrphanWorkDirs`.

Single-instance mode defaults to off. Startup cleanup excludes guest windows and `--new-window`, but not another ordinary independent launch. Both processes use the same `.work`, archive, and device temporary roots. The second process treats the first process's active working files as crash leftovers.

**Scenario:** Unlock a vault and modify a file in one normal instance; launch Galileo normally again. The second file-manager initialization can wipe the active plaintext working directory. Unsynced changes can be lost; archive/device browsing in the first instance can also break.

**Recommendation/acceptance:** Establish ownership using process/session leases and exclusive vault access. Clean only abandoned sessions. Test two ordinary launches as well as guest windows and crash recovery; no active session's data may be removed.

### C04 — P1: Shutdown exits even when vault commit/lock fails

**Evidence:** `MainWindow.xaml.cs:6981–6985`; `Services/Vault.cs:243–265`.

`Vault.LockAsync` deliberately retains the working copy and key after a failed commit. The closing handler catches that failure, logs it, marks shutdown ready, and closes anyway. Plaintext can remain on disk, and the next launch's orphan cleanup can destroy the only copy of changes made since the last successful flush.

**Scenario:** Cause the encrypted store to become unwritable, edit a working file, and close. A log entry is the only failure response before exit.

**Recommendation/acceptance:** Keep the session open after a failed lock and offer retry or a deliberate recovery/export path. Do not let startup erase an uncommitted recovery session. Test failed lock from window close, tray exit, and updater shutdown.

### C05 — P1: Backup uploads a blob list and index from different generations

**Evidence:** `Services/GoogleDriveBackup.cs:162–190`; manual callers `MainWindow.xaml.cs:5084–5101,5172–5195`.

The backup lists local blobs, uploads them, then opens the current index. No vault gate or immutable snapshot connects those reads. If a flush creates a new blob and publishes a new index after enumeration, the backup uploads an index referencing an unuploaded blob. Retrying only when an old blob disappears does not detect this schedule. Manual backup accepts unlocked vaults, and backing up immediately after an edit does not itself guarantee a flush of that edit.

**Scenario:** Add or modify a file during a slow manual upload after the blob list has been captured. The reported successful backup may be incomplete.

**Recommendation/acceptance:** Flush and capture a complete immutable snapshot under the same vault synchronization mechanism, retaining its blobs throughout upload. Publish remote metadata last and validate every referenced blob. Run a restore drill while repeatedly editing during backup.

### C06 — P1: Restore writes directly over the live vault without a transactional recovery path

**Evidence:** `Services/GoogleDriveBackup.cs:228–270`; `MainWindow.xaml.cs:5132–5167`.

Restore creates files directly inside the local vault, truncating matching files as downloads begin. There is no staging/validation/atomic swap, no guard against restoring the currently unlocked vault, and no coordination with the backup flag. The selection dialog notes “already on this PC” but does not explain that local contents will be overwritten or obtain a distinct replacement decision. The download result is not inspected before reporting completion.

**Scenario:** Restore an older backup over an existing vault and interrupt a download. The local store can contain a mixture of generations and incomplete files. Restoring while unlocked also leaves the manager's in-memory index/key state out of agreement with disk.

**Recommendation/acceptance:** Stage into a separate directory, check every download outcome and manifest/index/blob consistency, require an explicit existing-vault replacement decision, coordinate lock/backup state, and retain a rollback copy until success is verified.

### C07 — P1: Restore validates child filenames but not the remote vault identifier

**Evidence:** `Services/GoogleDriveBackup.cs`, `ListBackupsAsync`, `RestoreVaultAsync:235`, and `IsSafeRemoteName:340`.

The remote folder name becomes `RemoteVault.Id` and is combined directly with `VaultsRoot`. Child filename validation does not cover this parent path. A renamed/tampered remote folder containing an absolute or traversal path can direct restore outside the vault root. The downloaded manifest's `Id` and decrypted entry paths also lack containment validation before working-path construction in `Vault.cs:466–479`.

**Recommendation/acceptance:** Require the expected identifier format and validate canonical containment for every derived path. Validate manifest identity against its owning directory and relative index paths before writing. Exercise malformed remote folder IDs and manifests only against a disposable restore root.

### C08 — P1: Missing vault blobs are silently accepted and can disappear from future indexes

**Evidence:** `Services/Vault.cs:479,502–536,548–550`.

Unlock skips a referenced blob that is absent. A subsequent sync reconstructs the index from the remaining working files, so a missing entry can be silently removed. A missing `index.enc` is also treated as a new empty index rather than a damaged existing vault. These behaviors mask backup/restore or disk corruption.

**Recommendation/acceptance:** Treat missing index/blob data as an integrity failure. Preserve the original index and offer recovery or explicitly limited read-only access; never infer a user deletion from a failed materialization. Remove one blob from a test vault and verify that unlock reports the exact missing entry without rewriting away its evidence.

### C09 — P2: Deleting all vault files is deliberately not persisted

**Evidence:** `Services/Vault.cs:250–255,279–281,290–298`.

If the index contains entries but the working directory contains no files, flush skips the commit and lock preserves the old encrypted generation. `EnsureWorkingAsync` can restore it. Deleting the final file or all files therefore causes deleted content to reappear after rematerialization/unlock.

**Recommendation/acceptance:** Track explicit mutations and working-session integrity instead of interpreting every empty folder as transient failure. Verify that deleting the last item remains deleted, while a missing/corrupt working directory enters recovery rather than being committed as empty.

### C10 — P1: A corrupt encrypted index can count as a wrong passphrase and trigger self-wipe

**Evidence:** `Services/Vault.cs:187–188,548–553`; `Services/VaultManager.cs`, `UnlockWithPassphraseAsync` catch for `CryptographicException`.

The passphrase key may unwrap correctly, but authenticated index decryption can still throw `CryptographicException`. The manager catches it as an incorrect passphrase, increments attempts, and can destroy the vault when wipe-on-failure is enabled. The code distinguishes corrupt content blobs but not corrupt index ciphertext.

**Recommendation/acceptance:** Separate authentication of the passphrase keyslot from integrity failures after successful unwrap. With a deliberately corrupted index and the correct passphrase, failed-attempt counts must remain unchanged and automatic wipe must not run.

### C11 — P1: Recursive transfer planning follows directory links without cycle protection

**Evidence:** `Services/FileTransfer.cs:373–391`; similar unguarded traversal in `Services/RecycleBin.cs:230–240`.

Transfer planning and empty-directory pruning recursively enumerate directories without checking reparse points or visited identities. The lexical destination containment check does not address junction aliases. Copying a directory with a junction can copy data outside the selected tree; a cycle can produce runaway work or stack exhaustion. The Recycle Bin's cross-volume copy has the same traversal gap.

**Recommendation/acceptance:** Define link behavior explicitly, inspect links before descent, and preserve links or require an intentional follow-links policy with cycle protection. Test directory/file symlinks, junctions to ancestors, and junctions outside the selected root.

### C12 — P2: Transfer success and cancellation accounting are incomplete

**Evidence:** `Services/FileTransfer.cs:145,229–250,261–283,383`; `MainWindow.xaml.cs:3420`.

Planning exceptions, directory-creation failures, and source-delete failures are swallowed. Successful copied files count as moved even when their originals could not be deleted. Same-volume fast moves execute before streamed copies, so cancelling the later phase does not leave every original untouched, contrary to the README's broad cancellation promise. Fallback copies from failed fast moves also bypass the earlier conflict-resolution pass.

**Recommendation/acceptance:** Return per-item outcomes covering planning, copy, commit, deletion, skip, and cancellation. Resolve conflicts for fallback operations too. Preserve failed/skipped cut items for retry and report exactly what moved before cancellation. Test unreadable subdirectories, locked sources, mixed fast/streamed moves, and a destination that appears mid-operation.

### C13 — P2: Settings writes proceed even when the mutex was not acquired

**Evidence:** `Services/AppState.cs:283–292`.

When `WaitOne(2000)` returns false, `Save` still writes the shared `state.json.tmp` and replaces the state file. That defeats serialization at the moment contention is highest. Independently, successful serialized writes still overwrite newer changes with a stale process snapshot; the source comment explicitly acknowledges last-writer-wins behavior.

**Recommendation/acceptance:** Return/retry on failure to own the mutex, use unique temporary files, and implement field-aware merging or a single state owner. Test a held mutex longer than two seconds and two processes changing different favorites/settings.

### C14 — P1: Failed image overwrite exits the editor and discards the unsaved work

**Evidence:** `MainWindow.Editor.cs:1327–1347,1352–1373`.

The successful-save and failed-replacement paths both call `ExitEditMode`. After replacement retries fail, the rendered temporary file is deleted and editor state is discarded even though the original was not updated. This finding concerns replacement failure after rendering succeeds; the earlier export-failure branch returns without that explicit editor exit.

**Scenario:** Make edits, keep the original locked by another application, then choose Overwrite. The save fails, but the user cannot retry from the retained edit state.

**Recommendation/acceptance:** Keep the editor open and dirty on any failed export/replace, with Retry and Save As available. Only close after successful commit or an explicit discard decision. Verify pixel edits and markup survive failures.

### C15 — P1: Video export overwrites the selected final file before successful completion

**Evidence:** `MainWindow.VideoEditor.cs:248–262,281–309`; `Services/FfmpegVideo.cs:204,239,252,262`.

The selected output path is passed directly to FFmpeg with `-y`. Cancelling removes that path. If the user selected an existing file, its previous content is no longer recoverable; a general export failure can leave a partial output. The save-picker replacement decision authorizes replacement, but does not make failed output a successful replacement.

**Recommendation/acceptance:** Encode into a sibling staging file, wait for process termination and successful validation, then replace the chosen output. Reject input/output identity. Cancel or fail an export over an existing file and verify the previous output remains intact.

### C16 — P1: Update force-kills the application before vault/editor work can be saved

**Evidence:** `tools/update.ps1:53–64`; `tools/install.ps1:32–49`.

The update script force-stops processes immediately. The installer asks for close but force-kills after approximately five seconds, even if a save/lock dialog or a long commit is still pending. This can trigger C02/C04 recovery failures and discard editor work. Publishing directly into the installed app directory also lacks a staged rollback if publish fails halfway through.

**Recommendation/acceptance:** Add an acknowledged graceful shutdown protocol and abort an update when shutdown is incomplete. Publish and validate a separate version before switching installation state. Test slow vault locks, unsaved edits, tray mode, and publish failure.

### C17 — P2: Vault file identity and folder structure are not fully represented

**Evidence:** `Services/Vault.cs`, `VaultEntry`, `ImportPathsAsync`, `AddToOpenVaultAsync`, and unchanged-file check at `:515`.

Only files are indexed, so empty directory structures are not preserved through import/lock/unlock. Unchanged detection uses size and modified timestamp alone; same-size edits with a preserved/restored timestamp are treated as unchanged and can be lost when the working copy is wiped.

**Recommendation/acceptance:** Represent directories if vaults support folders, and use reliable mutation tracking or content validation for dirty files. Test empty nested directories and a same-length content change with its original timestamp restored.

### C18 — P2: Wipe failures can silently degrade into plain deletion or an apparent success

**Evidence:** `Services/SecureWipe.cs:167–214`; `Services/VaultCrypto.cs:230–269`; `MainWindow.xaml.cs:3807–3813`.

Overwrite exceptions are swallowed and followed by ordinary deletion. Directory cleanup is best-effort, and the vault lock path does not verify that all plaintext was removed before clearing `WorkingDir` and the key. The vault-aware delete wrapper returns true after a wipe task without checking file removal.

**Recommendation/acceptance:** Report overwrite-completed, plain-delete-only, failed, and cancelled outcomes separately. A lock must not claim complete cleanup while sensitive plaintext remains. Validate locked files, write failures, and residual-directory cleanup; communicate the existing best-effort erase limitation in the relevant UI.

## 4. UI and user experience findings

### U01 — P1: Settings does not isolate global file-management shortcuts

**Evidence:** `MainWindow.xaml.cs:5575–5578,6108–6119,6123–6187`.

Settings cycles focus within its card, but root keyboard handlers check Peek visibility rather than Settings visibility. With focus on a non-text Settings control, Ctrl+V still invokes paste into the explorer's selected folder; Ctrl+C/X/A and other unhandled file-management shortcuts can also reach the underlying view. A modal-looking scrim is not sufficient command isolation.

**Recommendation/acceptance:** Centralize active-surface command routing and reject explorer/viewer actions while Settings or dialogs own the interaction. With files selected behind Settings, exercise keyboard shortcuts from buttons, toggles, and combo boxes; no underlying file operation should start.

### U02 — P1: Archive browsing allows changes that are silently temporary

**Evidence:** `Services/ArchiveService.cs:10–13,23–28`; `MainWindow.xaml.cs:2568–2577,3378–3382`; `_openZips` usage is limited to recording/labeling locations.

ZIP contents become ordinary writable temporary folders. Paste, rename, delete, and media editing do not enforce archive read-only status, and no repack operation writes changes back to the ZIP. Users can receive normal save/rename success feedback for changes later removed by temporary cleanup.

**Recommendation/acceptance:** Introduce location capabilities used by every command path. Show a visible read-only archive indicator, block mutation, and offer Extract/Save As. Alternatively, implement an explicit durable archive-editing workflow. Test keyboard, context-menu, drag/drop, and editor paths inside nested archive directories.

### U03 — P2: Network and large local folders block the UI; failures look empty

**Evidence:** `MainWindow.xaml.cs:1840–1846`; `Services/FileSystemService.cs:53–84,137–140`; synchronous bin call at `MainWindow.xaml.cs:3815`.

Ordinary directory listing is called synchronously from navigation, and enumeration is fully materialized before display. A slow/disconnected share can stall the window. Exceptions return an empty or partial list, followed by “0 item(s)” or an ordinary count, so users cannot distinguish empty from inaccessible. Recycling across volumes can also perform synchronous copy work on the UI thread.

**Recommendation/acceptance:** Use cancellable background enumeration/operations with generation checks, loading status, and typed error/partial-result states. Measure responsiveness for large folders, unavailable UNC paths, and cross-volume recycling. Always offer retry/navigation out of an error state.

### U04 — P2: Recursive search starts uncancellable work per keystroke and silently caps results

**Evidence:** `MainWindow.xaml.cs:4247–4251,4274–4297`; `Services/FileSystemService.cs:93–126`.

Every edit launches a full recursive scan. Stale-result suppression checks query/folder strings, but does not cancel scans, debounce input, or identify repeated queries as distinct requests. Search stops at 4,000 matches and the UI presents an ordinary result count without disclosing truncation or skipped inaccessible branches.

**Recommendation/acceptance:** Debounce input, cancel superseded scans, capture filter state per request, and use a request generation. Return truncation and skipped-location metadata. Test rapid typing/backspacing, toggle changes during search, navigation away, and more than 4,000 matches.

### U05 — P2: Starting another operation silently cancels the current one

**Evidence:** `MainWindow.xaml.cs:4658,4679`; `MainWindow.VideoEditor.cs:283`.

Transfer, wipe, and video export each invoke the current cancellation callback simply because they need the shared progress panel. A second copy can cancel an export or shred operation the user expected to continue. The old task is not awaited before the replacement begins, so cancellation and the next operation can overlap on disk.

**Recommendation/acceptance:** Represent operations independently from their cards. Queue work or support multiple operations; if only one is allowed, explicitly block the new command or obtain a replace-operation decision. Test starting a transfer during an export and during a hidden wipe.

### U06 — P2: Pixel undo history silently stops undoing older AI actions

**Evidence:** `MainWindow.Editor.cs:41,1156–1165,1180–1205`.

After three pixel snapshots, older entries remain in history but lose their pixel buffers. Undo still advances through those entries and restores parameters/markup without reversing the corresponding pixel operation. This presents an undo step that no longer performs its original meaning.

**Recommendation/acceptance:** Remove unavailable history coherently or preserve reconstructible edits; visibly communicate a history limit. Budget undo and redo together by bytes rather than only snapshot count. Apply more than three destructive AI operations, then undo/redo across the boundary and compare actual pixels.

### U07 — P2: Terminal can remain blank forever on a fresh offline installation

**Evidence:** `Assets/terminal/index.html:5–8,22–23`; `MainWindow.xaml.cs:5284–5292`.

xterm and its fit addon load from a CDN. If unavailable, JavaScript retries indefinitely without showing an error. The host marks the web view ready on navigation, before terminal initialization succeeds. This violates the expected local-first experience for a terminal.

**Recommendation/acceptance:** Bundle versioned assets and wait for a terminal-ready handshake. Show actionable initialization errors and a retry action. Validate first launch with an empty WebView2 cache and no network. Also review the trust boundary: fetched scripts can send messages that become terminal input.

### U08 — P2: Quick access uses guessed Desktop/Downloads paths

**Evidence:** `Services/FileSystemService.cs:33–47`.

Desktop and Downloads are constructed below the profile while other folders use known-folder lookup. Relocated or redirected folders can be omitted or point to the wrong location. For an Explorer replacement, these are common primary navigation destinations.

**Recommendation/acceptance:** Resolve actual Windows known folders consistently, including Downloads. Validate relocated Desktop/Downloads and a redirected profile. The screenshot destination at `MainWindow.xaml.cs:6011–6014` should follow the same policy for Pictures.

### U09 — P2: MTP paste is rejected despite device write support elsewhere

**Evidence:** `MainWindow.xaml.cs:3381`; `Services/ShellBrowser.cs`, `Upload`; device routing in `ExplorerList_Drop`.

The paste handler rejects every shell location with “Can't paste here,” although a device upload service and drag/drop routing exist. Users familiar with Explorer will expect Ctrl+V to work after copying local files into a writable phone folder.

**Recommendation/acceptance:** Route supported device paste through the shell service and expose destination capabilities consistently. If a target is read-only, disable paste and explain why. Test local-file copy/paste to a disposable device folder, including partial failures and disconnects.

### U10 — P2: Very short windows can still hide Settings actions

**Evidence:** `MainWindow.xaml.cs:5574,5587`; `MainWindow.xaml:1163` and Settings footer at `:1597–1598`.

The Settings height cap uses `Math.Max(320, availableHeight - 40)`. Below that available height it stops tracking the actual space. No minimum main-window size enforcement was found in the inspected window code. This leaves a source-level clipping risk despite the earlier resize fix.

**Recommendation/acceptance:** Clamp to actual available space and make the action row reliably reachable, or enforce a supported minimum window size. Validate small windows, display scaling, and 150%/200% text scaling. This needs rendered confirmation; exact clipping dimensions depend on layout/DPI.

## 5. Further improvements and engineering gaps

These are scoped recommendations, not claims that every associated scenario currently fails.

### I01 — P2: Establish filesystem fidelity and recovery tests

The transfer service streams primary file bytes and explicitly preserves only last-write time (`FileTransfer.cs:368`). There is no explicit preservation policy for creation time, attributes, ACLs, alternate data streams, sparse/compressed files, or cloud placeholders. Link handling is already a concrete defect in C11. Build a supported-filesystem matrix and document intentional differences before advertising broad Explorer parity.

The custom Recycle Bin also needs recovery tests for cross-volume partial copy failures, index corruption, restore collisions between files and directories, and concurrent operations. `RecycleBin.UniquePath:245` checks only the expected item kind; a file/folder type collision can make restore fail instead of choosing a safe name. Its catch-all copy fallback and removal of the index record after failed moves deserve fault injection. The unused-looking `EmptyAsync` path clears the whole index after a snapshot-based wipe; keep it out of use until concurrent additions and failed wipes are handled safely.

### I02 — P2: Bound caches, logs, and editing memory during long sessions

`ThumbDiskCache.Sweep` runs at file-manager startup, while `Store` does not enforce the 256 MiB cap. A long browsing session can exceed it until restart. Cached thumbnails of ordinary files are not automatically invalidated when files are hidden, shredded, or moved to a vault; the current exclusion prevents new vault-working-folder thumbnails but does not remove older previews. Define and implement retention behavior for privacy-sensitive operations.

`App.xaml.cs` synchronously appends every first-chance exception and logs launch paths without rotation. This can amplify expected I/O errors, grow indefinitely, and retain sensitive path metadata. Use bounded, configurable diagnostics with privacy-aware fields.

`MainWindow.Editor.cs:79–99` allocates a full-resolution BGRA selection overlay. A 50-megapixel overlay alone is about 200 MB; source buffers, masks, GPU surfaces, and history add to it. Use a common memory budget and display-resolution overlays where feasible. Measure working set and GPU memory during repeated large-image edits.

### I03 — P2: Replace blanket exception suppression with explicit recovery states

`App.xaml.cs` marks all WinUI unhandled exceptions handled. Many services similarly swallow exceptions and continue. This can keep an inconsistent application alive without informing the user. Distinguish expected per-item errors from invariant/storage failures, roll back or stop the affected operation, and preserve diagnostic evidence. Critical storage errors should not look like empty folders or successful locks.

### I04 — P3: Extract command, operation, and location models

The large MainWindow controller couples modal state, filesystem capability, privacy, keyboard routing, asynchronous work, and status text. Current archive and Settings defects illustrate the consequences. Incrementally extract navigation state, command eligibility, operation coordination, persistence, and dialog handling behind testable interfaces. Keep presentation responsible for display rather than deciding storage safety independently in each event handler.

Use explicit location types/capabilities for normal folders, archives, Recycle Bin, devices, and vaults. Avoid relying on scattered string sentinels and whichever view happens to be visible when an asynchronous continuation returns.

### I05 — P3: Add automated regression and release gates

No tracked Windows test project or CI workflow was found. Add focused tests for storage transactions, cancellation, per-item errors, name collisions, link traversal, state concurrency, vault recovery, and backup snapshots. Use UI automation for shortcut ownership, archive capability enforcement, selection preservation, editor save failure, and minimum-size behavior.

Start from an isolated, configurable app-data root; current static per-user paths make safe testing harder. Simulated filesystems/streams and controlled failures should cover difficult timing and disk-error cases without touching personal files.

Validate a clean checkout's optional runtime assets. The project conditionally includes FFmpeg/FFprobe and Google OAuth configuration; packaging should explicitly describe which features are available when these are absent. Verify the supported architecture and Windows-version matrix instead of inferring support from project declarations. No current dependency vulnerability or support-lifecycle audit was performed here.

### I06 — P3: Reconcile documentation and prior-review status

`tasks.md` still describes removed gallery flows and marks editing/video features unfinished while current code implements them. The README both describes the folder tree as implemented and lists it as planned. File comparison is described as SHA-256 hashing, while `FileTransfer.FilesIdentical` performs byte comparison. Both can establish equality, but the documentation should describe the actual behavior.

Treat `polish-review.md` as history, not a passing test suite. Examples requiring reopening include its backup-snapshot claim (C05), restore protection claim (C06/C07), atomic settings/multi-process claim (C13), and focus-modal claim (U01). Some fixes are visibly present—automation names, guest-window cleanup exclusions, a real transfer ProgressBar, and source-remapping after rename—but their presence does not establish end-to-end correctness.

## 6. Recommended validation plan

Use disposable files and an isolated app-data root. Do not run these scenarios on the user's actual vaults or photo library.

| Suite | Representative checks | Required result |
|---|---|---|
| Replacement integrity | Cancel, source read failure, disk full, destination appears after planning | Previous destination intact until a complete new file commits. |
| Move correctness | Same-volume, cross-volume, mixed batches, skipped conflict, source-delete failure | Accurate per-item outcomes; no unreported loss or false complete-move result. |
| Link semantics | File symlink, directory junction, ancestor cycle, target outside selected root | No unintended traversal or deletion outside selected scope. |
| Recycle recovery | Cross-volume partial failure, corrupted index, concurrent add/empty, restore type collision | Data remains discoverable; no unrelated item removed. |
| Vault durability | Failure at each blob/index/manifest write boundary; correct password with corrupt index | Recoverable committed generation; integrity errors never trigger password self-wipe. |
| Vault lifecycle | Two normal launches, guests, failed close-lock, locked plaintext file, updater | One clear owner; no live-folder cleanup; failed cleanup/commit visible and recoverable. |
| Vault semantics | Delete final item, empty directories, timestamp-preserving edits | Intentional changes persist faithfully. |
| Backup/restore | Edits during upload, partial download, existing/unlocked restore, malformed identifiers | Complete snapshot; validated staged restore; no path escape. |
| Search/navigation | Offline share, large folder, rapid queries, filter changes, 4,001+ matches | UI remains responsive; old requests cannot take over; incomplete results labeled. |
| Modal commands | Settings/Peek/dialogs plus copy/cut/paste/delete/open/function keys | Commands affect only the active interaction surface. |
| Media editing | Locked overwrite, failed export, cancel over existing output, deep AI undo | Unsaved edits and previous output survive failure; undo remains truthful. |
| Native integration | Explorer clipboard, device copy/paste, unplug, redirected known folders | Consistent behavior with meaningful partial/error feedback. |
| Accessibility/layout | Keyboard-only, Narrator, light/dark/high contrast, scaling, narrow/short windows | Named controls, visible focus, reachable actions, readable errors. |
| Offline/resource usage | Fresh offline terminal; prolonged browsing; large edits; repeated I/O errors | Local tools initialize, or explain failure; memory/disk/log use stays bounded. |

## 7. Suggested implementation order

1. Protect data: C01–C08, C10–C11, C14–C16, and U02. Introduce transactional writes and safe session ownership before adding more functionality.
2. Make critical state truthful: C09, C12–C13, C17–C18, U01, U05–U06. Coordinate operations, preserve failed edits, and report partial results.
3. Improve everyday usability: U03–U04 and U07–U10, then resource budgets, privacy-aware cache cleanup, and filesystem-fidelity coverage.
4. Add regression gates alongside each approved fix, then reconcile documentation and perform isolated native UI/device validation.

No implementation has been started. The next step is selecting the findings to address and agreeing on the behavioral decisions in Section 2.
