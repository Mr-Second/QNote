using System.Text;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Controls;
using QNote.Markdown;

namespace QNote.Controls;

/// <summary>
/// Save-direction bridge: walks the RichEdit TOM (paragraph formats + per-character
/// formats) into the neutral <see cref="DocumentContent"/> model, which
/// <see cref="MarkdownEmitter"/> then turns into storage Markdown. The reverse
/// direction (MD → RTF) is pure Core (<see cref="RtfEmitter"/>); only this thin
/// view-side walker touches TOM. All TOM values flow through <c>var</c> — the
/// projection keeps interface type names out of reach, so no signature may name them.
///
/// Detection rules (the format subset has no other producers in the UI):
/// — Headings: first non-space character is bold and ≥ the band threshold
///   (H1 ≥ 18pt, H2 ≥ 15pt, H3 ≥ 12pt; body text is 11pt, so no false positives).
/// — Lists: paragraph ListType Bullet / Arabic; the B2 subset is flat single-level,
///   so the level index is ignored.
/// — Images: U+FFFC placeholders, resolved by ordinal against the caller-supplied
///   sha list (byte-identity-matched against <c>note_images</c> by the controller).
/// — Soft line breaks: RichEdit's VT character becomes '\n' in run text.
///
/// Not unit-testable headless (needs a live RichEditBox) — covered by the round-trip
/// matrix in-app (task step ⑤).
/// </summary>
public static class TomDocumentWalker
{
    private const char PictPlaceholder = '\uFFFC';
    private const char SoftLineBreak = '\v';

    /// <param name="editor">The RichEditBox whose document to walk.</param>
    /// <param name="pictShas">
    /// Sha of each U+FFFC in document order (<c>null</c> = unresolvable; the image
    /// is then dropped rather than emitted with a bogus reference).
    /// </param>
    public static DocumentContent Walk(RichEditBox editor, IReadOnlyList<string?> pictShas)
    {
        var document = editor.Document;
        document.GetText(TextGetOptions.None, out var text);

        var blocks = new List<DocumentBlock>();
        var pictOrdinal = 0;
        var listOrdered = false;
        var listNumber = 1;

        var paraStart = 0;
        while (paraStart < text.Length)
        {
            var paraEnd = text.IndexOf('\r', paraStart);
            if (paraEnd < 0)
                paraEnd = text.Length;

            var inlines = WalkParagraph();
            var isBlank = inlines.Count == 0 || IsWhitespaceOnly(inlines);
            if (!isBlank)
            {
                var listType = document.GetRange(paraStart, paraStart).ParagraphFormat.ListType;
                var isListItem = listType is MarkerType.Bullet or MarkerType.Arabic;
                if (isListItem)
                {
                    var ordered = listType == MarkerType.Arabic;
                    if (!ordered || !listOrdered)
                        listNumber = 1; // a new list (or a bullet↔number switch) restarts numbering
                    blocks.Add(new DocumentBlock(BlockKind.ListItem, inlines)
                    {
                        Ordered = ordered,
                        ListNumber = listNumber++,
                    });
                    listOrdered = ordered;
                }
                else
                {
                    listNumber = 1; // leaving a list restarts its counter
                    var heading = ClassifyHeading();
                    blocks.Add(heading is { } level
                        ? new DocumentBlock(BlockKind.Heading, inlines) { Level = level }
                        : new DocumentBlock(BlockKind.Paragraph, inlines));
                }
            }
            else
            {
                // Blank lines end the current list so the next item starts at 1 again.
                listNumber = 1;
            }

            paraStart = paraEnd + 1;
            continue;

            // ---------- local helpers (closures over the current paragraph) ----------

            List<DocumentInline> WalkParagraph()
            {
                var result = new List<DocumentInline>();
                var buffer = new StringBuilder();
                var runBold = false;
                var runItalic = false;
                var runStrike = false;
                var runOpen = false;

                for (var i = paraStart; i < paraEnd; i++)
                {
                    var c = text[i];
                    if (c == PictPlaceholder)
                    {
                        FlushRun();
                        var sha = pictOrdinal < pictShas.Count ? pictShas[pictOrdinal] : null;
                        if (sha is not null)
                            result.Add(new DocumentImage(sha));
                        pictOrdinal++;
                        continue;
                    }

                    var format = document.GetRange(i, i + 1).CharacterFormat;
                    var bold = IsOn(format.Bold);
                    var italic = IsOn(format.Italic);
                    var strike = IsOn(format.Strikethrough);

                    if (runOpen && (bold != runBold || italic != runItalic || strike != runStrike))
                        FlushRun();

                    runBold = bold;
                    runItalic = italic;
                    runStrike = strike;
                    runOpen = true;
                    buffer.Append(c == SoftLineBreak ? '\n' : c);
                }

                FlushRun();
                return result;

                void FlushRun()
                {
                    if (!runOpen || buffer.Length == 0)
                        return;
                    result.Add(new DocumentRun(buffer.ToString(), runBold, runItalic, runStrike));
                    buffer.Clear();
                    runOpen = false;
                }
            }

            HeadingLevel? ClassifyHeading()
            {
                for (var i = paraStart; i < paraEnd; i++)
                {
                    var c = text[i];
                    if (c == PictPlaceholder || char.IsWhiteSpace(c))
                        continue;

                    var format = document.GetRange(i, i + 1).CharacterFormat;
                    if (!IsOn(format.Bold))
                        return null;
                    var size = format.Size;
                    if (size >= 18f)
                        return HeadingLevel.H1;
                    if (size >= 15f)
                        return HeadingLevel.H2;
                    if (size >= 12f)
                        return HeadingLevel.H3;
                    return null;
                }
                return null;
            }
        }

        return new DocumentContent(blocks);
    }

    private static bool IsOn(FormatEffect effect) =>
        effect is FormatEffect.On or FormatEffect.Toggle;

    private static bool IsWhitespaceOnly(List<DocumentInline> inlines) =>
        inlines.All(i => i is DocumentRun run && string.IsNullOrWhiteSpace(run.Text));
}
