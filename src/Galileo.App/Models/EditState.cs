using Windows.Foundation;

namespace Galileo.Models;

public enum ImageFilter { None, Auto, BlackWhite, Sepia, Vivid, Warm, Cool, Invert }

/// <summary>
/// Non-destructive edit parameters for the image editor. Snapshotted (via <see cref="Clone"/>)
/// for undo/redo. Adjustment ranges are normalized so 0 = neutral; the editor maps them to Win2D
/// effect parameters in <c>ImageEditor</c>.
/// </summary>
public sealed class EditState
{
    // Orientation
    public int Quarter { get; set; }            // 90° clockwise turns, 0..3
    public bool FlipH { get; set; }
    public bool FlipV { get; set; }
    public double StraightenDeg { get; set; }   // fine rotation, -45..45

    /// <summary>Crop rectangle in oriented-image pixel space (null = whole image).</summary>
    public Rect? Crop { get; set; }

    // Adjustments (neutral = 0)
    public double Exposure { get; set; }        // -2..2 stops
    public double Brightness { get; set; }      // -1..1
    public double Contrast { get; set; }        // -1..1
    public double Saturation { get; set; }      // -1..1  (maps to a 0..2 multiplier)
    public double Temperature { get; set; }     // -1..1
    public double Tint { get; set; }            // -1..1
    public double Sharpness { get; set; }       // 0..1

    public ImageFilter Filter { get; set; } = ImageFilter.None;

    public bool IsNeutral =>
        Quarter == 0 && !FlipH && !FlipV && StraightenDeg == 0 && Crop is null
        && Exposure == 0 && Brightness == 0 && Contrast == 0 && Saturation == 0
        && Temperature == 0 && Tint == 0 && Sharpness == 0 && Filter == ImageFilter.None;

    public EditState Clone() => (EditState)MemberwiseClone();
}
