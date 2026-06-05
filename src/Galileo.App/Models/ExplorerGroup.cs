using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace Galileo.Models;

/// <summary>
/// A grouped section of explorer items. It IS the group's child collection for CollectionViewSource;
/// collapsing simply empties the visible items (keeping the header) and refills on expand.
/// </summary>
public class ExplorerGroup : ObservableCollection<ExplorerItem>
{
    private readonly List<ExplorerItem> _all = new();

    public string Key { get; set; } = "";
    public double Rank { get; set; }
    public bool IsExpanded { get; private set; } = true;

    /// <summary>Header label, e.g. "JPG File  (12)".</summary>
    public string HeaderText => $"{Key}  ({_all.Count})";

    /// <summary>Chevron glyph: down when expanded, right when collapsed.</summary>
    public string Glyph => char.ConvertFromUtf32(IsExpanded ? 0xE70D : 0xE76C);

    /// <summary>Adds to the backing set (call <see cref="Finish"/> once all items are added).</summary>
    public void AddItem(ExplorerItem item) => _all.Add(item);

    /// <summary>Sets the initial expanded state before <see cref="Finish"/> (e.g. to restore a
    /// remembered collapsed section across refreshes).</summary>
    public void SetExpanded(bool expanded) => IsExpanded = expanded;

    /// <summary>Populates the visible items from the backing set per the current expanded state.</summary>
    public void Finish() => RebuildVisible();

    public void Toggle()
    {
        IsExpanded = !IsExpanded;
        RebuildVisible();
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(IsExpanded)));
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Glyph)));
    }

    private void RebuildVisible()
    {
        Clear();
        if (IsExpanded)
            foreach (var i in _all) Add(i);
    }
}
