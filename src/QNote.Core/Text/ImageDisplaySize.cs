namespace QNote.Text;

/// <summary>
/// Display-size policy for inserted images (PRD "只缩不放", Qt parity).
/// <c>InsertImage</c> takes DIPs; we treat the source pixel size as its natural DIP
/// size (Qt behaviour) and clamp the width to the editor's available width, keeping
/// the aspect ratio. Never upscales. Pure function — Core, unit-testable.
/// </summary>
public static class ImageDisplaySize
{
    /// <summary>Minimum usable width; guards against a zero/negative measure during layout.</summary>
    public const int MinWidthDip = 32;

    /// <summary>
    /// Returns the display size in DIPs: width = min(pixelWidth, <paramref name="availableWidthDip"/>),
    /// height scaled to preserve aspect. A non-positive available width falls back to
    /// the natural width.
    /// </summary>
    public static (int WidthDip, int HeightDip) Fit(int pixelWidth, int pixelHeight, double availableWidthDip)
    {
        if (pixelWidth <= 0 || pixelHeight <= 0)
            return (MinWidthDip, MinWidthDip);

        var maxWidth = availableWidthDip >= MinWidthDip ? (int)Math.Floor(availableWidthDip) : pixelWidth;
        var width = Math.Min(pixelWidth, maxWidth);
        var height = (int)Math.Round(pixelHeight * (double)width / pixelWidth);
        return (Math.Max(1, width), Math.Max(1, height));
    }
}
