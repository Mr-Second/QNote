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
        var emittedAny = false;
        var afterTable = false;

        foreach (var block in content.Blocks)
        {
            // Tables manage their own blank-line separators (Markdig-verified
            // 2026-10-01: a plain line directly after the rows absorbs the whole
            // construct into one paragraph, a preceding list item lazily swallows
            // the rows, and two touching tables fuse) and stay fully transparent
            // when empty.
            if (block.Kind == BlockKind.Table)
            {
                if (block.TableCells is { Count: > 0 } cells)
                {
                    if (emittedAny)
                        buffer.Append('\n');
                    EmitTable(buffer, cells);
                    emittedAny = true;
                    afterTable = true;
                }
                continue;
            }

            // Thematic breaks: a blank line BEFORE is mandatory (Markdig-verified
            // 2026-10-01: "text\n---" re-parses as a setext H2, and a "---" tail
            // absorbs a pipe table the same way a plain line does). Nothing after
            // one needs a separator — paragraph, list, table and another break all
            // parse cleanly off a fresh "---" line.
            if (block.Kind == BlockKind.Divider)
            {
                if (emittedAny)
                    buffer.Append('\n');
                buffer.Append("---\n");
                emittedAny = true;
                afterTable = false; // a divider is a safe left neighbour
                continue;
            }

            if (emittedAny && afterTable)
                buffer.Append('\n');
            afterTable = false;

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
            emittedAny = true;
        }

        return buffer.ToString();
    }

    /// <summary>
    /// Emit one GFM pipe table: row 0 doubles as the header (the delimiter row opens
    /// the table on re-parse), the rest are body rows. Column count is the grid's
    /// width — the model guarantees a dense rectangular grid.
    /// </summary>
    private static void EmitTable(StringBuilder buffer,
        IReadOnlyList<IReadOnlyList<IReadOnlyList<DocumentInline>>> cells)
    {
        var columns = Math.Max(1, cells.Max(row => row.Count));

        for (var r = 0; r < cells.Count; r++)
        {
            if (r == 1)
            {
                // The delimiter row after the header — GFM requires it to recognize
                // the construct at all.
                buffer.Append('|');
                for (var c = 0; c < columns; c++)
                    buffer.Append(" --- |");
                buffer.Append('\n');
            }

            var row = cells[r];
            buffer.Append('|');
            for (var c = 0; c < columns; c++)
            {
                buffer.Append(' ');
                EmitInlines(buffer, c < row.Count ? row[c] : [], inTableCell: true);
                buffer.Append(" |");
            }
            buffer.Append('\n');
        }

        if (cells.Count == 1)
        {
            // Header-only table still needs its delimiter row to re-parse as a table.
            buffer.Append('|');
            for (var c = 0; c < columns; c++)
                buffer.Append(" --- |");
            buffer.Append('\n');
        }
    }

    private static void EmitInlines(StringBuilder buffer, IReadOnlyList<DocumentInline> inlines,
        bool inTableCell = false)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case DocumentRun run:
                    EmitRun(buffer, run, inTableCell);
                    break;
                case DocumentImage image:
                    // A newline inside a cell would split the GFM row — degrade to a
                    // space (cells cannot hold breaks; the collect side does the same).
                    var alt = inTableCell ? image.AltText.Replace("\n", " ") : image.AltText;
                    buffer.Append("![").Append(Escape(alt, AtLineStart: false, inTableCell))
                        .Append("](").Append(MarkdownParser.ImageSchemePrefix)
                        .Append(image.Sha256).Append(')');
                    break;
            }
        }
    }

    private static void EmitRun(StringBuilder buffer, DocumentRun run, bool inTableCell = false)
    {
        // Table cells sit mid-line: none of the line-start shapes can trigger there,
        // but a bare '|' would split the cell and a newline would break the row
        // (both degrade to a space — the collect side keeps cells canonical).
        if (inTableCell && run.Text.AsSpan().IndexOf('\n') >= 0)
            run = run with { Text = run.Text.Replace("\n", " ") };
        var text = Escape(run.Text, AtLineStart: !inTableCell, inTableCell);
        if (text.Length == 0)
            return;

        // Independent markers (not else-if): a bold+strikethrough run needs both.
        // Open order bold→italic→strike, close in reverse, so re-parsing merges the
        // flags back deterministically (round-trip stable). A linked run nests the
        // link INSIDE the emphasis markers — **[x](u)** re-parses to the same run.
        if (run.Bold)
            buffer.Append("**");
        if (run.Italic)
            buffer.Append('*');
        if (run.Strikethrough)
            buffer.Append("~~");

        if (run.NavigateUri is { Length: > 0 } uri)
            buffer.Append('[').Append(text).Append("](").Append(EscapeUrl(uri)).Append(')');
        else
            buffer.Append(text);

        if (run.Strikethrough)
            buffer.Append("~~");
        if (run.Italic)
            buffer.Append('*');
        if (run.Bold)
            buffer.Append("**");
    }

    /// <summary>
    /// Escape a link destination for the bare <c>(…)</c> form. Only characters that
    /// would break out of the destination are percent-encoded (whitespace, the
    /// parentheses themselves, backslash, control characters); ordinary URLs emit
    /// byte-for-byte so a round trip is string-identical.
    /// </summary>
    private static string EscapeUrl(string url)
    {
        if (url.AsSpan().IndexOfAny("(\\) \t\r\n") < 0 && !url.Any(char.IsControl))
            return url;

        var buffer = new StringBuilder(url.Length + 8);
        foreach (var c in url)
        {
            if (c is '(' or ')' or '\\' or ' ' or '\t' or '\r' or '\n' || char.IsControl(c))
                buffer.Append('%').Append(((int)c).ToString("X2"));
            else
                buffer.Append(c);
        }

        return buffer.ToString();
    }

    /// <summary>
    /// Escape characters that Markdown could reinterpret as syntax. Always escapes the
    /// inline-marker set; additionally guards line-start contexts (headings, list
    /// markers, block quotes, leading numbers followed by a dot) and, inside table
    /// cells, the pipe that would otherwise split the cell.
    /// </summary>
    internal static string Escape(string text, bool AtLineStart, bool inTableCell = false)
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
                // A pipe would split the GFM cell in two.
                '|' when inTableCell => true,
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
