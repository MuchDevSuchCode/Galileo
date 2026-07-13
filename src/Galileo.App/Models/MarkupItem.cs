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

    public MarkupItem Clone() => new()
    {
        Kind = Kind, Start = Start, End = End, Color = Color, Thickness = Thickness,
        Points = new List<Point>(Points), Text = Text, FontSize = FontSize,
    };

    /// <summary>Rescales the markup when the image's pixel size changes (an AI upscale rewrites the source
    /// at a new resolution). Coordinates, stroke width and text size all live in image space, so all of them
    /// must follow — otherwise annotations collapse into a corner and bake into the export that way.</summary>
    public void Scale(double f)
    {
        Start = new Point(Start.X * f, Start.Y * f);
        End = new Point(End.X * f, End.Y * f);
        for (var i = 0; i < Points.Count; i++)
            Points[i] = new Point(Points[i].X * f, Points[i].Y * f);
        Thickness *= f;
        FontSize *= f;
    }
}
