using System.Collections.Generic;
using Windows.UI;

namespace WinUIRichEditor.Documents;

/// <summary>Vertical alignment of a table cell's content within the cell box.</summary>
public enum CellVerticalAlignment
{
    /// <summary>Content sits at the top of the cell (the default).</summary>
    Top,
    /// <summary>Content is centered vertically.</summary>
    Center,
    /// <summary>Content sits at the bottom of the cell.</summary>
    Bottom,
}

/// <summary>The content of one table cell: a list of block-level elements (like the body of an HTML
/// &lt;td&gt;) plus cell-level formatting (background). Milestone A introduces this so a cell can hold
/// more than a single paragraph (multiple paragraphs, block images, dividers, nested tables).
/// <para>Invariant: <see cref="Blocks"/> is never empty — an "empty" cell holds one empty
/// <see cref="Paragraph"/>. Through phases P1/P2 every cell still holds exactly one paragraph (so
/// <see cref="Para"/> is the common accessor); P3 enables genuine multi-block cells.</para></summary>
public class TableCell : TextElement
{
    /// <summary>The block-level content of the cell. Never empty (at least one paragraph).</summary>
    public List<Block> Blocks { get; set; } = new();
    /// <summary>The cell background fill color (was previously stored on the cell's paragraph).</summary>
    public Color? Background { get; set; }
    /// <summary>Vertical alignment of the content within the cell box. Default: Top.</summary>
    public CellVerticalAlignment VerticalAlignment { get; set; } = CellVerticalAlignment.Top;

    /// <summary>Creates a cell containing a single empty paragraph.</summary>
    public TableCell()
    {
        Blocks.Add(new Paragraph { Inlines = { new Run { Text = "" } } });
    }

    /// <summary>Creates a cell wrapping an existing paragraph.</summary>
    public TableCell(Paragraph paragraph)
    {
        Blocks.Add(paragraph);
    }

    /// <summary>The cell's primary paragraph — the first paragraph reachable in <see cref="Blocks"/>,
    /// descending into a leading nested table if the cell starts with one (P4-2b). Convenience for the
    /// many call sites that treat a cell as having a single editable paragraph; multi-block walks use
    /// <see cref="Blocks"/> directly. The cell invariant guarantees at least one paragraph exists.</summary>
    public Paragraph Para
    {
        get
        {
            if (Blocks.Count > 0 && Blocks[0] is Paragraph p0) return p0; // fast path: the common case
            foreach (var b in Blocks)
                if (FirstParagraph(b) is { } p) return p;
            // Invariant says a paragraph always exists; restore it if somehow not (never expected).
            var restored = new Paragraph { Inlines = { new Run { Text = "" } } };
            Blocks.Add(restored);
            return restored;
        }
    }

    // The first paragraph reachable in a block, recursing through nested-table cells.
    private static Paragraph? FirstParagraph(Block block)
    {
        switch (block)
        {
            case Paragraph p: return p;
            case TableBlock tb:
                foreach (var row in tb.Cells)
                    foreach (var cell in row)
                        foreach (var cb in cell.Blocks)
                            if (FirstParagraph(cb) is { } fp) return fp;
                return null;
            default: return null;
        }
    }

    /// <inheritdoc/>
    public override TextElement Clone()
    {
        var tc = new TableCell { Background = Background, VerticalAlignment = VerticalAlignment };
        tc.Blocks.Clear();
        foreach (var b in Blocks)
        {
            if (b.Clone() is Block bc)
            {
                bc.Parent = tc;
                tc.Blocks.Add(bc);
            }
        }
        if (tc.Blocks.Count == 0) tc.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "" } } });
        return tc;
    }
}
