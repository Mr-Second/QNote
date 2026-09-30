using System.Collections.Generic;

namespace WinUIRichEditor.Documents;

/// <summary>The document's canonical block order — the one thing four separate walks used to each
/// implement, and three of them said so in a comment.
/// <para><c>RichEditor.ParagraphsInBlocks</c>, <c>TextRange.CollectParagraphs</c>,
/// <c>TextPointer.CompareTo</c>'s walk and <c>UndoManager.WalkParagraphs</c> all produced this order,
/// and their comments read "Mirror of the control's ParagraphsInBlocks", "Mirrors the editor's
/// ParagraphsInBlocks order", "mirroring CollectParagraphs". That is four copies held in agreement by
/// nothing but a request to whoever edits them next — and it has already failed once: TextPointer's own
/// comment records that without the recursion "a pointer inside a nested or inline table kept index -1
/// and selections spanning one compared as equal/reversed".</para>
/// <para>Order, and why each part of it matters:</para>
/// <list type="bullet">
/// <item>A <see cref="Paragraph"/> comes before the cell content of any inline table it hosts — the
/// paragraph is where the caret sits, the cells hang off it.</item>
/// <item>A <see cref="TableBlock"/> itself takes one position before its cells, which keeps the
/// historical index numbering that <see cref="TextPointer"/> comparisons and undo caret restoration
/// both depend on.</item>
/// <item>Cells are visited through <see cref="TableBlock.LogicalCells"/> — anchors only, row-major.
/// A covered (merged-away) cell is not content and must not take a position, or index-based range
/// loops disagree with the editor about merged tables.</item>
/// </list>
/// <para>Every block is yielded exactly once, so a consumer that wants global indices just counts what
/// it pulls, and one that wants paragraphs filters. Lazy: consumers that stop early (a comparison that
/// has located both endpoints, a containment test) stop the walk.</para></summary>
internal static class BlockWalk
{
    /// <summary>Every block under <paramref name="blocks"/>, in document order, including the tables and
    /// non-paragraph blocks themselves.</summary>
    internal static IEnumerable<Block> DocumentOrder(IEnumerable<Block> blocks)
    {
        foreach (var block in blocks)
        {
            yield return block;
            switch (block)
            {
                case Paragraph p:
                    foreach (var inline in p.Inlines)
                        if (inline is InlineTable it)
                            foreach (var (_, _, cell) in it.Table.LogicalCells())
                                foreach (var b in DocumentOrder(cell.Blocks))
                                    yield return b;
                    break;
                case TableBlock tb:
                    foreach (var (_, _, cell) in tb.LogicalCells())
                        foreach (var b in DocumentOrder(cell.Blocks))
                            yield return b;
                    break;
            }
        }
    }

    /// <summary>The paragraphs of <paramref name="blocks"/> in the same order, at any nesting depth.</summary>
    internal static IEnumerable<Paragraph> Paragraphs(IEnumerable<Block> blocks)
    {
        foreach (var block in DocumentOrder(blocks))
            if (block is Paragraph p)
                yield return p;
    }

    /// <summary>The encoded bytes of every picture under <paramref name="blocks"/> (block and inline, at any
    /// depth) — what the text formatters size their output by, since a picture dominates it.</summary>
    internal static IEnumerable<byte[]> PictureBytes(IEnumerable<Block> blocks)
    {
        foreach (var block in DocumentOrder(blocks))
        {
            if (block is ImageBlock { RawBytes: { } ib }) yield return ib;
            else if (block is Paragraph p)
                foreach (var inline in p.Inlines)
                    if (inline is InlineImage { RawBytes: { } ii }) yield return ii;
        }
    }

    /// <summary>True when <paramref name="block"/> is <paramref name="paragraph"/> or holds it at any
    /// depth (through table cells and inline tables).</summary>
    internal static bool Holds(Block block, Paragraph paragraph)
    {
        foreach (var b in DocumentOrder(new[] { block }))
            if (ReferenceEquals(b, paragraph))
                return true;
        return false;
    }
}
