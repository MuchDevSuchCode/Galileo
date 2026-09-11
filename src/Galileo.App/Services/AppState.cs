using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Galileo.Services;

public enum SlideshowTransition
{
    None,
    Crossfade,
    KenBurns
}

/// <summary>A folder's remembered sort/group choice.</summary>
public sealed class FolderSortPref
{
    public string SortBy { get; set; } = "Name";          // Name | Date | Type | Size
    public bool SortDescending { get; set; }
    public string GroupBy { get; set; } = "None";         // None | Name | Date | Type | Size
}

/// <summary>
/// Persistent app state: which photos are hidden / favorited, plus slideshow
/// preferences. Stored as JSON under %LocalAppData%\Galileo\state.json.
/// Original image files are never modified — hidden status lives only here.
/// </summary>
public sealed class AppState
{
    // Stored with case-insensitive comparison so path casing differences don't duplicate entries.
    public HashSet<string> HiddenPaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> FavoritePaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Folders the user has app-hidden (appear empty / excluded; disk untouched).</summary>
    public HashSet<string> HiddenFolders { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>User-chosen folder thumbnail: folder path → image inside it shown as the folder preview.</summary>
    public Dictionary<string, string> FolderThumbnails { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Custom locations pinned to the sidebar (local folders, UNC shares, WSL paths).</summary>
    public List<string> PinnedPaths { get; set; } = new();

    /// <summary>Per-folder sort/group overrides (folder path → preference). Folders without an entry
    /// inherit the last-used global sort below.</summary>
    public Dictionary<string, FolderSortPref> FolderSorts { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string? LastFolder { get; set; }

    // General settings
    public string Theme { get; set; } = "System";   // System | Light | Dark | Terminal | Gray
    public bool SingleClickToOpen { get; set; }       // false = double-click (default)
    public string CollagePreset { get; set; } = "Justified"; // Justified | Grid | Hero
    public double IconSize { get; set; } = 110;               // explorer icon size (Medium)
    public string ExplorerViewMode { get; set; } = "Medium";  // Large | Medium | Small | Details
    public double SidebarWidth { get; set; } = 240;           // resizable sidebar (nav) width
    public bool FolderPreviews { get; set; } = true;          // paint content previews on folders
    public bool ShowExtensions { get; set; } = true;          // show file extensions in the explorer
    public bool PeekEnabled { get; set; } = true;             // Spacebar Quick Look preview in the explorer
    public bool ShowAlbumArt { get; set; } = true;            // show embedded cover art when playing audio
    public bool StartVideoMuted { get; set; }                 // new videos begin muted (off by default)

    /// <summary>Remembered playback audio state, restored when the next video/audio plays:
    /// muted stays muted, otherwise the last volume (unless "Start videos muted" forces a mute).</summary>
    public bool VideoMuted { get; set; }
    public double VideoVolume { get; set; } = 100;
    public string SortBy { get; set; } = "Name";              // Name | Date | Type | Size
    public bool SortDescending { get; set; }
    public string GroupBy { get; set; } = "None";             // None | Name | Date | Type | Size

    /// <summary>Reuse a single window for files opened from the shell (off by default).</summary>
    public bool SingleInstance { get; set; }

    /// <summary>Always open photos/videos in a separate window instead of the in-app viewer (off by default).</summary>
    public bool AlwaysOpenMediaInNewWindow { get; set; }

    /// <summary>Last position/size of a photo/media viewer window ("open in new window"), so the next one
    /// opens where the user left it (e.g. a second monitor). Width 0 = never saved.</summary>
    public int PhotoWinX { get; set; }
    public int PhotoWinY { get; set; }
    public int PhotoWinW { get; set; }
    public int PhotoWinH { get; set; }

    /// <summary>While viewing a single photo/video, the window's close button goes back to the explorer
    /// instead of quitting the app (off by default).</summary>
    public bool CloseToViewerBack { get; set; }

    /// <summary>Require Windows Hello / PIN before revealing the Hidden album or app-hidden folders.</summary>
    public bool LockHiddenAlbum { get; set; }

    // Secure vault
    /// <summary>Auto-lock an unlocked vault after this many seconds of inactivity (0 = never).</summary>
    public int VaultIdleSeconds { get; set; } = 300;
    /// <summary>Offer/enable Windows Hello by default when creating a vault.</summary>
    public bool VaultDefaultUseHello { get; set; }
    /// <summary>Permanently wipe a vault after too many wrong passphrase attempts.</summary>
    public bool VaultWipeOnFailure { get; set; }
    /// <summary>Number of consecutive wrong passphrases that triggers a wipe (when enabled).</summary>
    public int VaultWipeAfterAttempts { get; set; } = 10;
    /// <summary>Hide the vault entry from the sidebar entirely; open a vault with Ctrl+Alt+V instead
    /// (deniability — nothing in the UI hints a vault exists). On by default.</summary>
    public bool HideVaultEntry { get; set; } = true;
    /// <summary>UTC ticks of the last successful Google Drive vault backup (0 = never).</summary>
    public long LastVaultBackupUtcTicks { get; set; }

    /// <summary>Automatic vault backup cadence: Off | Daily | Weekly. Runs while the app is open and
    /// signed in to Google Drive (it backs up on launch/while running once a backup is overdue).</summary>
    public string BackupSchedule { get; set; } = "Off";

    /// <summary>Keep Galileo running in the system tray when its window is closed. The window is
    /// hidden, not exited; quit from the tray menu.</summary>
    public bool RunInBackground { get; set; }

    /// <summary>Launch Galileo at sign-in (minimized to the tray).</summary>
    public bool StartWithWindows { get; set; }

    // Developer mode (embedded terminal)
    public bool DeveloperMode { get; set; }
    public string TerminalShell { get; set; } = "cmd";   // cmd | powershell | wsl

    // Secure delete: overwrite method used when shredding / Shift+Delete (and emptying the bin when
    // the toggle below is on). Right-click "Secure delete" always overwrites regardless of the toggle.
    // Zero | Random | Dod3 | Dod7 | Gutmann35
    public string WipeMethod { get; set; } = "Random";

    /// <summary>Overwrite (secure-wipe) files when emptying the Recycle Bin, instead of a plain delete (off by default).</summary>
    public bool SecureDeleteOnEmpty { get; set; }

    /// <summary>After converting an image to another format, move the original to the Recycle Bin (on by default).</summary>
    public bool ConvertRemovesOriginal { get; set; } = true;

    /// <summary>Re-hide app-hidden folders whenever Galileo loses focus / goes to the background; reveal them
    /// again with the Show app-hidden toggle. Off by default.</summary>
    public bool HideOnBackground { get; set; }

    // Slideshow settings
    public int SlideshowSeconds { get; set; } = 4;
    public bool SlideshowShuffle { get; set; }
    public bool SlideshowLoop { get; set; } = true;
    public SlideshowTransition SlideshowTransition { get; set; } = SlideshowTransition.Crossfade;

    /// <summary>When true, <see cref="Save"/> is a no-op — used while the Settings dialog is open so
    /// live edits don't persist until the user clicks Save.</summary>
    [JsonIgnore] // runtime-only flag — serializing it would also make settings-dirty fingerprints lie
    public bool SuppressSave { get; set; }

    /// <summary>Serialized snapshot for change detection (e.g. "does Settings have unsaved edits?").</summary>
    public string Fingerprint() => JsonSerializer.Serialize(this, Options);

    [JsonIgnore]
    private static string StatePath
    {
        get
        {
            var dir = AppPaths.Root;
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "state.json");
        }
    }

    // Pre-rename location (the app was formerly "PhotosPlus"); migrated once on first launch.
    [JsonIgnore]
    private static string LegacyStatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PhotosPlus", "state.json");

    /// <summary>If no Galileo state exists yet but an old PhotosPlus one does, carry it over.</summary>
    private static void MigrateLegacyState()
    {
        try
        {
            if (!File.Exists(StatePath) && File.Exists(LegacyStatePath))
                File.Copy(LegacyStatePath, StatePath);
        }
        catch
        {
            // Migration is best-effort; a failure just means defaults.
        }
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static AppState Load()
    {
        try
        {
            MigrateLegacyState();
            if (ReadStateFile() is { } state)
            {
                // Prune per-folder prefs for folders that are really gone (the map otherwise grows
                // forever) — but never for UNC/offline volumes, which are merely unreachable now.
                foreach (var k in state.FolderSorts.Keys.ToList())
                {
                    try
                    {
                        var root = Path.GetPathRoot(k);
                        if (!string.IsNullOrEmpty(root) && !k.StartsWith(@"\\", StringComparison.Ordinal)
                            && Directory.Exists(root) && !Directory.Exists(k))
                            state.FolderSorts.Remove(k);
                    }
                    catch { /* leave the entry */ }
                }
                state._baseline = state.Clone();
                return state;
            }
        }
        catch
        {
            // Corrupt state — fall back to defaults rather than crash.
        }
        var fresh = new AppState();
        fresh._baseline = fresh.Clone();
        return fresh;
    }

    /// <summary>Reads and rehydrates state.json (case-insensitive sets restored); null if absent/corrupt.</summary>
    private static AppState? ReadStateFile()
    {
        if (!File.Exists(StatePath)) return null;
        var state = JsonSerializer.Deserialize<AppState>(File.ReadAllText(StatePath), Options);
        if (state is null) return null;
        // Rehydrate sets as case-insensitive (deserializer loses the comparer).
        state.HiddenPaths = new HashSet<string>(state.HiddenPaths, StringComparer.OrdinalIgnoreCase);
        state.FavoritePaths = new HashSet<string>(state.FavoritePaths, StringComparer.OrdinalIgnoreCase);
        state.HiddenFolders = new HashSet<string>(state.HiddenFolders ?? new(), StringComparer.OrdinalIgnoreCase);
        state.FolderThumbnails = new Dictionary<string, string>(state.FolderThumbnails ?? new(), StringComparer.OrdinalIgnoreCase);
        state.FolderSorts = new Dictionary<string, FolderSortPref>(state.FolderSorts ?? new(), StringComparer.OrdinalIgnoreCase);
        return state;
    }

    // Cross-process guard: "open in new window" can run as a second process sharing this file. A plain
    // WriteAllText raced from two processes tears the JSON — and a torn file makes Load() fall back to
    // a FRESH default state (every favorite/pin/pref gone). Atomic replace + mutex prevents the tear;
    // concurrent writers still last-write-win on content, which loses far less than total corruption.
    private static readonly System.Threading.Mutex SaveMutex = new(false, "Galileo.StateSave");

    /// <summary>Re-keys every path-keyed setting after an on-disk rename so hidden flags, favorites,
    /// chosen thumbnails, remembered sorts and pins follow the item to its new path — renaming a hidden
    /// folder must not silently un-hide it (and a future folder at the old path must not inherit the
    /// flag). Prefix-rewrites descendants of a renamed folder too.</summary>
    public void RepathEntry(string oldPath, string newPath)
    {
        if (string.IsNullOrEmpty(oldPath) || string.IsNullOrEmpty(newPath)) return;
        oldPath = oldPath.TrimEnd('\\');
        newPath = newPath.TrimEnd('\\');

        string? Map(string p) =>
            p.Equals(oldPath, StringComparison.OrdinalIgnoreCase) ? newPath
            : p.StartsWith(oldPath + "\\", StringComparison.OrdinalIgnoreCase) ? newPath + p[oldPath.Length..]
            : null;

        void RekeySet(HashSet<string> set)
        {
            foreach (var p in set.Where(p => Map(p) is not null).ToList()) { set.Remove(p); set.Add(Map(p)!); }
        }
        RekeySet(HiddenPaths);
        RekeySet(FavoritePaths);
        RekeySet(HiddenFolders);

        foreach (var k in FolderThumbnails.Keys.Where(k => Map(k) is not null).ToList())
        {
            var v = FolderThumbnails[k]; FolderThumbnails.Remove(k); FolderThumbnails[Map(k)!] = v;
        }
        foreach (var k in FolderThumbnails.Keys.ToList())   // the VALUE is an image path — follow it too
            if (Map(FolderThumbnails[k]) is { } nv) FolderThumbnails[k] = nv;

        foreach (var k in FolderSorts.Keys.Where(k => Map(k) is not null).ToList())
        {
            var v = FolderSorts[k]; FolderSorts.Remove(k); FolderSorts[Map(k)!] = v;
        }
        for (var i = 0; i < PinnedPaths.Count; i++)
            if (Map(PinnedPaths[i]) is { } np) PinnedPaths[i] = np;

        Save();
    }

    // Snapshot of the state as of the last load/save. Lets Save() do a 3-way merge: what changed on
    // disk since (another process) vs what changed in memory since (this process). Never serialized.
    [JsonIgnore]
    private AppState? _baseline;

    public void Save()
    {
        if (SuppressSave) return;
        try
        {
            var owned = false;
            try
            {
                owned = SaveMutex.WaitOne(2000);
                if (!owned) owned = SaveMutex.WaitOne(3000); // one longer retry while contention drains
            }
            catch (System.Threading.AbandonedMutexException) { owned = true; } // prior holder died — lock is ours
            try
            {
                // Adopt changes other processes have saved since our last load/save (3-way merge
                // against the baseline snapshot). A plain write-out of this process's copy silently
                // rolled the other window's favorites/pins/settings back to our stale view of them.
                try
                {
                    if (_baseline is not null && ReadStateFile() is { } disk)
                        MergeExternalChanges(_baseline, disk);
                }
                catch { /* merge is best-effort; worst case is the old last-writer-wins */ }

                // Per-process temp name: even if the mutex could not be acquired (another writer is
                // wedged), two processes never scribble into the SAME .tmp — each write stays atomic
                // (worst case is last-writer-wins on content, never a torn state file).
                var tmp = $"{StatePath}.{Environment.ProcessId}.tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(this, Options));
                if (File.Exists(StatePath)) File.Replace(tmp, StatePath, null);
                else File.Move(tmp, StatePath);
                _baseline = Clone(); // what's on disk now IS this state — future diffs start here
            }
            finally { if (owned) SaveMutex.ReleaseMutex(); }
        }
        catch
        {
            // Best-effort persistence; ignore IO errors.
        }
    }

    /// <summary>Element-level 3-way merge: for every set entry, dictionary key, pin, and scalar
    /// setting, a change made on disk (by another process) since <paramref name="baseline"/> is
    /// adopted into this state UNLESS this process changed the same element itself (ours wins).</summary>
    private void MergeExternalChanges(AppState baseline, AppState disk)
    {
        MergeSet(HiddenPaths, baseline.HiddenPaths, disk.HiddenPaths);
        MergeSet(FavoritePaths, baseline.FavoritePaths, disk.FavoritePaths);
        MergeSet(HiddenFolders, baseline.HiddenFolders, disk.HiddenFolders);

        MergeDict(FolderThumbnails, baseline.FolderThumbnails, disk.FolderThumbnails,
            (a, b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase));
        MergeDict(FolderSorts, baseline.FolderSorts, disk.FolderSorts,
            (a, b) => a.SortBy == b.SortBy && a.SortDescending == b.SortDescending && a.GroupBy == b.GroupBy);

        // Pins: set semantics with this process's ordering preserved; their additions append.
        var cmp = StringComparer.OrdinalIgnoreCase;
        var basePins = new HashSet<string>(baseline.PinnedPaths, cmp);
        var minePins = new HashSet<string>(PinnedPaths, cmp);
        foreach (var p in disk.PinnedPaths)
            if (!basePins.Contains(p) && !minePins.Contains(p)) PinnedPaths.Add(p);          // they added
        foreach (var p in basePins)
            if (!disk.PinnedPaths.Contains(p, cmp) && minePins.Contains(p))
                PinnedPaths.RemoveAll(x => cmp.Equals(x, p));                                // they removed, we didn't touch

        // Scalar settings (bool/number/string/enum properties): theirs wins only where we didn't change it.
        foreach (var prop in typeof(AppState).GetProperties())
        {
            if (!prop.CanRead || !prop.CanWrite) continue;
            if (prop.GetCustomAttributes(typeof(JsonIgnoreAttribute), inherit: false).Length > 0) continue;
            var t = prop.PropertyType;
            if (!(t.IsPrimitive || t.IsEnum || t == typeof(string))) continue;
            var mine = prop.GetValue(this);
            var bas = prop.GetValue(baseline);
            var theirs = prop.GetValue(disk);
            if (Equals(mine, bas) && !Equals(theirs, bas)) prop.SetValue(this, theirs);
        }
    }

    private static void MergeSet(HashSet<string> mine, HashSet<string> baseline, HashSet<string> theirs)
    {
        var bas = new HashSet<string>(baseline, StringComparer.OrdinalIgnoreCase);
        var oth = new HashSet<string>(theirs, StringComparer.OrdinalIgnoreCase);
        foreach (var e in oth)
            if (!bas.Contains(e)) mine.Add(e);                          // they added
        foreach (var e in bas)
            if (!oth.Contains(e) && mine.Contains(e)) mine.Remove(e);   // they removed, we didn't
    }

    private static void MergeDict<T>(Dictionary<string, T> mine, Dictionary<string, T> baseline,
        Dictionary<string, T> theirs, Func<T, T, bool> eq)
    {
        var keys = new HashSet<string>(baseline.Keys.Concat(theirs.Keys).Concat(mine.Keys), StringComparer.OrdinalIgnoreCase);
        foreach (var k in keys)
        {
            var hasB = baseline.TryGetValue(k, out var bv);
            var hasT = theirs.TryGetValue(k, out var tv);
            var hasM = mine.TryGetValue(k, out var mv);
            var theirsChanged = hasB != hasT || (hasB && hasT && !eq(bv!, tv!));
            var mineChanged = hasB != hasM || (hasB && hasM && !eq(bv!, mv!));
            if (theirsChanged && !mineChanged)
            {
                if (hasT) mine[k] = tv!; else mine.Remove(k);
            }
        }
    }

    /// <summary>A snapshot used to revert edits when the user cancels the Settings dialog (and as the
    /// merge baseline in <see cref="Save"/>).</summary>
    public AppState Clone()
    {
        var copy = JsonSerializer.Deserialize<AppState>(JsonSerializer.Serialize(this, Options), Options) ?? new AppState();
        copy.SuppressSave = false;
        // Rehydrate the case-insensitive comparers the round-trip loses.
        copy.HiddenPaths = new HashSet<string>(copy.HiddenPaths, StringComparer.OrdinalIgnoreCase);
        copy.FavoritePaths = new HashSet<string>(copy.FavoritePaths, StringComparer.OrdinalIgnoreCase);
        copy.HiddenFolders = new HashSet<string>(copy.HiddenFolders, StringComparer.OrdinalIgnoreCase);
        copy.FolderThumbnails = new Dictionary<string, string>(copy.FolderThumbnails, StringComparer.OrdinalIgnoreCase);
        copy.FolderSorts = new Dictionary<string, FolderSortPref>(copy.FolderSorts, StringComparer.OrdinalIgnoreCase);
        return copy;
    }

    /// <summary>Copies the user-facing setting values (not the path sets) from another instance.</summary>
    public void CopySettingsFrom(AppState o)
    {
        Theme = o.Theme;
        SingleClickToOpen = o.SingleClickToOpen;
        CollagePreset = o.CollagePreset;
        IconSize = o.IconSize;
        ExplorerViewMode = o.ExplorerViewMode;
        FolderPreviews = o.FolderPreviews;
        ShowExtensions = o.ShowExtensions;
        PeekEnabled = o.PeekEnabled;
        ShowAlbumArt = o.ShowAlbumArt;
        StartVideoMuted = o.StartVideoMuted;
        SingleInstance = o.SingleInstance;
        AlwaysOpenMediaInNewWindow = o.AlwaysOpenMediaInNewWindow;
        CloseToViewerBack = o.CloseToViewerBack;
        LockHiddenAlbum = o.LockHiddenAlbum;
        VaultIdleSeconds = o.VaultIdleSeconds;
        VaultDefaultUseHello = o.VaultDefaultUseHello;
        VaultWipeOnFailure = o.VaultWipeOnFailure;
        VaultWipeAfterAttempts = o.VaultWipeAfterAttempts;
        HideVaultEntry = o.HideVaultEntry;
        SlideshowSeconds = o.SlideshowSeconds;
        SlideshowShuffle = o.SlideshowShuffle;
        SlideshowLoop = o.SlideshowLoop;
        SlideshowTransition = o.SlideshowTransition;
        DeveloperMode = o.DeveloperMode;
        TerminalShell = o.TerminalShell;
        WipeMethod = o.WipeMethod;
        SecureDeleteOnEmpty = o.SecureDeleteOnEmpty;
        ConvertRemovesOriginal = o.ConvertRemovesOriginal;
        HideOnBackground = o.HideOnBackground;
        BackupSchedule = o.BackupSchedule;
        RunInBackground = o.RunInBackground;
        StartWithWindows = o.StartWithWindows;
    }
}
