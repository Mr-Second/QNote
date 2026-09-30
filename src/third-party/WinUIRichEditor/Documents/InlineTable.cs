namespace WinUIRichEditor.Documents;

/// <summary>A table that flows inline within a paragraph's text (HWP-style "treat as character"),
/// occupying exactly one logical character position (object-replacement character U+FFFC) — the same
/// offset model as <see cref="InlineImage"/>. Unlike an image it has internal structure (cells, nested
/// blocks): it wraps a <see cref="TableBlock"/> so rendering, hit-testing and measurement reuse the
/// recursive block-table primitives. For a block-level grid use <see cref="TableBlock"/> directly.</summary>
public class InlineTable : Inline
{
    /// <summary>The wrapped grid. Holds the cells/blocks and all structural state; the inline wrapper
    /// only places it within a paragraph line.</summary>
    public TableBlock Table { get; set; } = new();

    /// <inheritdoc/>
    public override TextElement Clone() =>
        new InlineTable { Table = (TableBlock)Table.Clone() };
}
