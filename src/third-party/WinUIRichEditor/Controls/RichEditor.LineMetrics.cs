using System;
using System.Collections.Generic;
using Microsoft.Graphics.Canvas.Text;

namespace WinUIRichEditor.Controls;

// Per-visual-line metrics, and the reason this file exists.
//
// Win2D's CanvasTextLayout.LineMetrics returns CanvasLineMetrics[]. That struct carries a bool
// (IsTrimmed), which makes the array NON-BLITTABLE, and CsWinRT cannot marshal it under Native AOT:
//
//     NotSupportedException 0x80131515 — Cannot handle array marshalling for non blittable ...
//
// Every one of the eight callers wrapped the access in a try/catch and fell back to "no lines". Under
// AOT that meant, silently and on every frame: list markers lost their baseline alignment, vertical
// caret movement across wrapped lines stopped working, and pagination measured every paragraph as a
// single line. The build ran and rendered, so it looked fine — the failure only became visible when
// RichEditorDiagnostics started reporting swallowed faults.
//
// The fix is not to stop using LineMetrics: on the JIT/CoreCLR path it is correct and cheap, and this
// code keeps using it there. It is to give the AOT path a real fallback instead of an empty array.
// GetCharacterRegions returns CanvasTextLayoutRegion[] — ints and a Rect, blittable, works under AOT —
// and per-line Height and CharacterCount can be rebuilt from it.
//
// Baseline has no equivalent in the region API. Under uniform line spacing — every paragraph since
// 2026-09-15 made 160% the default, unless a tall inline object forces content-driven spacing — it is
// exactly LineSpacingBaseline for every line, so the fallback returns that. Otherwise it comes back NaN,
// which every consumer handles (see CaretInLayout's "no metrics: fall back to the line box"). NaN used to
// be the only answer; once uniform spacing became the default it made the AOT caret as tall as the whole
// 160% line box on every line.
//
// A paragraph ending in a soft break ('\n') ends on an EMPTY visual line that no character region covers.
// The native metrics report it (0 characters); the fallback recovers it as the part of LayoutBounds the
// region lines don't cover. Missing it made Up/Down skip that line and drew its list marker over the
// first line's.
//
// The fallback was validated against the real thing, since it can only RUN where the real thing cannot:
// a temporary Debug-only self-check computed both for every layout the demo built (headings, wrapped
// body text, table cells, list paragraphs) and reported any disagreement through RichEditorDiagnostics.
// Line count, per-line CharacterCount and per-line Height (±0.75px) matched everywhere; nothing was
// reported. That check was removed, and the trailing-soft-break gap above was found a month later by its
// replacement, LineMetricsFallbackTests, which compares the two on the same layouts in every test run.
public partial class RichEditor
{
    /// <summary>What the callers of <c>LineMetrics</c> actually read. <see cref="Baseline"/> is
    /// <see cref="double.NaN"/> when it could not be determined (the Native AOT fallback, without
    /// uniform line spacing).</summary>
    private readonly record struct LineMetric(double Height, int CharacterCount, double Baseline);

    // Latched after the first failure. Under AOT this never starts working, and the throw is not free:
    // without the latch every paragraph would raise and catch an exception on every frame.
    private static bool _lineMetricsUnsupported;

    // Per-line metrics for `layout`, whose text is `textLength` characters long. Never throws; an empty
    // result means the layout could not be measured at all.
    private static LineMetric[] LineMetricsOf(CanvasTextLayout layout, int textLength)
    {
        if (!_lineMetricsUnsupported)
        {
            try
            {
                CanvasLineMetrics[] native = layout.LineMetrics;
                var result = new LineMetric[native.Length];
                for (int i = 0; i < native.Length; i++)
                    result[i] = new LineMetric(native[i].Height, native[i].CharacterCount, native[i].Baseline);
                return result;
            }
            catch (Exception ex)
            {
                RichEditorDiagnostics.Report(ex);
                _lineMetricsUnsupported = true;
            }
        }
        return LineMetricsFromRegions(layout, textLength);
    }

    // Rebuilds line structure from character regions. Regions arrive in document order, so a line ends
    // when the next region's top drops below the current one.
    private static LineMetric[] LineMetricsFromRegions(CanvasTextLayout layout, int textLength)
    {
        double baseline = double.NaN;
        try
        {
            if (layout.LineSpacingMode == CanvasLineSpacingMode.Uniform) baseline = layout.LineSpacingBaseline;
        }
        catch (Exception ex) { RichEditorDiagnostics.Report(ex); }

        if (textLength <= 0)
        {
            // An empty paragraph still occupies one line, and callers divide by / index into the line
            // list — returning nothing here would make a blank line unnavigable.
            double h = 0;
            try { h = layout.LayoutBounds.Height; }
            catch (Exception ex) { RichEditorDiagnostics.Report(ex); }
            return new[] { new LineMetric(h, 0, baseline) };
        }

        CanvasTextLayoutRegion[] regions;
        try { regions = layout.GetCharacterRegions(0, textLength); }
        catch (Exception ex) { RichEditorDiagnostics.Report(ex); return Array.Empty<LineMetric>(); }

        var lines = new List<LineMetric>();
        double lineTop = double.NaN, lineHeight = 0;
        int lineChars = 0;
        foreach (var r in regions)
        {
            var b = r.LayoutBounds;
            // 0.5px of slack: regions on one line share a top, but floating point and mixed run heights
            // can jitter it. A real wrap moves the top by a whole line.
            bool newLine = double.IsNaN(lineTop) || b.Y > lineTop + 0.5;
            if (newLine)
            {
                if (!double.IsNaN(lineTop)) lines.Add(new LineMetric(lineHeight, lineChars, baseline));
                lineTop = b.Y;
                lineHeight = b.Height;
                lineChars = 0;
            }
            else if (b.Height > lineHeight) lineHeight = b.Height; // tallest run sets the line box
            lineChars += r.CharacterCount;
        }
        if (!double.IsNaN(lineTop)) lines.Add(new LineMetric(lineHeight, lineChars, baseline));
        if (lines.Count == 0) return Array.Empty<LineMetric>();

        // Callers map an offset to a line by accumulating CharacterCount, so the counts MUST cover the
        // whole paragraph. Regions can omit trailing characters that occupy no glyph box (a trailing
        // newline, trimmed whitespace); give the remainder to the last line rather than let every
        // offset past that point resolve to the wrong line.
        int counted = 0;
        foreach (var l in lines) counted += l.CharacterCount;
        if (counted < textLength)
        {
            var last = lines[^1];
            lines[^1] = last with { CharacterCount = last.CharacterCount + (textLength - counted) };
        }

        // The empty line after a trailing soft break has no region, but LayoutBounds includes it: whatever
        // height the lines above don't account for IS that line. (The caret region at the text's end is
        // no use here — its Y is the glyph top, not the line top, so it cannot tell the two cases apart.)
        try
        {
            double sum = 0;
            foreach (var l in lines) sum += l.Height;
            double rest = layout.LayoutBounds.Height - sum;
            if (rest > 0.5) lines.Add(new LineMetric(rest, 0, baseline));
        }
        catch (Exception ex) { RichEditorDiagnostics.Report(ex); }
        return lines.ToArray();
    }
}
