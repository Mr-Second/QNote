using System.Security.Cryptography;
using QNote.Markdown;
using Windows.UI.Text;
using WinUIRichEditor.Documents;

namespace QNote.WreMarkdown;

/// <summary>
/// Bidirectional bridge between QNote's storage-side Markdown document model
/// (<see cref="DocumentContent"/>) and the WinUIRichEditor editor document model
/// (<see cref="FlowDocument"/>). Storage stays Markdown; this is the headless core
/// of the WYSIWYG pipeline: load = Markdown → model → <see cref="FlowDocument"/>,
/// save = <see cref="FlowDocument"/> → model → Markdown.
/// <para>The mappable subset is exactly what the QNote model can express: paragraphs,
/// headings (model carries H1–H3; deeper WRE levels clamp the way the Markdown
/// parser clamps), flat bullet/ordered lists, bold/italic/strikethrough, inline
/// links, <c>qnote-img:</c> image references, GFM pipe tables (plain 1×1 grids) and
/// thematic breaks. Everything else WRE can hold (colors, fonts, underline,
/// alignment, block quotes, merges, cell nesting) degrades the same way the
/// Markdown parser degrades out-of-subset input: content survives, the construct
/// does not.</para>
/// <para>Soft line breaks map without loss — WRE stores Shift+Enter breaks as
/// <c>'\n'</c> inside <c>Run.Text</c>, the same convention the QNote model uses.</para>
/// </summary>
public static class MarkdownDocumentFormatter
{
    /// <summary>
    /// Resolves a <c>qnote-img:</c> sha256 to the bytes to embed in the editor document.
    /// Return null when the bytes are unavailable (the image shows as an empty
    /// placeholder). The reverse direction re-derives the sha by hashing these bytes,
    /// so they must be the SAME content the sha addresses (QNote's scheme: sha =
    /// SHA-256 of the image's original bytes).
    /// </summary>
    public delegate byte[]? ImageBytesProvider(string sha256);

    /// <summary>
    /// Load direction: QNote document model → WRE editor document. Heading look is
    /// left to the editor: the paragraphs carry only <see cref="Paragraph.HeadingLevel"/>,
    /// and the editor materializes bold/size presets on receipt (HeadingStyle).
    /// </summary>
    public static FlowDocument ToFlowDocument(DocumentContent content, ImageBytesProvider? imageProvider = null)
    {
        var document = new FlowDocument();

        foreach (var block in content.Blocks)
        {
            if (block.Kind == BlockKind.Table && block.TableCells is { Count: > 0 } cells)
            {
                document.Blocks.Add(CreateTable(cells, imageProvider));
                continue;
            }

            if (block.Kind == BlockKind.Divider)
            {
                document.Blocks.Add(new DividerBlock());
                continue;
            }

            var paragraph = new Paragraph
            {
                HeadingLevel = block.Kind == BlockKind.Heading ? (int)block.Level : 0,
                ListType = block.Kind == BlockKind.ListItem
                    ? (block.Ordered ? ListKind.Ordered : ListKind.Bullet)
                    : ListKind.None,
            };

            foreach (var inline in block.Inlines)
                AppendInline(paragraph, inline, imageProvider);

            document.Blocks.Add(paragraph);
        }

        return document;
    }

    /// <summary>Map one model inline into the WRE paragraph (shared by body and cells).</summary>
    private static void AppendInline(Paragraph paragraph, DocumentInline inline,
        ImageBytesProvider? imageProvider)
    {
        switch (inline)
        {
            case DocumentRun run:
                paragraph.Inlines.Add(new Run
                {
                    Text = run.Text,
                    // Plain struct values (not the FontWeights WinRT static) so the
                    // model builds without activating the WinUI runtime — the same
                    // headless-construction rule the WRE model itself follows.
                    FontWeight = new FontWeight { Weight = (ushort)(run.Bold ? 700 : 400) },
                    FontStyle = run.Italic ? FontStyle.Italic : FontStyle.Normal,
                    TextDecorations = run.Strikethrough
                        ? TextDecorationFlags.Strikethrough
                        : TextDecorationFlags.None,
                    NavigateUri = run.NavigateUri,
                });
                break;

            case DocumentImage image:
                paragraph.Inlines.Add(CreateImage(image, imageProvider));
                break;
        }
    }

    /// <summary>
    /// Build a WRE table from the model's dense cell grid. Every cell holds one
    /// paragraph of inline content (the GFM subset keeps plain 1×1 grids — merges are
    /// not expressible); an empty model cell keeps WRE's own empty-cell shape (a
    /// paragraph with one empty run, the TableCell constructor's convention).
    /// </summary>
    private static TableBlock CreateTable(
        IReadOnlyList<IReadOnlyList<IReadOnlyList<DocumentInline>>> cells,
        ImageBytesProvider? imageProvider)
    {
        var rows = cells.Count;
        var columns = Math.Max(1, cells.Max(row => row.Count));
        var table = new TableBlock(rows, columns);

        for (var r = 0; r < rows; r++)
        {
            var row = cells[r];
            for (var c = 0; c < columns; c++)
            {
                var paragraph = new Paragraph();
                foreach (var inline in c < row.Count ? row[c] : [])
                    AppendInline(paragraph, inline, imageProvider);
                if (paragraph.Inlines.Count == 0)
                    paragraph.Inlines.Add(new Run { Text = "" });

                var cell = table.Cells[r][c];
                cell.Blocks.Clear();
                cell.Blocks.Add(paragraph);
            }
        }

        return table;
    }

    /// <summary>
    /// Save direction: WRE editor document → QNote document model (ready for
    /// <see cref="MarkdownEmitter"/>). The result is canonical — it equals what
    /// parsing its own emitted Markdown would produce, so a save/load cycle is
    /// stable from the first round trip.
    /// </summary>
    public static DocumentContent ToDocumentContent(FlowDocument document)
    {
        var blocks = new List<DocumentBlock>();

        // md list semantics: the parser numbers items per consecutive same-kind run
        // (a list is entirely ordered or entirely bullet) and ignores the literal
        // digits, so consecutive same-kind blocks MUST come out 1,2,3… to equal
        // parse(emit(model)). Anything that emits a block between two items breaks
        // the list in md and resets the count; blocks dropped as empty/out-of-subset
        // (divider, byte-less image, empty paragraph) emit nothing and stay
        // transparent, or the renumbering they cause would surface on the next save.
        var numbering = new ListNumbering();
        foreach (var block in document.Blocks)
            CollectBlock(block, blocks, numbering);

        return new DocumentContent(blocks);
    }

    /// <summary>
    /// The running ordinals of consecutive same-kind list items — bullets too, since
    /// the parser numbers every item of a list and the model carries the count even
    /// where the emitter ignores it.
    /// </summary>
    private sealed class ListNumbering
    {
        private bool? _lastKind; // true = ordered, false = bullet, null = no run
        private int _count;

        public int Take(bool ordered)
        {
            _count = _lastKind == ordered ? _count + 1 : 1;
            _lastKind = ordered;
            return _count;
        }

        /// <summary>End the current run (called for every block that emits).</summary>
        public void Break() => _lastKind = null;
    }

    private static void CollectBlock(Block block, List<DocumentBlock> blocks, ListNumbering numbering)
    {
        switch (block)
        {
            case Paragraph paragraph:
                CollectParagraph(paragraph, blocks, numbering);
                break;

            case ImageBlock image:
                // md has only the inline reference form, so a block picture lands in
                // a paragraph of its own — the reference survives the same way.
                CollectInlines([.. ImageInlines(image.RawBytes, image.AltText)],
                    BlockKind.Paragraph, HeadingLevel.H1, ordered: false, blocks, numbering);
                break;

            case TableBlock table:
                // GFM pipe tables joined the md subset (2026-10-01): the grid is
                // preserved as a Table block. Out-of-subset bits degrade the same way
                // the parser degrades loaded files: merges (covered cells are empty —
                // MergeCells already moved their content into the anchor), cell
                // backgrounds/alignment, and block-level nesting inside a cell.
                CollectTable(table, blocks, numbering);
                break;

            case DividerBlock:
                // Thematic breaks joined the md subset (2026-10-01): pure structure,
                // no inline content — the emitter writes --- with its own blank-line
                // discipline. Emits, so the list-numbering run ends here like any
                // other block.
                blocks.Add(new DocumentBlock(BlockKind.Divider, []));
                numbering.Break();
                break;
        }
    }

    /// <summary>
    /// Convert one WRE paragraph into a model block. A heading-and-list-item paragraph
    /// (WRE allows both) degrades to the list item — the same loss the Markdown
    /// parser applies to a heading written inside a list item.
    /// </summary>
    private static void CollectParagraph(Paragraph paragraph, List<DocumentBlock> blocks, ListNumbering numbering)
    {
        var isHeading = paragraph.HeadingLevel is >= 1 and <= 6;

        // Headings beyond the model's H1–H3 clamp to H3, mirroring the parser's
        // `##### five` → H3 rule; the clamp is what the storage side does, so the
        // bridge must not invent level-4+ references the pipeline would re-clamp.
        var level = (HeadingLevel)Math.Clamp(paragraph.HeadingLevel, 1, 3);
        var isListItem = paragraph.IsListItem;
        var ordered = isListItem && paragraph.ListType == ListKind.Ordered;

        var inlines = new List<DocumentInline>();
        var pendingTables = new List<TableBlock>();
        foreach (var inline in paragraph.Inlines)
        {
            switch (inline)
            {
                case Run run when (run.Text ?? string.Empty).Length > 0:
                    // md's "# " already renders bold+large; the level re-applies the
                    // preset in the editor (HeadingStyle.Materialize), so run bold
                    // inside a heading is marker noise, not data. Italic/strike and
                    // links are independent of the preset and survive.
                    AppendRun(inlines, new DocumentRun(
                        run.Text!,
                        Bold: !isHeading && run.FontWeight.Weight >= 600,
                        Italic: run.FontStyle == FontStyle.Italic,
                        Strikethrough: run.TextDecorations.HasFlag(TextDecorationFlags.Strikethrough),
                        NavigateUri: run.NavigateUri is { Length: > 0 } uri ? uri : null));
                    break;

                case InlineImage image:
                    inlines.AddRange(ImageInlines(image.RawBytes, image.AltText));
                    break;

                case InlineTable inlineTable:
                    // A table inside a paragraph is promoted to the block level right
                    // after its host (GFM tables are block constructs) — the grid now
                    // survives; host-internal position degrades (the inline table was
                    // mid-paragraph, md can only carry it after the text).
                    pendingTables.Add(inlineTable.Table);
                    break;
            }
        }

        CollectInlines(inlines,
            isListItem ? BlockKind.ListItem : isHeading ? BlockKind.Heading : BlockKind.Paragraph,
            level, ordered, blocks, numbering);

        // Inline tables land as block-level Tables behind their host paragraph (the
        // shared numbering state keeps a numbered list running through the cells sequential).
        foreach (var table in pendingTables)
            CollectTable(table, blocks, numbering);
    }

    /// <summary>
    /// Collect one WRE table into the model's dense cell grid. Covered slots of merged
    /// cells carry an empty inline list (their content moved to the anchor); a
    /// degenerate grid (no rows/columns) stays transparent like any other empty block.
    /// </summary>
    private static void CollectTable(TableBlock table, List<DocumentBlock> blocks,
        ListNumbering numbering)
    {
        if (table.Rows <= 0 || table.Columns <= 0)
            return;

        var rows = new List<IReadOnlyList<IReadOnlyList<DocumentInline>>>(table.Rows);
        for (var r = 0; r < table.Rows; r++)
        {
            var rowCells = new List<IReadOnlyList<DocumentInline>>(table.Columns);
            for (var c = 0; c < table.Columns; c++)
            {
                // Dense-grid invariant, guarded: any covered or out-of-range slot
                // reads as an empty cell.
                if (table.IsCovered(r, c) ||
                    r >= table.Cells.Count || c >= table.Cells[r].Count)
                {
                    rowCells.Add([]);
                }
                else
                {
                    rowCells.Add(CollectCellInlines(table.Cells[r][c]));
                }
            }
            rows.Add(rowCells);
        }

        blocks.Add(new DocumentBlock(BlockKind.Table, [])
        {
            TableCells = rows,
        });

        // The table emits as md — any list run around it restarts (same rule as
        // every other block that emits).
        numbering.Break();
    }

    /// <summary>
    /// Collect one cell's content as inline list. GFM cells hold inline content only:
    /// multiple paragraphs join with a space, images keep their reference, dividers
    /// drop, and nested tables degrade to their cells' text (the grid is not
    /// expressible inside a cell).
    /// </summary>
    private static List<DocumentInline> CollectCellInlines(TableCell cell)
    {
        var inlines = new List<DocumentInline>();
        var first = true;

        foreach (var block in cell.Blocks)
        {
            switch (block)
            {
                case Paragraph paragraph:
                    if (!first && inlines.Count > 0)
                        AppendRun(inlines, new DocumentRun(" "));
                    first = false;
                    CollectCellParagraph(paragraph, inlines);
                    break;

                case ImageBlock image:
                    if (!first && inlines.Count > 0)
                        AppendRun(inlines, new DocumentRun(" "));
                    first = false;
                    AppendCellInlines(inlines, ImageInlines(image.RawBytes, image.AltText));
                    break;

                case TableBlock nested:
                    if (!first && inlines.Count > 0)
                        AppendRun(inlines, new DocumentRun(" "));
                    first = false;
                    AppendNestedTable(nested, inlines);
                    break;

                case DividerBlock:
                    // No content to keep (the parser drops thematic breaks too).
                    break;
            }
        }

        return NormalizeCell(inlines);
    }

    /// <summary>A cell paragraph's inlines — lists/headings inside a cell are not
    /// expressible in GFM, so the list/heading semantics degrade to plain runs.</summary>
    private static void CollectCellParagraph(Paragraph paragraph, List<DocumentInline> inlines)
    {
        foreach (var inline in paragraph.Inlines)
        {
            switch (inline)
            {
                case Run run when (run.Text ?? string.Empty).Length > 0:
                    AppendRun(inlines, new DocumentRun(
                        run.Text!,
                        Bold: run.FontWeight.Weight >= 600,
                        Italic: run.FontStyle == FontStyle.Italic,
                        Strikethrough: run.TextDecorations.HasFlag(TextDecorationFlags.Strikethrough),
                        NavigateUri: run.NavigateUri is { Length: > 0 } uri ? uri : null));
                    break;

                case InlineImage image:
                    AppendCellInlines(inlines, ImageInlines(image.RawBytes, image.AltText));
                    break;

                case InlineTable inlineTable:
                    AppendNestedTable(inlineTable.Table, inlines);
                    break;
            }
        }
    }

    /// <summary>
    /// Append a collected inline list into a cell being built, keeping the parser's
    /// merge rule: adjacent same-format runs must join (a bare <c>AddRange</c> would
    /// leave "before" + "x" as two runs and emit as "beforex").
    /// </summary>
    private static void AppendCellInlines(List<DocumentInline> target,
        IReadOnlyList<DocumentInline> source)
    {
        foreach (var inline in source)
        {
            if (inline is DocumentRun run)
                AppendRun(target, run);
            else
                target.Add(inline);
        }
    }

    /// <summary>A nested table inside a cell: keep the cells' text, lose the grid.</summary>
    private static void AppendNestedTable(TableBlock table, List<DocumentInline> inlines)
    {
        var first = true;
        foreach (var (_, _, cell) in table.LogicalCells())
        {
            var nested = CollectCellInlines(cell);
            if (nested.Count == 0)
                continue;
            if (!first && inlines.Count > 0)
                AppendRun(inlines, new DocumentRun(" "));
            first = false;
            AppendCellInlines(inlines, nested);
        }
    }

    /// <summary>
    /// Canonicalize a cell's inline list so the first save already equals the
    /// reloaded parse (the emitter pads each cell with <c>| … |</c> spaces and
    /// Markdig trims cell content): soft breaks become spaces (GFM cells cannot hold
    /// line breaks) and leading/trailing whitespace is trimmed from the cell edges.
    /// </summary>
    private static List<DocumentInline> NormalizeCell(List<DocumentInline> inlines)
    {
        for (var i = 0; i < inlines.Count; i++)
            if (inlines[i] is DocumentRun r && r.Text.AsSpan().IndexOf('\n') >= 0)
                inlines[i] = r with { Text = r.Text.Replace("\n", " ") };

        if (inlines.Count > 0 && inlines[0] is DocumentRun first)
        {
            var text = first.Text.TrimStart();
            if (text.Length == 0) inlines.RemoveAt(0);
            else inlines[0] = first with { Text = text };
        }

        if (inlines.Count > 0 && inlines[^1] is DocumentRun last)
        {
            var text = last.Text.TrimEnd();
            if (text.Length == 0) inlines.RemoveAt(inlines.Count - 1);
            else inlines[^1] = last with { Text = text };
        }

        return inlines;
    }

    /// <summary>
    /// Emit (or transparently skip) one model block built from already-collected
    /// inlines, maintaining the list-numbering state.
    /// </summary>
    private static void CollectInlines(List<DocumentInline> inlines, BlockKind kind,
        HeadingLevel level, bool ordered, List<DocumentBlock> blocks, ListNumbering numbering)
    {
        // A heading block with no inlines is expressible md ("# ") and parses back,
        // so headings always emit; other empty blocks are skipped — the parser never
        // produces one, and a skipped block stays transparent (see ToDocumentContent).
        if (inlines.Count == 0 && kind != BlockKind.Heading)
            return;

        // Non-list blocks match the model's ListNumber default of 1 (ignored there).
        var number = kind == BlockKind.ListItem ? numbering.Take(ordered) : 1;
        if (kind != BlockKind.ListItem)
            numbering.Break();

        blocks.Add(new DocumentBlock(kind, inlines)
        {
            Level = level,
            Ordered = ordered,
            ListNumber = number,
        });
    }

    /// <summary>
    /// Image bytes → model inlines: content-addressed reference (DocumentImage) when
    /// bytes exist; otherwise the alt text as a plain readable run (the RTF emitter's
    /// missing-image degradation); a byte-less, alt-less image drops — WRE's own
    /// HTML export drops it too.
    /// </summary>
    private static IReadOnlyList<DocumentInline> ImageInlines(byte[]? rawBytes, string? altText)
    {
        if (rawBytes is { Length: > 0 })
            return [new DocumentImage(Convert.ToHexStringLower(SHA256.HashData(rawBytes)), altText ?? "")];

        if (!string.IsNullOrEmpty(altText))
            return [new DocumentRun(altText)];

        return [];
    }

    /// <summary>Build the editor-side image from a storage reference.</summary>
    private static InlineImage CreateImage(DocumentImage image, ImageBytesProvider? imageProvider)
    {
        var inline = new InlineImage { AltText = image.AltText };

        var bytes = imageProvider?.Invoke(image.Sha256);
        if (bytes is { Length: > 0 })
        {
            // Core's public byte sniffer (the RTF emitter's): pixel dimensions for
            // display geometry (px @ 96 DPI = DIP, the RTF-era convention) and the
            // PNG/JPEG kind for the MIME type.
            var payload = RtfImagePayload.FromBytes(bytes);
            inline.SetImageData(bytes,
                payload?.Blip == RtfImagePayload.JpegBlip ? "image/jpeg" : "image/png");
            if (payload is not null)
            {
                inline.Width = payload.PixelWidth;
                inline.Height = payload.PixelHeight;
            }
        }

        return inline;
    }

    /// <summary>
    /// Append with the parser's merge rule: adjacent runs join only when every
    /// format flag and the link target match, so WRE's arbitrary run splitting
    /// (and the editor's undo splits) normalizes back to parser-canonical runs.
    /// </summary>
    private static void AppendRun(List<DocumentInline> inlines, DocumentRun run)
    {
        if (run.Text.Length == 0)
            return;
        if (inlines.Count > 0 && inlines[^1] is DocumentRun last &&
            last.Bold == run.Bold && last.Italic == run.Italic &&
            last.Strikethrough == run.Strikethrough && last.NavigateUri == run.NavigateUri)
            inlines[^1] = last with { Text = last.Text + run.Text };
        else
            inlines.Add(run);
    }
}
