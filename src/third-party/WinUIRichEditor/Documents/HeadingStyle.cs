using System;
using System.Linq;
using Windows.UI.Text;

namespace WinUIRichEditor.Documents;

// A heading's look lives on its RUNS. Applying a level writes bold and the level's size onto the text; until
// the next application they are ordinary run attributes — bold can be turned off, any size set, and it shows.
//
// Until 2026-09-12 the renderer forced both instead: every run of a heading was drawn bold, and a run with
// no size of its own was drawn at the heading's size. So a heading could not be un-bolded, 10pt could not be
// shown in one, and the bold toggle was made to do nothing there rather than flip a hidden flag. User
// decision: apply the heading's format whenever a heading is applied, and let changes in between through.
// Upstream (AvaloniaRichEditor) still forces both.
internal static class HeadingStyle
{
    // Run.FontSize's default, which the model has always read as "no size chosen".
    internal const double BodySize = 10;
    private static readonly FontWeight Bold = new() { Weight = 700 };
    private static readonly FontWeight Normal = new() { Weight = 400 };

    internal static bool IsHeading(int level) => level is >= 1 and <= 6;

    internal static double Size(int level)
        => level switch { 1 => 20, 2 => 16, 3 => 14, 4 => 12, 5 => 11, 6 => 10, _ => BodySize };

    private static bool Near(double a, double b) => Math.Abs(a - b) < 0.01;

    private static bool IsUnstyledSize(double size) => size <= 0 || Near(size, BodySize);

    // The heading's preset on one run: what text typed into a heading with no text to copy a format from gets.
    internal static void ApplyPreset(Run r, int level)
    {
        r.FontWeight = Bold;
        r.FontSize = Size(level);
    }

    // The empty run a paragraph is left holding (after an Enter that moved all its text away): in a heading
    // it carries the preset, so typing there writes heading text and not plain body text.
    internal static Run EmptyRun(int level)
    {
        var r = new Run { Text = "" };
        if (IsHeading(level)) ApplyPreset(r, level);
        return r;
    }

    // Rewrites a paragraph's runs for a level change `from` → `to` (0 = body). Applying a heading applies its
    // FORMAT: every run gets the level's preset (bold, its size) — on first application, on a change of level,
    // and on re-applying the same level, which is how the heading's look comes back after the user changed
    // its bold or size (user decision, 2026-09-13; the first cut kept user changes across level changes).
    // The user's changes stand until the next application. Back to body takes the preset off the same way:
    // every run to normal weight and the body size (the host default, as ClearFormatting writes it). Body to
    // body changes nothing — that would wipe the bold words of an ordinary paragraph.
    internal static void Retype(Paragraph p, int from, int to, double hostDefault)
    {
        if (IsHeading(to))
            foreach (var r in p.Inlines.OfType<Run>()) ApplyPreset(r, to);
        else if (IsHeading(from))
            foreach (var r in p.Inlines.OfType<Run>()) { r.FontWeight = Normal; r.FontSize = hostDefault; }
    }

    // A document whose headings predate run-level heading formats — files written before 2026-09-12, files
    // written by upstream, a host's own model — gets them written on, exactly as the old renderer showed them:
    // every heading run bold, an unsized run at the heading's size. Once: the flag travels with the document
    // (Clone, the JSON marker), so text the user un-bolded is never re-bolded by an undo or a reload.
    internal static void Materialize(FlowDocument doc)
    {
        if (doc.HeadingFormatsApplied) return;
        doc.HeadingFormatsApplied = true;
        foreach (var p in BlockWalk.Paragraphs(doc.Blocks))
        {
            if (!IsHeading(p.HeadingLevel)) continue;
            foreach (var r in p.Inlines.OfType<Run>())
            {
                if (IsUnstyledSize(r.FontSize)) r.FontSize = Size(p.HeadingLevel);
                r.FontWeight = Bold;
            }
        }
    }
}
