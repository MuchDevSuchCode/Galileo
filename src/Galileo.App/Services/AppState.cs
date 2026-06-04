using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Galileo.Services;

public enum SlideshowTransition
{
    None,
    Crossfade,
    KenBurns
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

    public string? LastFolder { get; set; }

    // General settings
    public string Theme { get; set; } = "System";   // System | Light | Dark | Terminal | Gray
    public bool SingleClickToOpen { get; set; }       // false = double-click (default)
    public string CollagePreset { get; set; } = "Justified"; // Justified | Grid | Hero
    public double IconSize { get; set; } = 110;               // explorer icon size (Medium)
    public bool FolderPreviews { get; set; } = true;          // paint content previews on folders
    public bool ShowExtensions { get; set; } = true;          // show file extensions in the explorer
    public bool PeekEnabled { get; set; } = true;             // Spacebar Quick Look preview in the explorer
    public bool ShowAlbumArt { get; set; } = true;            // show embedded cover art when playing audio
    public string SortBy { get; set; } = "Name";              // Name | Date | Type | Size
    public bool SortDescending { get; set; }
    public string GroupBy { get; set; } = "None";             // None | Name | Date | Type | Size

    /// <summary>Reuse a single window for files opened from the shell (off by default).</summary>
    public bool SingleInstance { get; set; }

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
    /// <summary>UTC ticks of the last successful Google Drive vault backup (0 = never).</summary>
    public long LastVaultBackupUtcTicks { get; set; }

    // Slideshow settings
    public int SlideshowSeconds { get; set; } = 4;
    public bool SlideshowShuffle { get; set; }
    public bool SlideshowLoop { get; set; } = true;
    public SlideshowTransition SlideshowTransition { get; set; } = SlideshowTransition.Crossfade;

    /// <summary>When true, <see cref="Save"/> is a no-op — used while the Settings dialog is open so
    /// live edits don't persist until the user clicks Save.</summary>
    [JsonIgnore]
    public bool SuppressSave { get; set; }

    [JsonIgnore]
    private static string StatePath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Galileo");
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
            if (File.Exists(StatePath))
            {
                var json = File.ReadAllText(StatePath);
                var state = JsonSerializer.Deserialize<AppState>(json, Options);
                if (state is not null)
                {
                    // Rehydrate sets as case-insensitive (deserializer loses the comparer).
                    state.HiddenPaths = new HashSet<string>(state.HiddenPaths, StringComparer.OrdinalIgnoreCase);
                    state.FavoritePaths = new HashSet<string>(state.FavoritePaths, StringComparer.OrdinalIgnoreCase);
                    state.HiddenFolders = new HashSet<string>(state.HiddenFolders ?? new(), StringComparer.OrdinalIgnoreCase);
                    state.FolderThumbnails = new Dictionary<string, string>(state.FolderThumbnails ?? new(), StringComparer.OrdinalIgnoreCase);
                    return state;
                }
            }
        }
        catch
        {
            // Corrupt state — fall back to defaults rather than crash.
        }
        return new AppState();
    }

    public void Save()
    {
        if (SuppressSave) return;
        try
        {
            File.WriteAllText(StatePath, JsonSerializer.Serialize(this, Options));
        }
        catch
        {
            // Best-effort persistence; ignore IO errors.
        }
    }

    /// <summary>A snapshot used to revert edits when the user cancels the Settings dialog.</summary>
    public AppState Clone()
    {
        var copy = JsonSerializer.Deserialize<AppState>(JsonSerializer.Serialize(this, Options), Options) ?? new AppState();
        copy.SuppressSave = false;
        return copy;
    }

    /// <summary>Copies the user-facing setting values (not the path sets) from another instance.</summary>
    public void CopySettingsFrom(AppState o)
    {
        Theme = o.Theme;
        SingleClickToOpen = o.SingleClickToOpen;
        CollagePreset = o.CollagePreset;
        IconSize = o.IconSize;
        FolderPreviews = o.FolderPreviews;
        ShowExtensions = o.ShowExtensions;
        PeekEnabled = o.PeekEnabled;
        ShowAlbumArt = o.ShowAlbumArt;
        SingleInstance = o.SingleInstance;
        LockHiddenAlbum = o.LockHiddenAlbum;
        VaultIdleSeconds = o.VaultIdleSeconds;
        VaultDefaultUseHello = o.VaultDefaultUseHello;
        VaultWipeOnFailure = o.VaultWipeOnFailure;
        VaultWipeAfterAttempts = o.VaultWipeAfterAttempts;
        SlideshowSeconds = o.SlideshowSeconds;
        SlideshowShuffle = o.SlideshowShuffle;
        SlideshowLoop = o.SlideshowLoop;
        SlideshowTransition = o.SlideshowTransition;
    }
}
