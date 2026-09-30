using System.Numerics;
using Windows.Foundation;
using Microsoft.Graphics.Canvas.Text;

namespace WinUIRichEditor.Controls;

/// <summary>An <see cref="ICanvasTextInlineObject"/> that only reserves space in a
/// <c>CanvasTextLayout</c> (the Win2D analog of Avalonia's <c>DrawableTextRun</c> for an inline image
/// or table — rule #2: an atomic inline occupies one U+FFFC character position). It paints nothing:
/// the editor draws the actual bitmap/table after <c>DrawTextLayout</c> using the layout's character
/// regions, so a cached layout never holds a stale drawing session.</summary>
internal sealed partial class SpacerInlineObject : ICanvasTextInlineObject
{
    public SpacerInlineObject(Size size) => Size = size;

    public Size Size { get; }
    // Bottom of the box sits on the text baseline (matches the Avalonia ImageTextRun.Baseline = height).
    public float Baseline => (float)Size.Height;
    public Rect DrawBounds => new(0, -Size.Height, Size.Width, Size.Height);
    public bool SupportsSideways => false;
    public CanvasLineBreakCondition BreakBefore => CanvasLineBreakCondition.Neutral;
    public CanvasLineBreakCondition BreakAfter => CanvasLineBreakCondition.Neutral;

    public void Draw(ICanvasTextRenderer renderer, Vector2 point, bool isSideways, bool isRightToLeft, object brush)
    {
        // Intentionally empty — the editor paints the image/table itself after the text layout.
    }
}
