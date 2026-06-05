using System.Collections.Generic;
using Windows.Foundation;
using Windows.UI;

namespace Galileo.Models;

public enum MarkupKind { Pen, Rectangle, Ellipse, Line, Arrow, Text }

/// <summary>A vector annotation drawn over the image (freehand pen, shapes &amp; text). Coordinates are
/// in oriented-image pixel space so they survive window resizes and map cleanly on export.</summary>
public sealed class MarkupItem
{
    public MarkupKind Kind { get; set; }
    public Point Start { get; set; }
    public Point End { get; set; }
    public Color Color { get; set; }
    public double Thickness { get; set; } = 4;

    /// <summary>Freehand path points (Pen only).</summary>
    public List<Point> Points { get; set; } = new();

    // Text only
    public string Text { get; set; } = "";
    public double FontSize { get; set; } = 28;
}
