using System.Collections.Generic;

namespace Galileo.Models;

/// <summary>A grouped section of explorer items (the list IS the group's children for CollectionViewSource).</summary>
public class ExplorerGroup : List<ExplorerItem>
{
    public string Key { get; set; } = "";
    public double Rank { get; set; }
}
