using System.Collections.Generic;
using Windows.UI;
using Microsoft.UI.Xaml;

namespace WinUIRichEditor.Documents;

/// <summary>List style for a <see cref="Paragraph"/>.</summary>
public enum ListKind
{
    /// <summary>Not a list item.</summary>
    None,
    /// <summary>Unordered bullet list.</summary>
    Bullet,
    /// <summary>Ordered (numbered) list.</summary>
    Ordered
}

/// <summary>The specific bullet glyph or number format of a list item, refining <see cref="ListKind"/>.
/// <see cref="Default"/> means the kind's default (• for bullets, "1." for numbers).</summary>
public enum ListMarkerStyle
{
    /// <summary>The list kind's default marker (• / "1.").</summary>
    Default,
    /// <summary>Filled disc bullet (•).</summary>
    Disc,
    /// <summary>Hollow circle bullet (◦).</summary>
    Circle,
    /// <summary>Filled square bullet (▪).</summary>
    Square,
    /// <summary>Dash bullet (–).</summary>
    Dash,
    /// <summary>Decimal with a dot (1.).</summary>
    Decimal,
    /// <summary>Decimal with a parenthesis (1)).</summary>
    DecimalParen,
    /// <summary>Lowercase letters (a)).</summary>
    LowerAlpha,
    /// <summary>Uppercase letters (A)).</summary>
    UpperAlpha,
    /// <summary>Lowercase Roman numerals (i)).</summary>
    LowerRoman
}

/// <summary>A block of inline content (<see cref="Run"/>s and <see cref="InlineImage"/>s) with
/// paragraph-level formatting: alignment, line spacing, indent, list style, heading level, background.
/// Also serves as the content of a table cell.</summary>
public class Paragraph : Block
{
    /// <summary>Creates an empty paragraph. Like HWP, a paragraph has no bottom margin by default — its
    /// line spacing alone separates it from the next (images/tables keep <see cref="Block"/>'s 10).</summary>
    public Paragraph()
    {
        MarginBottom = 0;
    }

    /// <summary>The inline elements (runs and inline images) that make up the paragraph.</summary>
    public List<Inline> Inlines { get; set; } = new();
    /// <summary>Horizontal text alignment. Default: Left.</summary>
    public TextAlignment TextAlignment { get; set; } = TextAlignment.Left;
    /// <summary>Absolute line-box height in device-independent pixels ("exactly" spacing, like Word's
    /// fixed value, HWP's "고정 값"). <see cref="double.NaN"/> = unset. Overridden by <see cref="LineSpacing"/>
    /// when that is set. For proportional spacing that scales with font size, prefer <see cref="LineSpacing"/>.</summary>
    public double LineHeight { get; set; } = double.NaN;
    /// <summary>Proportional line spacing, HWP's "글자에 따라" % ÷ 100: the line box is the paragraph's
    /// largest font size × this ratio (1.0 = 100% = font size, 1.6 = 160%, 2.0 = 200%). Not Word's
    /// "Multiple", which is relative to the font's natural line height. <see cref="double.NaN"/> = unset
    /// (falls back to <see cref="LineHeight"/>, then the HWP default 160%). Takes priority over
    /// <see cref="LineHeight"/> when set.</summary>
    public double LineSpacing { get; set; } = double.NaN;
    /// <summary>Right margin in device-independent pixels — narrows the wrap width. Paragraph-only:
    /// nothing flows around images/tables, so a right margin would be invisible there
    /// (the left margin is <see cref="Block.Indent"/>). Default: 0.</summary>
    public double MarginRight { get; set; } = 0;
    /// <summary>List style for this paragraph. Default: None.</summary>
    public ListKind ListType { get; set; } = ListKind.None;
    /// <summary>The bullet glyph / number format for this list item (refines <see cref="ListType"/>).
    /// Default = the kind's default (• / "1."). Ignored when <see cref="ListType"/> is None.</summary>
    public ListMarkerStyle ListMarker { get; set; } = ListMarkerStyle.Default;
    /// <summary>Heading level: 0 = body text, 1–6 = h1–h6.</summary>
    public int HeadingLevel { get; set; } = 0;
    /// <summary>Paragraph or table-cell background fill color.</summary>
    public Color? Background { get; set; }
    /// <summary>Whether this paragraph is a blockquote.</summary>
    public bool IsQuote { get; set; } = false;
    /// <summary>Nested list depth (0 = top level).</summary>
    public int ListLevel { get; set; } = 0;

    /// <summary><see langword="true"/> if this paragraph is a bullet or numbered list item.</summary>
    public bool IsListItem => ListType != ListKind.None;

    /// <summary>Copies every paragraph-level formatting field from <paramref name="src"/> onto this
    /// paragraph (inlines untouched). Single source of the format-field list shared by
    /// <see cref="CloneFormat"/>, <see cref="Clone"/>, and the paste-into-empty-paragraph path.</summary>
    public void CopyFormatFrom(Paragraph src)
    {
        MarginTop = src.MarginTop;
        MarginBottom = src.MarginBottom;
        MarginRight = src.MarginRight;
        TextAlignment = src.TextAlignment;
        LineHeight = src.LineHeight;
        LineSpacing = src.LineSpacing;
        ListType = src.ListType;
        ListMarker = src.ListMarker;
        HeadingLevel = src.HeadingLevel;
        Background = src.Background;
        Indent = src.Indent;
        IsQuote = src.IsQuote;
        ListLevel = src.ListLevel;
    }

    /// <summary>Creates an EMPTY paragraph carrying all of this paragraph's paragraph-level formatting
    /// (no inlines) — the Enter-split and block-insert paths use it so the continuation keeps list
    /// marker style, line spacing, margins, quote state etc. (hand-picked copies kept dropping fields:
    /// splitting a ◦-bulleted item reset the new item's glyph, Enter lost custom line spacing).</summary>
    public Paragraph CloneFormat()
    {
        var p = new Paragraph();
        p.CopyFormatFrom(this);
        return p;
    }

    /// <inheritdoc/>
    /// <remarks>The format fields come from <see cref="CopyFormatFrom"/> — the ONE list — because this
    /// used to be a second hand-written copy of it. A paragraph property added without updating both
    /// lists survives normal editing and then disappears at the first undo, since an undo state is a
    /// clone; that is the same failure <see cref="CloneFormat"/>'s note records for the split path.</remarks>
    public override TextElement Clone()
    {
        var p = new Paragraph();
        p.CopyFormatFrom(this);
        foreach (var inline in Inlines)
        {
            var inlineClone = inline.Clone() as Inline;
            if (inlineClone != null)
            {
                inlineClone.Parent = p;
                p.Inlines.Add(inlineClone);
            }
        }
        return p;
    }
}
