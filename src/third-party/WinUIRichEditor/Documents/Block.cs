namespace WinUIRichEditor.Documents;

/// <summary>Abstract base for block-level elements (<see cref="Paragraph"/>, <see cref="TableBlock"/>,
/// <see cref="ImageBlock"/>, <see cref="DividerBlock"/>).</summary>
public abstract class Block : TextElement
{
    /// <summary>Left indent in device-independent pixels. Shifts paragraph text right; shifts
    /// image/table blocks right (used by the "Space before a block" margin feature). Default: 0.</summary>
    public double Indent { get; set; } = 0;
    /// <summary>Top margin in device-independent pixels (gap to the previous block). Default: 0 for a
    /// <see cref="Paragraph"/>, <see cref="AutoTopMargin"/> for the block kinds that are objects on the
    /// page (<see cref="TableBlock"/>, <see cref="ImageBlock"/>, <see cref="DividerBlock"/>).</summary>
    public double MarginTop { get; set; } = 0;

    /// <summary><see cref="MarginTop"/> value meaning "let the editor choose": the block sits one line
    /// gap below whatever precedes it — the white space a line break leaves between two lines of body
    /// text, so it follows the document's font size and line spacing. Paragraphs carry no bottom margin
    /// (HWP-style), so without this a table or picture butts straight against the text above it.
    /// <para>NaN as "unset" follows <see cref="Paragraph.LineSpacing"/>; a plain 0 cannot say it, because
    /// 0 is also a perfectly good margin to ask for — and pagination asks for it.</para></summary>
    public static double AutoTopMargin => double.NaN;
    /// <summary>Bottom margin in device-independent pixels (gap to the next block). Default: 10
    /// (<see cref="DividerBlock"/> overrides to 0 — its height already includes spacing).</summary>
    public double MarginBottom { get; set; } = 10;
}
