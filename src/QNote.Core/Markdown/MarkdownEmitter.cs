using System.Text;

namespace QNote.Markdown;

/// <summary>
/// Emits storage Markdown from the neutral <see cref="DocumentContent"/> model — the
/// save direction. Text is escaped so arbitrary note content round-trips as plain
/// text instead of being reinterpreted as syntax.
/// </summary>
public static class MarkdownEmitter
{
    public static string Emit(DocumentContent content)
    {
        var buffer = new StringBuilder();

        foreach (var block in content.Blocks)
        {
            switch (block.Kind)
            {
                case BlockKind.Heading:
                    buffer.Append('#', (int)block.Level).Append(' ');
                    EmitInlines(buffer, block.Inlines);
                    break;

                case BlockKind.ListItem:
                    buffer.Append(block.Ordered
                        ? $"{block.ListNumber}. "
                        : "- ");
                    EmitInlines(buffer, block.Inlines);
                    break;

                default:
                    EmitInlines(buffer, block.Inlines);
                    break;
            }

            buffer.Append('\n');
        }

        return buffer.ToString();
    }

    private static void EmitInlines(StringBuilder buffer, IReadOnlyList<DocumentInline> inlines)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case DocumentRun run:
                    EmitRun(buffer, run);
                    break;
                case DocumentImage image:
                    buffer.Append("![").Append(Escape(image.AltText, AtLineStart: false))
                        .Append("](").Append(MarkdownParser.ImageSchemePrefix)
                        .Append(image.Sha256).Append(')');
                    break;
            }
        }
    }

    private static void EmitRun(StringBuilder buffer, DocumentRun run)
    {
        var text = Escape(run.Text, AtLineStart: true);
        if (text.Length == 0)
            return;

        // Independent markers (not else-if): a bold+strikethrough run needs both.
        // Open order bold→italic→strike, close in reverse, so re-parsing merges the
        // flags back deterministically (round-trip stable).
        if (run.Bold)
            buffer.Append("**");
        if (run.Italic)
            buffer.Append('*');
        if (run.Strikethrough)
            buffer.Append("~~");

        buffer.Append(text);

        if (run.Strikethrough)
            buffer.Append("~~");
        if (run.Italic)
            buffer.Append('*');
        if (run.Bold)
            buffer.Append("**");
    }

    /// <summary>
    /// Escape characters that Markdown could reinterpret as syntax. Always escapes the
    /// inline-marker set; additionally guards line-start contexts (headings, list
    /// markers, block quotes, leading numbers followed by a dot).
    /// </summary>
    internal static string Escape(string text, bool AtLineStart)
    {
        if (text.Length == 0)
            return string.Empty;

        var buffer = new StringBuilder(text.Length + 8);
        var atLineStart = AtLineStart;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var escaped = c switch
            {
                '\\' or '`' or '*' or '_' or '~' or '[' or ']' => true,
                // Line-start guards: "# ", "- ", "+ ", "> " shapes.
                '#' or '-' or '+' or '>' when atLineStart => true,
                // Leading "N. " would start an ordered list — the DOT is the escapable
                // char (CommonMark forbids backslash before digits), not the digits.
                // EndsLineStartDigits itself walks back over the digits to a line
                // start, so no atLineStart guard here (digits reset it mid-run).
                '.' when EndsLineStartDigits(text, i) => true,
                _ => false,
            };

            if (escaped)
                buffer.Append('\\');
            buffer.Append(c);

            atLineStart = c is '\n' or '\r';
        }

        return buffer.ToString();
    }

    /// <summary>True when <paramref name="dotIndex"/> follows one or more digits that
    /// begin at the line start, and a space/tab follows the dot.</summary>
    private static bool EndsLineStartDigits(string text, int dotIndex)
    {
        if (dotIndex + 1 >= text.Length || text[dotIndex + 1] is not (' ' or '\t'))
            return false;

        var i = dotIndex;
        var sawDigit = false;
        while (i > 0 && char.IsAsciiDigit(text[i - 1]))
        {
            i--;
            sawDigit = true;
        }

        return sawDigit && (i == 0 || text[i - 1] is '\n' or '\r');
    }
}
