using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhotosPlus.Services;

public enum SlideshowTransition
{
    None,
    Crossfade,
    KenBurns
}

/// <summary>
/// Persistent app state: which photos are hidden / favorited, plus slideshow
/// preferences. Stored as JSON under %LocalAppData%\PhotosPlus\state.json.
/// Original image files are never modified — hidden status lives only here.
/// </summary>
public sealed class AppState
{
    // Stored with case-insensitive comparison so path casing differences don't duplicate entries.
    public HashSet<string> HiddenPaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> FavoritePaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string? LastFolder { get; set; }

    // Slideshow settings
    public int SlideshowSeconds { get; set; } = 4;
    public bool SlideshowShuffle { get; set; }
    public bool SlideshowLoop { get; set; } = true;
    public SlideshowTransition SlideshowTransition { get; set; } = SlideshowTransition.Crossfade;

    [JsonIgnore]
    private static string StatePath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PhotosPlus");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "state.json");
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
            if (File.Exists(StatePath))
            {
                var json = File.ReadAllText(StatePath);
                var state = JsonSerializer.Deserialize<AppState>(json, Options);
                if (state is not null)
                {
                    // Rehydrate sets as case-insensitive (deserializer loses the comparer).
                    state.HiddenPaths = new HashSet<string>(state.HiddenPaths, StringComparer.OrdinalIgnoreCase);
                    state.FavoritePaths = new HashSet<string>(state.FavoritePaths, StringComparer.OrdinalIgnoreCase);
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
        try
        {
            File.WriteAllText(StatePath, JsonSerializer.Serialize(this, Options));
        }
        catch
        {
            // Best-effort persistence; ignore IO errors.
        }
    }
}
