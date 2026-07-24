namespace QNote.Text;

/// <summary>
/// Pixel ↔ point conversion for the editor font-size dropdown. The UI exposes Qt's
/// pixel steps (12/14/16/…/32) while <c>ITextCharacterFormat.Size</c> is in points.
/// Lives in Core (not Presentation, as the PRD sketched) so the headless xUnit suite —
/// which references QNote.Core only — can cover it.
/// </summary>
public static class RtfFontSize
{
    /// <summary>Points per pixel at 96 DPI.</summary>
    public const double PtPerPx = 0.75;

    public static float PxToPt(int px) => (float)(px * PtPerPx);

    /// <summary>Rounds a point size back to the nearest pixel step.</summary>
    public static int PtToPx(float pt) => (int)MathF.Round(pt / (float)PtPerPx, MidpointRounding.AwayFromZero);
}
