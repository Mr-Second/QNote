using System;
using Windows.Foundation;
using WinUIRichEditor.Documents;

using Microsoft.UI.Xaml;

namespace WinUIRichEditor.Controls;

// Find / replace: linear search over paragraph order with wrap-around, anchored at the selection.
// Ported from the Avalonia original (name adaptations only).
public partial class RichEditor
{
    /// <summary>Identifies the <see cref="AllowFindReplace"/> dependency property.</summary>
    public static readonly DependencyProperty AllowFindReplaceProperty = DependencyProperty.Register(
        nameof(AllowFindReplace), typeof(bool), typeof(RichEditor), new PropertyMetadata(true, OnToolbarFlagChanged));

    /// <summary>Enables find/replace (set false to disable in read-only presets).</summary>
    public bool AllowFindReplace
    {
        get => (bool)GetValue(AllowFindReplaceProperty);
        set => SetValue(AllowFindReplaceProperty, value);
    }

    /// <summary>Raised when the user presses Ctrl+F (arg <see langword="false"/>) or Ctrl+H
    /// (<see langword="true"/> = with the replace row). <see cref="RichEditorView"/> opens its built-in
    /// find bar on this; a host with its own find UI subscribes instead.</summary>
    public event EventHandler<bool>? FindRequested
    {
        add { _findRequested += value; FindUiChanged?.Invoke(this, EventArgs.Empty); }
        remove { _findRequested -= value; FindUiChanged?.Invoke(this, EventArgs.Empty); }
    }
    private EventHandler<bool>? _findRequested;
    internal void RaiseFindRequested(bool withReplace) => _findRequested?.Invoke(this, withReplace);

    // Whether anything answers FindRequested — a RichEditorView's bar, or a host's own find UI. Without one, the
    // toolbar's Find button opened nothing: it showed on a bare editor + toolbar and did nothing when pressed
    // (live check, 2026-09-19). Like Print, it now shows only when something handles it.
    internal bool HasFindUi => _findRequested != null;

    // Raised when a FindRequested handler is added or removed, so a toolbar shows or hides its Find button at once.
    // Internal and separate from StatusChanged, which hosts count.
    internal event EventHandler? FindUiChanged;

    /// <summary>The most recent find query (set by <see cref="FindNext"/>/<see cref="FindPrev"/>);
    /// F3 / Shift+F3 repeat it via <see cref="FindAgain"/>.</summary>
    public string? LastFindQuery { get; private set; }
    /// <summary>Whether the most recent find was case-sensitive.</summary>
    public bool LastFindMatchCase { get; private set; }

    // ---- highlight-all ------------------------------------------------------
    // While non-null, the render walk tints EVERY occurrence of this query (browser/VS Code find UX).
    // Set live by the find bar as the user types, and by FindNext/FindPrev; cleared when the bar closes.
    internal string? FindHighlightQuery { get; private set; }
    internal bool FindHighlightMatchCase { get; private set; }

    /// <summary>Sets (or clears, with null/empty) the query whose matches are all highlighted while a
    /// find UI is open. Independent of the caret selection; purely visual.</summary>
    public void SetFindHighlight(string? query, bool matchCase)
    {
        string? q = string.IsNullOrEmpty(query) ? null : query;
        if (q == FindHighlightQuery && matchCase == FindHighlightMatchCase) return;
        FindHighlightQuery = q;
        FindHighlightMatchCase = matchCase;
        InvalidateCanvas();
    }

    /// <summary>Clears the highlight-all overlay (call when the find UI closes).</summary>
    public void ClearFindHighlight() => SetFindHighlight(null, false);

    /// <summary>Position of the current selection among all matches of the highlight query:
    /// (current 1-based index or 0 when the selection isn't on a match, total match count).
    /// For the find bar's "n/m" counter.</summary>
    public (int current, int total) GetFindMatchPosition()
    {
        if (FindHighlightQuery is not { } q || Document == null) return (0, 0);
        var cmp = FindHighlightMatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        TextPointer s = _selStart, e = _selEnd;
        if (ComparePositions(s, e) > 0) (s, e) = (e, s);
        bool selIsMatch = s.Paragraph != null && ReferenceEquals(s.Paragraph, e.Paragraph)
            && e.Offset - s.Offset == q.Length;
        int total = 0, current = 0;
        foreach (var p in ParagraphsInBlocks(Document.Blocks))
        {
            string text = BuildPlain(p);
            int from = 0;
            while (from <= text.Length)
            {
                int idx = text.IndexOf(q, from, cmp);
                if (idx < 0) break;
                total++;
                if (selIsMatch && ReferenceEquals(p, s.Paragraph) && idx == s.Offset) current = total;
                from = idx + 1;
            }
        }
        return (current, total);
    }

    /// <summary>Repeats the last find forward (F3) or backward (Shift+F3). False when there is no
    /// previous query or no match.</summary>
    public bool FindAgain(bool backwards)
        => !string.IsNullOrEmpty(LastFindQuery)
           && (backwards ? FindPrev(LastFindQuery!, LastFindMatchCase) : FindNext(LastFindQuery!, LastFindMatchCase));

    /// <summary>Selects the next occurrence of <paramref name="query"/> after the caret, wrapping around.</summary>
    public bool FindNext(string query, bool matchCase)
    {
        if (!AllowFindReplace || Document == null || string.IsNullOrEmpty(query)) return false;
        LastFindQuery = query; LastFindMatchCase = matchCase;
        SetFindHighlight(query, matchCase);
        var paras = AllParagraphs();
        int pi = _selEnd.Paragraph != null ? paras.IndexOf(_selEnd.Paragraph) : -1;
        bool found = FindCore(query, matchCase, backwards: false, wrap: true, fromPi: pi, fromOff: _selEnd.Offset);
        RaiseStatusChanged(); // flush SelectionChanged (and, after ReplaceNext, the pending TextChanged)
        return found;
    }

    /// <summary>Selects the previous occurrence of <paramref name="query"/> before the caret, wrapping around.</summary>
    public bool FindPrev(string query, bool matchCase)
    {
        if (!AllowFindReplace || Document == null || string.IsNullOrEmpty(query)) return false;
        LastFindQuery = query; LastFindMatchCase = matchCase;
        SetFindHighlight(query, matchCase);
        var paras = AllParagraphs();
        int pi = _selStart.Paragraph != null ? paras.IndexOf(_selStart.Paragraph) : -1;
        bool found = FindCore(query, matchCase, backwards: true, wrap: true, fromPi: pi, fromOff: _selStart.Offset);
        RaiseStatusChanged();
        return found;
    }

    /// <summary>Replaces the current selection if it matches, then advances to the next match.</summary>
    public bool ReplaceNext(string query, string replacement, bool matchCase)
    {
        if (!AllowFindReplace || IsReadOnly || Document == null || string.IsNullOrEmpty(query)) return false;
        var cmp = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        bool selMatches = HasSelection && string.Equals(new TextRange(_selStart, _selEnd).GetText(), query, cmp);
        if (selMatches)
        {
            PushUndo(null);
            ReplaceSelectionText(replacement);
            // The replacement changes the paragraph's height; when it lives in a cell the enclosing
            // tables' cached row heights must go too (RelayoutToViewport doesn't clear them).
            InvalidateCaretTableMeasure();
            RelayoutToViewport();
        }
        return FindNext(query, matchCase);
    }

    /// <summary>Replaces every occurrence in the document. Returns the number of replacements made.</summary>
    public int ReplaceAll(string query, string replacement, bool matchCase)
    {
        if (!AllowFindReplace || IsReadOnly || Document == null || string.IsNullOrEmpty(query)) return 0;
        var paras = AllParagraphs();
        var cmp = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        // Nothing to replace: no undo step and no "modified" flag (both came from the checkpoint below).
        if (!paras.Exists(p => BuildPlain(p).Contains(query, cmp))) return 0;
        PushUndo(null);
        _caret = new TextPointer(paras[0], 0);
        CollapseSelectionToCaret();
        int count = 0;
        while (count <= 1_000_000)
        {
            var cur = AllParagraphs();
            int pi = _caret.Paragraph != null ? cur.IndexOf(_caret.Paragraph) : -1;
            if (!FindCore(query, matchCase, backwards: false, wrap: false, fromPi: pi, fromOff: _caret.Offset)) break;
            ReplaceSelectionText(replacement);
            count++;
        }
        // Replacements can land in any cell of any table, so the caret-ancestor invalidation isn't
        // enough here — drop every cached row height and let the next layout re-measure.
        if (count > 0) _tableRowHeights.Clear();
        RelayoutToViewport();
        RaiseStatusChanged(); // flush the pending TextChanged/SelectionChanged (FindCore doesn't raise)
        return count;
    }

    private bool FindCore(string query, bool matchCase, bool backwards, bool wrap, int fromPi, int fromOff)
    {
        var paras = AllParagraphs();
        if (paras.Count == 0) return false;
        var cmp = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        int LastMatchBefore(string text, int limit)
        {
            int best = -1, from = 0;
            while (from <= text.Length)
            {
                int idx = text.IndexOf(query, from, cmp);
                if (idx < 0 || idx >= limit) break;
                best = idx;
                from = idx + 1;
            }
            return best;
        }

        if (!backwards)
        {
            for (int pi = Math.Max(0, fromPi); pi < paras.Count; pi++)
            {
                string text = BuildPlain(paras[pi]);
                int start = pi == fromPi ? Math.Max(0, fromOff) : 0;
                if (start > text.Length) continue;
                int idx = text.IndexOf(query, start, cmp);
                if (idx >= 0) { SelectMatch(paras[pi], idx, query.Length); return true; }
            }
            if (wrap)
                for (int pi = 0; pi < paras.Count; pi++)
                {
                    int idx = BuildPlain(paras[pi]).IndexOf(query, cmp);
                    if (idx >= 0) { SelectMatch(paras[pi], idx, query.Length); return true; }
                }
        }
        else
        {
            for (int pi = Math.Min(fromPi, paras.Count - 1); pi >= 0; pi--)
            {
                string text = BuildPlain(paras[pi]);
                int idx = LastMatchBefore(text, pi == fromPi ? Math.Min(fromOff, text.Length + 1) : text.Length + 1);
                if (idx >= 0) { SelectMatch(paras[pi], idx, query.Length); return true; }
            }
            if (wrap)
                for (int pi = paras.Count - 1; pi >= 0; pi--)
                {
                    int idx = LastMatchBefore(BuildPlain(paras[pi]), int.MaxValue);
                    if (idx >= 0) { SelectMatch(paras[pi], idx, query.Length); return true; }
                }
        }
        return false;
    }

    private void SelectMatch(Paragraph p, int start, int length)
    {
        _selStart = new TextPointer(p, start);
        _selEnd = new TextPointer(p, start + length);
        _caret = new TextPointer(p, start + length);
        RestartBlink();
        SyncIme();
        ScrollCaretIntoView();
        InvalidateCanvas();
    }

    private void ReplaceSelectionText(string replacement)
    {
        DeleteSelection();
        if (!string.IsNullOrEmpty(replacement) && _caret.Paragraph != null)
        {
            TryInsertTextCore(_caret.Paragraph, replacement, _caret.Offset);
            _caret = new TextPointer(_caret.Paragraph, _caret.Offset + replacement.Length);
        }
        CollapseSelectionToCaret();
    }

    // Scrolls the host so the caret is within the viewport — vertically always, horizontally when the
    // (zoomed/paged) canvas is wider than the viewport (used by find and caret navigation).
    private void ScrollCaretIntoView()
    {
        if (CaretToDocPoint(_caret) is not { } cp) return;
        // Paged mode stacks pages with gaps; DocToView also folds in the zoom scale.
        var v = DocToView(new Point(cp.X, cp.Y));
        const double margin = 24;

        double top = v.Y, bottom = v.Y + cp.Height * EffectiveZoom;
        double viewTop = _scroll.VerticalOffset;
        double viewBottom = viewTop + _scroll.ViewportHeight;
        double? newV = null;
        if (top < viewTop + margin) newV = Math.Max(0, top - margin);
        else if (bottom > viewBottom - margin) newV = bottom - _scroll.ViewportHeight + margin;

        // Horizontal tracking: at 200% zoom (or wide paper) the canvas outgrows the viewport and the
        // caret could sit off-screen to the side; vertical-only scrolling never brought it back.
        double? newH = null;
        if (_scroll.ScrollableWidth > 0)
        {
            double left = v.X, right = v.X + 2;
            double viewLeft = _scroll.HorizontalOffset;
            double viewRight = viewLeft + _scroll.ViewportWidth;
            if (left < viewLeft + margin) newH = Math.Max(0, left - margin);
            else if (right > viewRight - margin) newH = right - _scroll.ViewportWidth + margin;
        }

        if (newV != null || newH != null) _scroll.ChangeView(newH, newV, null);
    }
}
