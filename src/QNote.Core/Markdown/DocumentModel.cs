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
}

/// <summary>
/// Neutral, storage-agnostic document model that both conversion directions flow
/// through. The Markdown side (parse/emit) lives in Core; the RichEdit side is a thin
/// Presentation walker filling the same records from TOM. The format subset is the
/// locked Markdown subset: bold / italic / strikethrough / headings (H1–H3) /
/// bullet+numbered lists / images (schema B2, PRD 2026-09-28).
/// </summary>
public abstract record DocumentInline;

/// <summary>A styled text run. Newlines inside <see cref="Text"/> are soft line breaks.</summary>
public sealed record DocumentRun(string Text, bool Bold = false, bool Italic = false,
    bool Strikethrough = false) : DocumentInline;

/// <summary>
/// An image reference — <c>![alt](qnote-img:&lt;sha256&gt;)</c> in storage. The RTF side
/// resolves the sha to inline bytes via an image provider; nothing else knows about files.
/// </summary>
public sealed record DocumentImage(string Sha256, string AltText = "") : DocumentInline;

/// <summary>One paragraph-level block of the document, in order.</summary>
public sealed record DocumentBlock(BlockKind Kind, IReadOnlyList<DocumentInline> Inlines)
{
    /// <summary>Heading level; only meaningful when <see cref="Kind"/> is Heading.</summary>
    public HeadingLevel Level { get; init; } = HeadingLevel.H1;

    /// <summary>True = numbered list item, false = bullet. Only for ListItem.</summary>
    public bool Ordered { get; init; }

    /// <summary>1-based ordinal within the ordered list; ignored for bullets.</summary>
    public int ListNumber { get; init; } = 1;
}

/// <summary>The whole note content as a flat ordered block list.</summary>
public sealed record DocumentContent(IReadOnlyList<DocumentBlock> Blocks);
