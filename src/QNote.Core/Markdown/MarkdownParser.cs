using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace QNote.Markdown;

/// <summary>
/// Parses stored Markdown into the neutral <see cref="DocumentContent"/> model.
/// The pipeline enables only what the locked format subset models: emphasis,
/// strikethrough (via EmphasisExtras), inline links, lists, headings, and
/// <c>qnote-img:</c> image links. Everything else (code spans, foreign images,
/// HTML, tables…) degrades to plain text runs — the paste-degradation rule applies
/// to loaded files too.
/// </summary>
public static class MarkdownParser
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseEmphasisExtras(EmphasisExtraOptions.Strikethrough)
        .Build();

    /// <summary>Custom scheme used for content-addressed image references in storage.</summary>
    public const string ImageSchemePrefix = "qnote-img:";

    /// <summary>
    /// Every image sha referenced by the stored Markdown, in document order
    /// (duplicates kept — callers distinct/ordinal-map as needed). The save path's
    /// <c>note_images</c> sync and the editor's pict-ordinal map both key off this.
    /// </summary>
    public static IReadOnlyList<string> ReferencedImageShas(string markdown) =>
        Parse(markdown)
            .Blocks
            .SelectMany(b => b.Inlines.OfType<DocumentImage>())
            .Select(i => i.Sha256)
            .ToList();

    public static DocumentContent Parse(string markdown)
    {
        var document = Markdig.Markdown.Parse(markdown ?? string.Empty, Pipeline);
        var blocks = new List<DocumentBlock>();

        foreach (var block in document)
            CollectBlock(block, blocks);

        return new DocumentContent(blocks);
    }

    /// <summary>
    /// Block-level dispatch, recursive so container blocks outside the subset
    /// (quote blocks, custom containers) flatten to their inner paragraphs instead
    /// of being silently dropped.
    /// </summary>
    private static void CollectBlock(Block block, List<DocumentBlock> blocks)
    {
        switch (block)
        {
            case HeadingBlock heading:
                blocks.Add(new DocumentBlock(BlockKind.Heading, CollectInlines(heading.Inline))
                {
                    Level = heading.Level switch
                    {
                        <= 1 => HeadingLevel.H1,
                        2 => HeadingLevel.H2,
                        _ => HeadingLevel.H3,
                    },
                });
                break;

            case ParagraphBlock paragraph:
                blocks.Add(new DocumentBlock(BlockKind.Paragraph, CollectInlines(paragraph.Inline)));
                break;

            case ListBlock list:
                var number = 1;
                foreach (var item in list)
                {
                    if (item is not ListItemBlock listItem)
                        continue;

                    // A list item's own children are the blocks of its content;
                    // tight lists yield bare ParagraphBlocks.
                    foreach (var itemBlock in listItem)
                    {
                        var inlines = itemBlock switch
                        {
                            ParagraphBlock p => CollectInlines(p.Inline),
                            HeadingBlock h => CollectInlines(h.Inline),
                            _ => [],
                        };
                        if (inlines.Count == 0)
                            continue;
                        blocks.Add(new DocumentBlock(BlockKind.ListItem, inlines)
                        {
                            Ordered = list.IsOrdered,
                            ListNumber = number,
                        });
                    }

                    number++;
                }

                break;

            case QuoteBlock quote:
                foreach (var sub in quote)
                    CollectBlock(sub, blocks);
                break;

            // Code blocks and other leaf blocks outside the subset: keep their text.
            case LeafBlock leaf when leaf.Inline is not null:
                blocks.Add(new DocumentBlock(BlockKind.Paragraph, CollectInlines(leaf.Inline)));
                break;

            // Thematic breaks etc. carry no content — drop them.
        }
    }

    private static List<DocumentInline> CollectInlines(ContainerInline? container)
    {
        var result = new List<DocumentInline>();
        if (container is null)
            return result;

        foreach (var inline in container)
            CollectInto(inline, result);

        return result;
    }

    private static void CollectInto(Inline inline, List<DocumentInline> result)
    {
        switch (inline)
        {
            case LiteralInline literal:
                AppendRun(result, literal.Content.ToString(), bold: false, italic: false, strike: false);
                break;

            case CodeInline code:
                AppendRun(result, code.Content, bold: false, italic: false, strike: false);
                break;

            case LineBreakInline:
                // Soft/hard break inside a paragraph: keep as a newline in the run text;
                // emitters map it (\line for RTF, "\n" for Markdown).
                AppendRun(result, "\n", bold: false, italic: false, strike: false);
                break;

            case EmphasisInline emphasis:
                // Markdig 1.x models strikethrough as a '~' EmphasisInline (the
                // standalone StrikethroughInline type is gone). DelimiterCount 3 = the
                // "***bold italic***" combined form.
                var (b, i, s) = ClassifyEmphasis(emphasis);
                foreach (var child in emphasis)
                    CollectFormatted(child, bold: b, italic: i, strike: s,
                        navigateUri: null, result);
                break;

            case LinkInline link:
                var url = link.Url ?? string.Empty;
                if (link.IsImage)
                {
                    if (url.StartsWith(ImageSchemePrefix, StringComparison.Ordinal))
                    {
                        result.Add(new DocumentImage(url[ImageSchemePrefix.Length..],
                            ExtractText(link)));
                    }
                    else
                    {
                        // Foreign images are outside the subset: keep the alt text.
                        foreach (var child in link)
                            CollectInto(child, result);
                    }

                    break;
                }

                // Inline links are in the subset (WRE bridge, 2026-09-30): the URL
                // travels with every text run under the link. An empty URL degrades
                // to the plain text — a link to nothing is just text.
                if (url.Length > 0)
                    foreach (var child in link)
                        CollectFormatted(child, bold: false, italic: false, strike: false,
                            navigateUri: url, result);
                else
                    foreach (var child in link)
                        CollectInto(child, result);
                break;

            case AutolinkInline autolink:
                // `<https://…>` (CommonMark §6.7) is its own inline type in Markdig
                // 1.4, NOT a LinkInline — the LeafInline fall-through below would
                // keep the text but silently drop the link. The visible text is the
                // URL itself.
                AppendRun(result, autolink.ToString() ?? string.Empty, bold: false,
                    italic: false, strike: false,
                    navigateUri: autolink.Url is { Length: > 0 } autoUrl ? autoUrl : null);
                break;

            case ContainerInline container:
                foreach (var child in container)
                    CollectInto(child, result);
                break;

            case LeafInline leafInline:
                AppendRun(result, leafInline.ToString() ?? string.Empty,
                    bold: false, italic: false, strike: false);
                break;
        }
    }

    /// <summary>Map an emphasis node to (bold, italic, strikethrough) flags.</summary>
    private static (bool Bold, bool Italic, bool Strikethrough) ClassifyEmphasis(
        EmphasisInline emphasis) =>
        emphasis.DelimiterChar switch
        {
            '~' when emphasis.DelimiterCount >= 2 => (false, false, true),
            '*' or '_' => (emphasis.DelimiterCount >= 2,
                emphasis.DelimiterCount is 1 or 3,
                false),
            _ => (false, false, false),
        };

    /// <summary>
    /// Collect a formatted subtree, merging the format flags downward (e.g. bold
    /// inside italic becomes a bold+italic run; strikethrough wrapping anything
    /// propagates the strike flag; a link wrapping anything propagates its URL).
    /// </summary>
    private static void CollectFormatted(Inline inline, bool bold, bool italic, bool strike,
        string? navigateUri, List<DocumentInline> result)
    {
        switch (inline)
        {
            case LiteralInline lit:
                AppendRun(result, lit.Content.ToString(), bold, italic, strike, navigateUri);
                break;

            case EmphasisInline emphasis:
                var (b, i, s) = ClassifyEmphasis(emphasis);
                foreach (var child in emphasis)
                    CollectFormatted(child, bold || b, italic || i, strike || s, navigateUri, result);
                break;

            case LinkInline { IsImage: true, Url: not null } image
                    when image.Url.StartsWith(ImageSchemePrefix, StringComparison.Ordinal):
                result.Add(new DocumentImage(image.Url[ImageSchemePrefix.Length..],
                    ExtractText(image)));
                break;

            // A link nested inside emphasis keeps BOTH: the accumulated outer flags
            // and its own URL — without this the emphasis around "**[x](u)**" was
            // dropped on re-read (found by the WRE round-trip matrix, 2026-09-30).
            case LinkInline { IsImage: false, Url: { Length: > 0 } nestedUrl } nestedLink:
                foreach (var child in nestedLink)
                    CollectFormatted(child, bold, italic, strike, navigateUri: nestedUrl, result);
                break;

            default:
                CollectInto(inline, result);
                break;
        }
    }

    /// <summary>
    /// Append text to the result, merging into the previous run ONLY when the flags
    /// AND the link target match — merging across different formats would silently
    /// re-format plain text (e.g. a trailing " normal" absorbing into the preceding
    /// strikethrough run; a linked word absorbing an unlinked neighbour).
    /// </summary>
    private static void AppendRun(List<DocumentInline> result, string text,
        bool bold, bool italic, bool strike, string? navigateUri = null)
    {
        if (text.Length == 0)
            return;
        if (result.Count > 0 && result[^1] is DocumentRun last &&
            last.Bold == bold && last.Italic == italic && last.Strikethrough == strike &&
            last.NavigateUri == navigateUri)
            result[^1] = last with { Text = last.Text + text };
        else
            result.Add(new DocumentRun(text, bold, italic, strike, navigateUri));
    }

    private static string ExtractText(ContainerInline container)
    {
        var buffer = new System.Text.StringBuilder();
        AppendAllText(container, buffer);
        return buffer.ToString();
    }

    private static void AppendAllText(ContainerInline container, System.Text.StringBuilder buffer)
    {
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline lit:
                    buffer.Append(lit.Content);
                    break;
                case ContainerInline nested:
                    AppendAllText(nested, buffer);
                    break;
            }
        }
    }
}
