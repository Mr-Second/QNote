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
/// links and <c>qnote-img:</c> image references. Everything else WRE can hold
/// (colors, fonts, underline, alignment, block quotes, tables, dividers, nesting)
/// degrades the same way the Markdown parser degrades out-of-subset input: content
/// survives, the construct does not.</para>
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
            var paragraph = new Paragraph
            {
                HeadingLevel = block.Kind == BlockKind.Heading ? (int)block.Level : 0,
                ListType = block.Kind == BlockKind.ListItem
                    ? (block.Ordered ? ListKind.Ordered : ListKind.Bullet)
                    : ListKind.None,
            };

            foreach (var inline in block.Inlines)
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

            document.Blocks.Add(paragraph);
        }

        return document;
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
                // Out of the md subset (QNote keeps AllowTables off): flatten the
                // cells' paragraphs so the text survives — the grid does not. Cells
                // share the numbering state like any sibling blocks would.
                foreach (var (_, _, cell) in table.LogicalCells())
                    foreach (var cellBlock in cell.Blocks)
                        CollectBlock(cellBlock, blocks, numbering);
                break;

            case DividerBlock:
                // Thematic breaks carry no content and are dropped by the Markdown
                // parser too — emitting one could not survive its own reload.
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
                    // Out of the md subset: the cells land after the host paragraph,
                    // flat (grid lost) — content survives.
                    pendingTables.Add(inlineTable.Table);
                    break;
            }
        }

        CollectInlines(inlines,
            isListItem ? BlockKind.ListItem : isHeading ? BlockKind.Heading : BlockKind.Paragraph,
            level, ordered, blocks, numbering);

        // The inline tables flattened behind the emitted paragraph; the shared
        // numbering state keeps a numbered list running through the cells sequential.
        foreach (var table in pendingTables)
            foreach (var (_, _, cell) in table.LogicalCells())
                foreach (var cellBlock in cell.Blocks)
                    CollectBlock(cellBlock, blocks, numbering);
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
