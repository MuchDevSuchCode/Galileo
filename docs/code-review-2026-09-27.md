# Galileo code and UI/UX review — 2026-09-27

The desktop application builds successfully, but this review found ten actionable defects, including four data-loss scenarios reproduced against the service code. Fix the move engine and vault lifecycle/deletion paths before further UI polish.

## Scope and verification

- Reviewed the Windows app's file transfers, recycle/delete workflows, vault lifecycle, backup restore, image saving, navigation/keyboard handlers, and representative XAML layouts. This is a focused review, not an exhaustive audit of every feature. The Android companion and relay server were not comprehensively reviewed.
- `dotnet build src/Galileo.App/Galileo.App.csproj --no-restore -p:Platform=x64 --verbosity minimal`: passed, zero warnings/errors.
- Existing test suite: **24 passed, 0 failed**.
- Four additional regression checks: **all four failed on the expected data-preservation assertions**, confirming the defects below. Tests used temporary files and the test suite's isolated app-data root, not real user files/vaults.
- UI/UX conclusions are from source inspection. Native visual rendering, Narrator behavior, DPI behavior, and mouse/keyboard walkthroughs were not exercised.
- No production code changed. Reproduction source is preserved in [review-regressions-2026-09-27.cs](C:/code/Galileo/docs/review-regressions-2026-09-27.cs), outside the compiled test project so it does not leave the ordinary suite failing.

Priorities: **P1** = data loss, privacy, or destructive-action semantics requiring prompt correction; **P2** = material correctness/usability defect.

## Findings

### 1. P1 — Cancelling a move deletes a pre-existing replacement destination

Source: [FileTransfer.cs:302](C:/code/Galileo/src/Galileo.App/Services/FileTransfer.cs:302), [commit at line 408](C:/code/Galileo/src/Galileo.App/Services/FileTransfer.cs:408).

Move two files into a destination containing same-named files, choose Replace, and cancel after the first file completes. The first replacement destroys the old destination without keeping a backup. Cancellation then deletes the replacement through `copiedDests`, leaving no destination file at all. Source files survive, but the original destination content is lost.

**Verified:** `CancelMoveAfterReplacement_PreservesOldDestination` failed because the old destination path no longer existed.

**Fix:** Keep a rollback backup for each overwritten destination until the move batch commits. Rollback must restore overwritten files and remove only newly created files; it cannot treat both cases identically.

### 2. P1 — Same-named sources overwrite each other and both originals are deleted

Source: [FileTransfer.cs:192](C:/code/Galileo/src/Galileo.App/Services/FileTransfer.cs:192).

Move `one/a.txt` and `two/a.txt` into a directory already containing `a.txt`, choosing Replace for the conflicts. Each conflict is planned against the same old destination. The Overwrite branch ignores whether `claimedDests.Add` succeeds, so both copies retain the identical target. The second overwrites the first, and move finalization deletes both source files. This can arise from recursive search results or a multi-directory clipboard selection.

**Verified:** `MoveTwoSameNamedSources_PreservesBothContents` reported zero transfer errors, but only `SECOND` remained anywhere in the test tree; `FIRST` was lost.

**Fix:** Resolve duplicate destinations within the batch before consulting on-disk conflicts. Reserve every final target and use Keep both or an explicit conflict against the preceding planned source.

### 3. P1 — Cancellation can delete an unrelated file that appeared during copying

Source: [FileTransfer.cs:411](C:/code/Galileo/src/Galileo.App/Services/FileTransfer.cs:411), [rollback bookkeeping at line 277](C:/code/Galileo/src/Galileo.App/Services/FileTransfer.cs:277).

If a destination appears after planning, `CopyFile` correctly chooses a unique target, but that target stays in a local variable. The caller records the original `op.Dest`. Cancelling the move subsequently deletes the unrelated file at the original path and leaves the actual copied file behind.

**Verified:** `CancelMoveAfterDestinationAppears_DoesNotDeleteUnrelatedFile` creates a competing file during copying, then cancels after the first completed copy. Rollback removed the competing file. Read handles in the test force the same-volume rename to fall back to the streamed-copy branch.

**Fix:** Return the actual committed destination, together with its rollback metadata, and record that result rather than the planned path. Revalidate ownership when rolling back.

### 4. P1 — A second vault owner destroys the first owner's unsaved edits

Source: [Vault.cs:579](C:/code/Galileo/src/Galileo.App/Services/Vault.cs:579), [VaultManager.cs:60](C:/code/Galileo/src/Galileo.App/Services/VaultManager.cs:60).

Two ordinary Galileo processes are allowed when single-instance mode is off. Each has its own manager and semaphore, but both use `.work/<vault-id>`. Unlocking the same vault in the second process unconditionally wipes that shared directory before decrypting the last committed contents. The startup `IsOnlyInstance` guard does not cover this explicit unlock path.

**Verified at service level:** `SecondVaultOwner_DoesNotEraseFirstOwnersUnsavedEdits` used two independently loaded vault objects. After the second unlocked, the first owner's `UNSAVED EDIT` had become `ORIGINAL`. This models the separate owners in two processes; the native two-process UI was not exercised.

**Fix:** Acquire a cross-process, per-vault ownership lock before touching its working directory and hold it for the whole unlocked session. Reject or redirect a second unlock. Recovery must distinguish abandoned ownership from a live session.

### 5. P1 — Deleting a vault photo in the viewer leaks plaintext into the normal Recycle Bin

Source: [MainWindow.xaml.cs:4528](C:/code/Galileo/src/Galileo.App/MainWindow.xaml.cs:4528), [vault-aware helper at line 4146](C:/code/Galileo/src/Galileo.App/MainWindow.xaml.cs:4146).

Open an image from an unlocked vault and use the viewer's Delete command. `DeleteItemAsync` calls `_bin.MoveToBin` directly, bypassing `BinOrShredVaultAwareAsync`. This moves decrypted content out of the vault working directory and into the ordinary, unencrypted bin store, where locking the vault does not remove it. The viewer delete path also omits explicit-deletion tracking, so deleting the last vault photo can resurrect it at the next unlock through the empty-working-directory safety guard.

**Evidence:** Traced from `Delete_Click` through `DeleteItemAsync`; vault images are deliberately opened in the current viewer. Not a native UI reproduction.

**Fix:** Centralize deletion policy and outcomes for explorer and viewer commands. Apply vault-aware deletion, explicit-deletion tracking, and relevant cache invalidation consistently.

### 6. P1 — Vault deletion confirmation promises recovery for an irreversible operation

Source: [MainWindow.xaml.cs:4232](C:/code/Galileo/src/Galileo.App/MainWindow.xaml.cs:4232), [single-item confirmation at line 4189](C:/code/Galileo/src/Galileo.App/MainWindow.xaml.cs:4189).

In the vault explorer, ordinary Delete asks to move items to the Recycle Bin. After confirmation, the vault-aware helper securely shreds them instead. The multi-selection path then says they were moved to the bin. A user consenting to a recoverable removal is instead performing irreversible deletion.

**Fix:** Determine the actual deletion policy before building the confirmation. Vault items need an explicit permanent-deletion message and matching button/status text, or an encrypted in-vault trash mechanism. For mixed selections, explain the different outcomes and default destructive confirmations to Cancel.

### 7. P2 — Save a copy inside an archive reports success for a disposable file

Source: [MainWindow.Editor.cs:1509](C:/code/Galileo/src/Galileo.App/MainWindow.Editor.cs:1509).

Browse a ZIP, open an image, edit it, and choose the default Save a copy action. The destination is always a sibling of `_editPath`, so the edit is saved below the extracted `.zip` temp root. The editor closes and reports Saved, but startup cleanup deletes that directory. The Overwrite action has an archive guard; Save a copy, including the unsaved-changes prompt's save action, does not.

**Fix:** For extracted archive/device temporary sources, require a persistent Save As destination. Close the editor only after that save succeeds; make the destination visible in the success message.

### 8. P2 — HEIC, WebP and RAW edits are written as JPEG with the original extension

Source: [ImageEditor.cs:385](C:/code/Galileo/src/Galileo.App/Services/ImageEditor.cs:385), [MainWindow.Editor.cs:1513](C:/code/Galileo/src/Galileo.App/MainWindow.Editor.cs:1513), [overwrite path at line 1590](C:/code/Galileo/src/Galileo.App/MainWindow.Editor.cs:1590).

The editor accepts these input formats, and Save a copy retains the input extension. `FormatFor` only handles PNG/BMP/GIF/TIFF explicitly and falls back to JPEG for everything else. A successful HEIC save therefore creates JPEG bytes under a `.heic` filename; Overwrite original can similarly replace a RAW file with JPEG bytes under its RAW extension. This misrepresents the format and loses format-specific information.

**Fix:** Separate decode support from encode support. Offer a supported export format and matching extension, and disallow original-format overwrite when no matching encoder exists. Explicitly communicate conversion and loss of RAW editability/transparency where applicable.

### 9. P1 — Restore deletes the local rollback before proving the encrypted backup is usable

Source: [GoogleDriveBackup.cs:304](C:/code/Galileo/src/Galileo.App/Services/GoogleDriveBackup.cs:304), [rollback deletion at line 312](C:/code/Galileo/src/Galileo.App/Services/GoogleDriveBackup.cs:312).

Restore checks that the manifest parses and an `index.enc` file exists, swaps the backup into place, and calls `Vault.Load` before deleting the previous local store. `Vault.Load` only parses the manifest; it does not authenticate/decrypt the index or verify referenced blobs. A truncated index or missing blobs can pass these checks and cause a healthy local vault's rollback copy to be deleted. The fault is in local validation, independent of whether the Drive download itself completes successfully.

**Evidence:** Static trace of restore and `Vault.Load`; no real cloud account was accessed.

**Fix:** Retain the old store until a successful unlock and full integrity validation of the restored index/blobs. If validation requires a passphrase, defer destructive cleanup until that point. Preserve recovery copies across failed or interrupted attempts.

### 10. P2 — Multi-delete reports every selected item as successfully recycled after failures

Source: [MainWindow.xaml.cs:4252](C:/code/Galileo/src/Galileo.App/MainWindow.xaml.cs:4252).

The loop ignores the boolean returned by `BinOrShredVaultAwareAsync`, catches exceptions, and then unconditionally reports that all selected items moved to the bin. Locked files, denied access, or cross-volume recycle failures therefore produce a success message. Folder reload can also replace operation feedback with a generic count.

**Fix:** Aggregate successes, failures, and permanent deletions from structured operation results. Present a persistent partial-failure summary with failed filenames and retry actions; navigation/selection updates should not erase that outcome.

## Additional UI/UX improvement opportunities

These are code-based design recommendations, not visually verified defects.

1. **Add a compact explorer toolbar.** [MainWindow.xaml:254](C:/code/Galileo/src/Galileo.App/MainWindow.xaml:254) puts a fixed-width search box, sort/group controls, several icon buttons, and a fixed-width slider in an `Auto` column. Only the left command group can scroll, and its scrollbar is hidden. Narrow/snap layouts, a wide sidebar, or the terminal pane can consume the remaining room. Use an overflow command menu, a flexible search field, and explicit compact breakpoints; validate at 800/1024 logical-pixel widths and 150%/200% scaling.
2. **Distinguish privacy controls by visible purpose.** The adjacent app-hidden and Windows-hidden toggles use the same eye glyph ([MainWindow.xaml:316](C:/code/Galileo/src/Galileo.App/MainWindow.xaml:316)). Tooltips and automation names help, but persistent labels or an explicit visibility menu would make the two policies clearer. Distinguish concealment from encryption in the UI.
3. **Make operation results durable and actionable.** The shared status text is used for navigation counts, selection counts, progress, errors, and success messages. Use a compact operation-history surface or InfoBar for failures and destructive outcomes, with an accessible announcement and a path to retry/view details. Keep ordinary selection counts separate.

## Suggested implementation order

1. Correct transfer commit/rollback ownership and preserve overwritten destinations; adopt the first three reproduction tests.
2. Add per-vault cross-process ownership; unify delete policy across viewer/explorer and repair confirmation wording.
3. Preserve restore rollback until full validation; correct archive saves and output-format selection.
4. Add structured operation outcomes and compact toolbar behavior, followed by native keyboard/Narrator/DPI walkthroughs.

To rerun the reproductions, temporarily copy `docs/review-regressions-2026-09-27.cs` into `src/Galileo.Tests/ReviewRegressionTests.cs`, then run:

```powershell
dotnet test src/Galileo.Tests/Galileo.Tests.csproj --no-restore --filter FullyQualifiedName~ReviewRegressionTests --verbosity minimal
```

The assertions express the desired safe behavior and currently fail. Remove the temporary test-project copy after reproducing, or retain it as regression coverage when implementing the fixes. The source uses the existing test suite's isolated app-data configuration.

