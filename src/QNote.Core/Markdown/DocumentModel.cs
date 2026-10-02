namespace QNote.Markdown;

/// <summary>Inline piece kind discriminator for <see cref="DocumentInline"/>.</summary>
public enum HeadingLevel
{
    H1 = 1,
    H2 = 2,
    H3 = 3,
}

/// <summary>Block (paragraph-level) kind of a <see cref="DocumentBlock"/>.</summary>
public enum BlockKind
{
    Paragraph,
    Heading,
    ListItem,
    /// <summary>GFM pipe table. The cell grid lives in <see cref="DocumentBlock.TableCells"/>;
    /// <see cref="DocumentBlock.Inlines"/> is empty for this kind.</summary>
    Table,
    /// <summary>Thematic break (<c>---</c>). Pure structure: <see cref="DocumentBlock.Inlines"/>
    /// is empty for this kind.</summary>
    Divider,
}

/// <summary>
/// Neutral, storage-agnostic document model that both conversion directions flow
/// through. The Markdown side (parse/emit) lives in Core; the RichEdit side is a thin
/// Presentation walker filling the same records from TOM. The format subset is the
/// locked Markdown subset: bold / italic / strikethrough / inline links /
/// headings (H1–H3) / bullet+numbered lists / images (schema B2, PRD 2026-09-28;
/// links joined for the WRE editor bridge, 2026-09-30; GFM pipe tables joined for
/// the editor table feature, 2026-10-01).
/// </summary>
public abstract record DocumentInline;

/// <summary>A styled text run. Newlines inside <see cref="Text"/> are soft line breaks.</summary>
/// <param name="NavigateUri">Hyperlink target; <see langword="null"/> = plain text.</param>
public sealed record DocumentRun(string Text, bool Bold = false, bool Italic = false,
    bool Strikethrough = false, string? NavigateUri = null) : DocumentInline;

/// <summary>
/// An image reference — <c>![alt](qnote-img:&lt;sha256&gt;)</c> in storage, optionally with the
/// persisted display size as an <c>@&lt;w&gt;x&lt;h&gt;</c> suffix on the reference (DIP, rounded).
/// The editor side resolves the sha to inline bytes via an image provider; nothing else
/// knows about files.
/// </summary>
/// <param name="Width">Persisted display width in DIP; 0 = intrinsic size.</param>
/// <param name="Height">Persisted display height in DIP; 0 = intrinsic size.</param>
public sealed record DocumentImage(string Sha256, string AltText = "", int Width = 0, int Height = 0)
    : DocumentInline;

/// <summary>One paragraph-level block of the document, in order.</summary>
public sealed record DocumentBlock(BlockKind Kind, IReadOnlyList<DocumentInline> Inlines)
{
    /// <summary>Heading level; only meaningful when <see cref="Kind"/> is Heading.</summary>
    public HeadingLevel Level { get; init; } = HeadingLevel.H1;

    /// <summary>True = numbered list item, false = bullet. Only for ListItem.</summary>
    public bool Ordered { get; init; }

    /// <summary>1-based ordinal within the ordered list; ignored for bullets.</summary>
    public int ListNumber { get; init; } = 1;

    /// <summary>
    /// The table's cell grid — <c>[row][column] → that cell's inlines</c> — dense and
    /// rectangular. Non-null only when <see cref="Kind"/> is
    /// <see cref="BlockKind.Table"/>. The GFM subset stores plain 1×1 grids only
    /// (merges degrade: covered cells carry no content, the text lives in the
    /// anchor). Cells hold inline content only; block-level nesting (multiple
    /// paragraphs, dividers, nested tables) degrades to run text with spaces at the
    /// seams.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<IReadOnlyList<DocumentInline>>>? TableCells { get; init; }
}

/// <summary>The whole note content as a flat ordered block list.</summary>
public sealed record DocumentContent(IReadOnlyList<DocumentBlock> Blocks);
