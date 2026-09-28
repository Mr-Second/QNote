using System.Text;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace QNote.Markdown;

/// <summary>
/// Extracts plain, syntax-free text from stored Markdown — the PlainText/FTS corpus
/// and list-preview source once content is Markdown (replaces the RTF-strip +
/// U+FFFC cleanup path of the RTF era). Images contribute nothing.
/// </summary>
public static class MarkdownText
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseEmphasisExtras(Markdig.Extensions.EmphasisExtras.EmphasisExtraOptions.Strikethrough)
        .Build();

    /// <summary>Full plain text: blocks joined by newlines, inlines by nothing.</summary>
    public static string ToPlainText(string markdown)
    {
        if (string.IsNullOrEmpty(markdown))
            return string.Empty;

        var document = Markdig.Markdown.Parse(markdown, Pipeline);
        var buffer = new System.Text.StringBuilder();

        foreach (var block in document)
            AppendBlockText(block, buffer);

        return buffer.ToString().TrimEnd('\n');
    }

    /// <summary>
    /// Block-level walk: leaf blocks contribute their inline text; container blocks
    /// (lists, quotes) are descended into, or their items' text would be lost.
    /// </summary>
    private static void AppendBlockText(Block block, StringBuilder buffer)
    {
        switch (block)
        {
            case LeafBlock { Inline: not null } leaf:
                var wroteAny = false;
                foreach (var inline in leaf.Inline)
                    wroteAny |= AppendInlineText(inline, buffer);
                if (wroteAny)
                    buffer.Append('\n');
                break;

            case ContainerBlock container:
                foreach (var child in container)
                    AppendBlockText(child, buffer);
                break;
        }
    }

    /// <summary>
    /// Append an inline subtree's text. Emphasis/link containers must be descended
    /// into, or their literal children (the actual visible text) get skipped.
    /// </summary>
    private static bool AppendInlineText(Inline inline, StringBuilder buffer)
    {
        switch (inline)
        {
            case LiteralInline literal:
                buffer.Append(literal.Content);
                return true;
            case CodeInline code:
                buffer.Append(code.Content);
                return true;
            case LineBreakInline:
                buffer.Append('\n');
                return false;
            case LinkInline link when link.IsImage:
                // Image alt text is not visible text — images contribute nothing.
                return false;
            case ContainerInline container:
                var wroteAny = false;
                foreach (var child in container)
                    wroteAny |= AppendInlineText(child, buffer);
                return wroteAny;
            default:
                return false;
        }
    }

    /// <summary>
    /// Single-line preview text: first non-empty line flattened for the note list card
    /// (replaces <c>MakePreview</c>'s RTF-era pipeline; no markdown markers leak).
    /// </summary>
    public static string ToPreviewLine(string markdown, int maxLength)
    {
        var text = ToPlainText(markdown);
        var builder = new StringBuilder(text.Length);

        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
                continue;
            builder.Append(trimmed);
            break;
        }

        if (builder.Length > maxLength)
            builder.Length = maxLength;

        return builder.ToString();
    }
}
