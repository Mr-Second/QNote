using System;
using System.Collections.Generic;
using System.Text;

namespace WinUIRichEditor.Documents;

/// <summary>A logical range between two <see cref="TextPointer"/> positions in a <see cref="FlowDocument"/>.
/// Supports text extraction, deletion, and property (formatting) application across paragraph boundaries.</summary>
public class TextRange
{
    private TextPointer _start;
    private TextPointer _end;

    /// <summary>The start (earlier) position of the range.</summary>
    public TextPointer Start => _start;
    /// <summary>The end (later) position of the range.</summary>
    public TextPointer End => _end;

    /// <summary>Creates a range from two positions (order does not matter; the earlier becomes <see cref="Start"/>).</summary>
    public TextRange(TextPointer position1, TextPointer position2)
    {
        if (position1.CompareTo(position2) <= 0)
        {
            _start = position1;
            _end = position2;
        }
        else
        {
            _start = position2;
            _end = position1;
        }
    }

    /// <summary><see langword="true"/> when Start and End are at the same position (nothing selected).</summary>
    public bool IsEmpty => _start.CompareTo(_end) == 0;

    /// <summary>Deletes all content covered by this range (across paragraph boundaries).
    /// After deletion, <see cref="Start"/> and <see cref="End"/> both point to the same position.</summary>
    public void Delete()
    {
        if (IsEmpty) return;
        var sp = _start.Paragraph;
        var ep = _end.Paragraph;
        if (sp == null || ep == null) return;

        if (ReferenceEquals(sp, ep))
        {
            DeleteInParagraph(sp, _start.Offset, _end.Offset);
        }
        else
        {
            var doc = GetFlowDocument(sp);
            if (doc == null) return;

            int p1Len = GetParagraphLength(sp);
            DeleteInParagraph(sp, _start.Offset, p1Len);

            // Remove every top-level block (paragraphs, images, tables) strictly between the
            // start and end paragraphs' top-level blocks, so a drag selection that spans an
            // image or table deletes those too.
            var startTop = TopLevelBlockOf(doc, sp);
            var endTop = TopLevelBlockOf(doc, ep);
            int si = startTop != null ? doc.Blocks.IndexOf(startTop) : -1;
            int ei = endTop != null ? doc.Blocks.IndexOf(endTop) : -1;
            if (si >= 0 && ei >= 0 && ei > si)
            {
                for (int i = ei - 1; i > si; i--) doc.Blocks.RemoveAt(i);
            }

            DeleteInParagraph(ep, 0, _end.Offset);

            // Merging is only meaningful between two top-level paragraphs. When either endpoint
            // is a table cell, merging would move text across the grid (the end cell's remainder
            // used to land in the start cell) — instead keep the structure and just clear the
            // fully-covered paragraphs (cells) between the endpoints.
            bool spIsCell = !doc.Blocks.Contains(sp);
            bool epIsCell = !doc.Blocks.Contains(ep);
            if (!spIsCell && !epIsCell)
            {
                MergeParagraphs(sp, ep, doc);
            }
            else
            {
                var all = GetAllParagraphsInOrder(doc);
                int sIdx = all.IndexOf(sp);
                int eIdx = all.IndexOf(ep);
                if (sIdx >= 0 && eIdx > sIdx)
                    for (int i = sIdx + 1; i < eIdx; i++)
                        DeleteInParagraph(all[i], 0, GetParagraphLength(all[i]));
            }
        }

        _end = new TextPointer(sp, _start.Offset);
    }

    /// <summary>Returns the plain text content of this range (paragraphs joined with newlines;
    /// inline image placeholders are dropped).</summary>
    public string GetText()
    {
        if (IsEmpty) return "";
        var sp = _start.Paragraph;
        var ep = _end.Paragraph;
        if (sp == null || ep == null) return "";

        if (ReferenceEquals(sp, ep))
        {
            return GetParagraphText(sp, _start.Offset, _end.Offset);
        }

        var doc = GetFlowDocument(sp);
        if (doc == null) return "";

        var allParagraphs = GetAllParagraphsInOrder(doc);
        int startIdx = allParagraphs.IndexOf(sp);
        int endIdx = allParagraphs.IndexOf(ep);

        var sb = new StringBuilder();
        int p1Len = GetParagraphLength(sp);
        sb.Append(GetParagraphText(sp, _start.Offset, p1Len));

        for (int i = startIdx + 1; i < endIdx; i++)
        {
            sb.Append('\n');
            int pLen = GetParagraphLength(allParagraphs[i]);
            sb.Append(GetParagraphText(allParagraphs[i], 0, pLen));
        }

        sb.Append('\n');
        sb.Append(GetParagraphText(ep, 0, _end.Offset));

        return sb.ToString();
    }

    /// <summary>Returns cloned <see cref="Run"/>s covering the range with formatting preserved.
    /// Paragraph breaks are represented as <see cref="Run"/>s with <c>Text = "\n"</c>.</summary>
    public List<Run> GetRichRuns()
    {
        var result = new List<Run>();
        if (IsEmpty) return result;
        var sp = _start.Paragraph;
        var ep = _end.Paragraph;
        if (sp == null || ep == null) return result;

        if (ReferenceEquals(sp, ep))
        {
            result.AddRange(GetParagraphRuns(sp, _start.Offset, _end.Offset));
            return result;
        }

        var doc = GetFlowDocument(sp);
        if (doc == null) return result;

        var allParagraphs = GetAllParagraphsInOrder(doc);
        int startIdx = allParagraphs.IndexOf(sp);
        int endIdx = allParagraphs.IndexOf(ep);

        result.AddRange(GetParagraphRuns(sp, _start.Offset, GetParagraphLength(sp)));
        for (int i = startIdx + 1; i < endIdx; i++)
        {
            result.Add(new Run { Text = "\n" });
            result.AddRange(GetParagraphRuns(allParagraphs[i], 0, GetParagraphLength(allParagraphs[i])));
        }
        result.Add(new Run { Text = "\n" });
        result.AddRange(GetParagraphRuns(ep, 0, _end.Offset));

        return result;
    }

    private List<Run> GetParagraphRuns(Paragraph p, int startOffset, int endOffset)
    {
        var result = new List<Run>();
        int idx = 0;
        foreach (var inline in p.Inlines)
        {
            int len = InlineLen(inline);
            if (inline is Run run)
            {
                int rs = Math.Max(startOffset, idx);
                int re = Math.Min(endOffset, idx + len);
                if (re > rs)
                {
                    var clone = (Run)run.Clone();
                    clone.Text = run.Text!.Substring(rs - idx, re - rs);
                    result.Add(clone);
                }
            }
            idx += len; // inline images advance the offset but aren't captured in run-based copy
        }
        return result;
    }

    /// <summary>Like <see cref="GetRichRuns"/> but also captures atomic object inlines
    /// (<see cref="InlineImage"/>, <see cref="InlineTable"/>), so an in-app copy/paste preserves them
    /// (the run-only version drops them). Paragraph breaks are <see cref="Run"/>s with <c>Text = "\n"</c>.</summary>
    public List<Inline> GetRichInlines()
    {
        var result = new List<Inline>();
        if (IsEmpty) return result;
        var sp = _start.Paragraph;
        var ep = _end.Paragraph;
        if (sp == null || ep == null) return result;

        if (ReferenceEquals(sp, ep))
        {
            result.AddRange(GetParagraphInlines(sp, _start.Offset, _end.Offset));
            return result;
        }

        var doc = GetFlowDocument(sp);
        if (doc == null) return result;
        var all = GetAllParagraphsInOrder(doc);
        int si = all.IndexOf(sp), ei = all.IndexOf(ep);

        result.AddRange(GetParagraphInlines(sp, _start.Offset, GetParagraphLength(sp)));
        for (int i = si + 1; i < ei; i++)
        {
            result.Add(new Run { Text = "\n" });
            result.AddRange(GetParagraphInlines(all[i], 0, GetParagraphLength(all[i])));
        }
        result.Add(new Run { Text = "\n" });
        result.AddRange(GetParagraphInlines(ep, 0, _end.Offset));
        return result;
    }

    private List<Inline> GetParagraphInlines(Paragraph p, int startOffset, int endOffset)
    {
        var result = new List<Inline>();
        int idx = 0;
        foreach (var inline in p.Inlines)
        {
            int len = InlineLen(inline);
            int segStart = idx, segEnd = idx + len;
            idx = segEnd;
            if (len == 0 || segEnd <= startOffset || segStart >= endOffset) continue;
            if (inline is Run run)
            {
                int rs = Math.Max(startOffset, segStart) - segStart;
                int re = Math.Min(endOffset, segEnd) - segStart;
                if (re > rs) { var c = (Run)run.Clone(); c.Text = run.Text!.Substring(rs, re - rs); result.Add(c); }
            }
            else if (inline is not Run && segStart >= startOffset && segEnd <= endOffset)
            {
                // Atomic object inline (image, table): capture it whole when fully inside the range.
                result.Add((Inline)inline.Clone());
            }
        }
        return result;
    }

    /// <summary>Applies <paramref name="styleAction"/> to every <see cref="Run"/> within the range,
    /// splitting runs at boundaries as needed.</summary>
    public void ApplyPropertyValue(Action<Run> styleAction)
    {
        if (IsEmpty) return;
        var sp = _start.Paragraph;
        var ep = _end.Paragraph;
        if (sp == null || ep == null) return;

        if (ReferenceEquals(sp, ep))
        {
            ApplyStyleToParagraph(sp, _start.Offset, _end.Offset, styleAction);
        }
        else
        {
            var doc = GetFlowDocument(sp);
            if (doc == null) return;

            var allParagraphs = GetAllParagraphsInOrder(doc);
            int startIdx = allParagraphs.IndexOf(sp);
            int endIdx = allParagraphs.IndexOf(ep);

            int p1Len = GetParagraphLength(sp);
            ApplyStyleToParagraph(sp, _start.Offset, p1Len, styleAction);

            for (int i = startIdx + 1; i < endIdx; i++)
            {
                int pLen = GetParagraphLength(allParagraphs[i]);
                ApplyStyleToParagraph(allParagraphs[i], 0, pLen, styleAction);
            }

            ApplyStyleToParagraph(ep, 0, _end.Offset, styleAction);
        }
    }

    private string GetParagraphText(Paragraph p, int startOffset, int endOffset)
    {
        var sb = new StringBuilder();
        foreach (var inline in p.Inlines)
        {
            if (inline is Run r && r.Text != null) sb.Append(r.Text);
            else if (inline is not Run) sb.Append(ObjChar);
        }
        string fullText = sb.ToString();

        if (startOffset >= fullText.Length) return "";
        int len = Math.Min(endOffset - startOffset, fullText.Length - startOffset);
        if (len <= 0) return "";
        // Drop image placeholders from copied plain text.
        return fullText.Substring(startOffset, len).Replace(ObjChar.ToString(), "");
    }

    private void DeleteInParagraph(Paragraph p, int startOffset, int endOffset)
    {
        if (startOffset >= endOffset) return;

        SplitRunAtOffset(p, endOffset);
        SplitRunAtOffset(p, startOffset);

        int currentLeft = 0;
        var toRemove = new List<Inline>();
        foreach (var inline in p.Inlines)
        {
            int len = InlineLen(inline);
            if (len > 0 && currentLeft >= startOffset && currentLeft + len <= endOffset)
            {
                toRemove.Add(inline); // removes runs and inline images that fall inside the range
            }
            currentLeft += len;
        }
        foreach (var item in toRemove) p.Inlines.Remove(item);
        CoalesceRuns(p); // removing a run can leave its former neighbours adjacent with equal formatting
    }

    private void MergeParagraphs(Paragraph target, Paragraph source, FlowDocument doc)
    {
        foreach (var inline in source.Inlines)
        {
            inline.Parent = target;
            target.Inlines.Add(inline);
        }
        source.Inlines.Clear();

        RemoveParagraphFromDocument(doc, source);
        CoalesceRuns(target); // the boundary runs may now be adjacent with identical formatting
    }

    // Merges adjacent <see cref="Run"/>s that share all formatting into one, so an edit (paragraph
    // merge, mid-paragraph delete) doesn't leave the run list fragmented. Text and logical offsets are
    // unchanged — only the run count shrinks — so caret/selection positions stay valid. Inline images
    // act as boundaries (never merged). Shared by the editor's manual merge paths too.
    internal static void CoalesceRuns(Paragraph p)
    {
        for (int i = 0; i < p.Inlines.Count - 1; )
        {
            if (p.Inlines[i] is Run a && p.Inlines[i + 1] is Run b && RunFormatEquals(a, b))
            {
                a.Text = (a.Text ?? "") + (b.Text ?? "");
                p.Inlines.RemoveAt(i + 1); // stay at i to fold a whole run of identical neighbours
            }
            else i++;
        }
    }

    // FontWeight is a WinRT struct (Windows.UI.Text.FontWeight) with no == operator, so compare its
    // numeric Weight. Foreground/Background are nullable Color and TextDecorations a flags enum, both
    // value-equatable — no brush/decoration helpers needed (unlike the Avalonia original).
    private static bool RunFormatEquals(Run a, Run b)
        => a.FontWeight.Weight == b.FontWeight.Weight
        && a.FontStyle == b.FontStyle
        && a.FontSize.Equals(b.FontSize)
        && a.FontFamily == b.FontFamily
        && a.NavigateUri == b.NavigateUri
        && a.Foreground.Equals(b.Foreground)
        && a.Background.Equals(b.Background)
        && a.TextDecorations == b.TextDecorations;

    private void RemoveParagraphFromDocument(FlowDocument doc, Paragraph p)
    {
        if (doc.Blocks.Remove(p)) return;

        // Inside a cell the paragraph must STAY: a cell's block list is never empty (it always holds at
        // least one paragraph), so removing it would strand the caret. Emptying it to a placeholder run
        // is the in-cell equivalent of the removal above.
        if (TopLevelBlockOf(doc, p) != null)
        {
            p.Inlines.Clear();
            p.Inlines.Add(new Run { Text = "", Parent = p });
        }
    }

    // The top-level Block in the document that contains the given paragraph: the paragraph itself if it
    // is a top-level block, otherwise the block it lives under at ANY depth.
    private Block? TopLevelBlockOf(FlowDocument doc, Paragraph p)
    {
        foreach (var block in doc.Blocks)
            if (BlockHolds(block, p)) return block;
        return null;
    }

    // Whether `b` contains paragraph `p` anywhere beneath it: b itself, a table cell's blocks at any
    // nesting depth, or an inline table hanging off a paragraph's inlines. Both callers used to look
    // exactly one level deep (a TOP-LEVEL table's cells), so a paragraph in a nested or inline table
    // read as "not in this document": TopLevelBlockOf returned null, which made Delete() skip removing
    // the top-level blocks a selection spanned, and RemoveParagraphFromDocument silently did nothing.
    // Membership must agree with CollectParagraphs on order and on which cells count, so it comes from
    // the same walk. (This was a third copy of the recursion, and the roadmap's note that "these workers
    // may have more copies" was written after one of them was found a level short.)
    private static bool BlockHolds(Block b, Paragraph p) => BlockWalk.Holds(b, p);

    private FlowDocument? GetFlowDocument(TextElement element)
    {
        object? current = element;
        while (current != null)
        {
            if (current is FlowDocument doc) return doc;
            if (current is TextElement te) current = te.Parent;
            else break;
        }
        return null;
    }

    private void ApplyStyleToParagraph(Paragraph p, int startOffset, int endOffset, Action<Run> styleAction)
    {
        if (startOffset >= endOffset) return;

        SplitRunAtOffset(p, endOffset);
        SplitRunAtOffset(p, startOffset);

        int currentIndex = 0;
        foreach (var inline in p.Inlines)
        {
            int len = InlineLen(inline);
            if (inline is Run run && currentIndex >= startOffset && currentIndex + len <= endOffset)
            {
                styleAction(run);
            }
            currentIndex += len;
        }
        CoalesceRuns(p); // toggling a style back off can make the split runs match their neighbours again
    }

    private void SplitRunAtOffset(Paragraph p, int offset)
    {
        if (offset <= 0) return;

        int currentIndex = 0;
        for (int i = 0; i < p.Inlines.Count; i++)
        {
            int len = InlineLen(p.Inlines[i]);
            if (p.Inlines[i] is Run run)
            {
                int runLen = len;
                if (offset > currentIndex && offset < currentIndex + runLen)
                {
                    int localSplit = offset - currentIndex;

                    string text1 = run.Text!.Substring(0, localSplit);
                    string text2 = run.Text!.Substring(localSplit);

                    run.Text = text1;

                    // Clone() copies every formatting field (a hand-rolled copy once dropped
                    // FontFamily/Background, losing them on the split tail).
                    var newRun = (Run)run.Clone();
                    newRun.Text = text2;
                    newRun.Parent = p;

                    p.Inlines.Insert(i + 1, newRun);
                    return;
                }
            }
            currentIndex += len;
        }
    }

    // An atomic object inline (image, table) counts as a single character position (U+FFFC), matching
    // the editor's logical offset model so split/delete/style ranges stay aligned with the rendered layout.
    private const char ObjChar = '￼';
    private static int InlineLen(Inline i) => i is Run r ? (r.Text?.Length ?? 0) : 1;

    private int GetParagraphLength(Paragraph p)
    {
        int len = 0;
        foreach (var inline in p.Inlines) len += InlineLen(inline);
        return len;
    }

    private List<Paragraph> GetAllParagraphsInOrder(FlowDocument doc)
    {
        var result = new List<Paragraph>();
        CollectParagraphs(doc.Blocks, result);
        return result;
    }

    // The control's paragraph order, which the index-based range loops must agree with on merged tables.
    // It is BlockWalk's now — this was a hand-kept mirror of RichEditor.ParagraphsInBlocks.
    private static void CollectParagraphs(IEnumerable<Block> blocks, List<Paragraph> result)
    {
        foreach (var p in BlockWalk.Paragraphs(blocks)) result.Add(p);
    }
}
