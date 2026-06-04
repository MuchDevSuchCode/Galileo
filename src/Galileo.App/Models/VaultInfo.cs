using CommunityToolkit.Mvvm.ComponentModel;

namespace Galileo.Models;

/// <summary>Sidebar binding model for a vault row (name + locked/unlocked glyph).</summary>
public partial class VaultInfo : ObservableObject
{
    // Segoe Fluent Icons code points: Unlock (open) / Lock (closed).
    private const int GlyphUnlock = 0xE785;
    private const int GlyphLock = 0xE72E;

    public string Id { get; }
    public string Name { get; }

    [ObservableProperty] private bool _isUnlocked;

    public VaultInfo(string id, string name, bool isUnlocked)
    {
        Id = id;
        Name = name;
        _isUnlocked = isUnlocked;
    }

    public string Glyph => char.ConvertFromUtf32(IsUnlocked ? GlyphUnlock : GlyphLock);

    partial void OnIsUnlockedChanged(bool value) => OnPropertyChanged(nameof(Glyph));
}
