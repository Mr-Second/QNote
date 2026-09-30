using System;
using Windows.UI;
using Windows.UI.Text;
using Microsoft.UI.Xaml;
using WinUIRichEditor.Documents;

namespace WinUIRichEditor.Controls;

// Caret-format + document-status API for toolbar/status-bar reflection. StatusChanged fires after
// edits, formatting changes, and caret/selection moves so chrome can refresh.
public partial class RichEditor
{
    /// <summary>Raised after the caret moves, the selection changes, or the document is edited, so a
    /// toolbar/status bar can refresh its state. Coarse signal — prefer <see cref="TextChanged"/> or
    /// <see cref="SelectionChanged"/> for new code.</summary>
    public event EventHandler? StatusChanged;

    /// <summary>Raised after the document's text, structure, or formatting is modified.</summary>
    public event EventHandler? TextChanged;

    /// <summary>Raised when the caret position or the selected range changes.</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>Raised when the <see cref="Document"/> is replaced with a different instance.</summary>
    public event EventHandler? DocumentChanged;

    // Set by any mutation (PushUndo) or a wholesale Document swap; cleared when flushed as TextChanged.
    private bool _textChangedPending;
    private void MarkTextChanged()
    {
        _textChangedPending = true;
        _paraIndexMap = null;    // a mutation may reorder/add/remove paragraphs (see ComparePositions)
        InvalidateBlockLayout(); // block heights/positions may change (see EnsureBlockLayout)
        SetModified(true);
    }

    /// <summary>True when the document has been modified since it was loaded (or since
    /// <see cref="MarkSaved"/>). Undo/redo count as modifications — the flag is a "needs saving" hint,
    /// not a content diff against the saved state.</summary>
    public bool IsModified { get; private set; }

    /// <summary>Raised when <see cref="IsModified"/> changes — after the edit that changed it is complete,
    /// so a handler sees the edited document.</summary>
    public event EventHandler? IsModifiedChanged;

    /// <summary>Clears the modified flag; call after persisting the document.</summary>
    public void MarkSaved() { IsModified = false; ReportModified(); }

    // An edit sets the flag at its START — PushUndo checkpoints before mutating — so raising the event there
    // ran the host's handler in the middle of the command, looking at the document from before the edit
    // (measured 2026-09-14: typing "X" after "ab", the handler read "ab"). The flag flips at once; the event
    // waits for the flush that raises TextChanged (RaiseStatusChanged). MarkSaved, a host call, reports at once.
    private void SetModified(bool value) => IsModified = value;

    // The IsModified value handlers were last told, so a flag that flips and flips back unseen raises nothing.
    private bool _modifiedReported;
    // Set while LoadDocument swaps the document in: the swap marks it modified (a raw Document assignment is an
    // edit) and flushes, and the load clears it again at once — reporting in between announced "modified" at
    // every open (measured: LoadHtml into a clean editor raised IsModifiedChanged twice, true then false).
    private bool _loadingDocument;
    private void ReportModified()
    {
        if (_loadingDocument || _modifiedReported == IsModified) return;
        _modifiedReported = IsModified;
        IsModifiedChanged?.Invoke(this, EventArgs.Empty);
    }

    // Last-seen selection, so SelectionChanged fires only on a real change (not on every repaint). It records
    // what the selection IS, not only where its endpoints are: selecting an image or a table as an object leaves
    // the caret where it was, and F5 can mark exactly the range already selected — or, in an empty cell, the
    // caret itself. The endpoints did not move, so SelectionChanged never came while HasBlockSelection, Copy
    // and Delete all changed meaning (measured 2026-09-14).
    private (Paragraph? cp, int co, Paragraph? ss, int so, Paragraph? se, int eo, object? obj, bool cell) _selSnapshot;
    private bool SelectionMovedSinceLastSnapshot()
    {
        object? obj = (object?)_selectedInlineTable?.it ?? (object?)_selectedInline?.img ?? _selectedBlock;
        var now = (_caret.Paragraph, _caret.Offset, _selStart.Paragraph, _selStart.Offset,
                   _selEnd.Paragraph, _selEnd.Offset, obj, MarkedCell() != null);
        if (now == _selSnapshot) return false;
        _selSnapshot = now;
        return true;
    }

    // The notification hub. Always fires StatusChanged; additionally flushes a pending TextChanged and,
    // when the caret/selection actually moved, SelectionChanged. Callers reach this off the render stack
    // (input handlers, AfterEdit), so handlers see the already-mutated document.
    private void RaiseStatusChanged()
    {
        StatusChanged?.Invoke(this, EventArgs.Empty);
        if (_textChangedPending)
        {
            _textChangedPending = false;
            SweepRemovedImages();
            TextChanged?.Invoke(this, EventArgs.Empty);
            NotifyAutomation(Microsoft.UI.Xaml.Automation.Peers.AutomationEvents.TextPatternOnTextChanged);
        }
        ReportModified();
        if (SelectionMovedSinceLastSnapshot())
        {
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            NotifyAutomation(Microsoft.UI.Xaml.Automation.Peers.AutomationEvents.TextPatternOnTextSelectionChanged);
        }
    }

    // Tells a screen reader that the text or the selection moved.
    //
    // RichEditorAutomationPeer implements ITextProvider — Narrator/NVDA can ASK for the text, the caret
    // and the selection — but nothing ever told them to ask again, so the peer only ever answered
    // at the moments UIA happened to poll. Announcing a typed character or an arrow-key caret move is
    // exactly what these two events are for, and they are the pair every editable-text control raises.
    //
    // ListenerExists is not an optimization detail here: without an assistive client attached, creating
    // the peer at all is pure cost on a path that runs after every keystroke.
    private void NotifyAutomation(Microsoft.UI.Xaml.Automation.Peers.AutomationEvents which)
    {
        if (!Microsoft.UI.Xaml.Automation.Peers.AutomationPeer.ListenerExists(which)) return;
        try { (_automationPeer ?? OnCreateAutomationPeer()).RaiseAutomationEvent(which); }
        catch (Exception ex) { RichEditorDiagnostics.Report(ex); }
    }

    // Fired from the Document DP callback when the whole document instance is replaced.
    private void RaiseDocumentChanged() => DocumentChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Snapshot of the formatting at the caret position, for toolbar state reflection.</summary>
    public readonly record struct CaretFormat(bool Bold, bool Italic, bool Underline, bool Strike,
        double FontSize, string? FontFamily, TextAlignment Align, ListKind List, int Heading,
        Color? Foreground = null, Color? Background = null, bool Quote = false, double LineSpacing = double.NaN,
        ListMarkerStyle ListMarker = ListMarkerStyle.Default);

    // The Run holding the character at logical offset within p — at or past the end, the paragraph's last
    // inline if that is a run — or null when the character is an inline object (an image has no character
    // format). It used to fall back to the paragraph's LAST run whenever the offset landed on an object,
    // so next to an image the caret reported, and CurrentLinkUri returned, a run far away.
    private static Run? RunAtOffset(Paragraph p, int offset)
    {
        int pos = 0;
        Inline? last = null;
        foreach (var inl in p.Inlines)
        {
            int len = InlineLen(inl);
            last = inl;
            if (offset >= pos && offset < pos + len) return inl as Run;
            pos += len;
        }
        return offset >= pos ? last as Run : null;
    }

    // Where text typed at logical offset k goes, and whose character format it takes — the ONE rule for
    // insertion (TryInsertTextCore) and for the caret report (CaretFormatRun), so the toolbar shows what
    // typing will produce. Word's rule: the nearest text BEFORE the caret, skipping inline objects; with
    // none before, the nearest after. Before this (2026-09-12) insertion and report each had their own
    // rule and disagreed next to images — measured at 4 caret positions, e.g. "[img] bold italic" with
    // the caret before the image: the toolbar showed the italic of the paragraph's last run, typing wrote
    // plain text.
    //   Run  — the format source; null when the paragraph has no run at all.
    //   Into — the typed text goes INTO Run at this local index; -1 = into a new run cloned from Run,
    //          inserted at inline index At.
    //   Link — the typed text continues Run's hyperlink. Only strictly inside a link (the characters on
    //          both sides of the caret carry it): typing at a link's end or start writes plain text, as in
    //          Word. It used to extend the link.
    private static (Run? Run, int Into, int At, bool Link) TypingSource(Paragraph p, int k)
    {
        var inls = p.Inlines;
        int holdsPrev = -1, holdsPrevStart = 0; // the inline holding character k-1
        int pos = 0;
        for (int i = 0; i < inls.Count; i++)
        {
            int len = InlineLen(inls[i]);
            if (pos < k && k <= pos + len) { holdsPrev = i; holdsPrevStart = pos; break; }
            pos += len;
        }
        int at = holdsPrev + 1; // a new run goes right after the character before the caret (0 at the start)

        // The run holding character k-1: the typed text joins it.
        if (holdsPrev >= 0 && inls[holdsPrev] is Run t)
        {
            int end = holdsPrevStart + InlineLen(t);
            bool link = !string.IsNullOrEmpty(t.NavigateUri)
                && (k < end || NextTextRun(inls, holdsPrev)?.NavigateUri == t.NavigateUri);
            bool into = link || string.IsNullOrEmpty(t.NavigateUri);
            return (t, into ? k - holdsPrevStart : -1, at, link);
        }
        // Character k-1 is an object (or there is none): the nearest run before, skipping objects...
        for (int i = holdsPrev - 1; i >= 0; i--)
            if (inls[i] is Run b) return (b, -1, at, false);
        // ...else the nearest after. Touching the caret (no object between), the text goes into it.
        for (int i = at; i < inls.Count; i++)
            if (inls[i] is Run a)
            {
                bool touching = i == at;
                return (a, touching && string.IsNullOrEmpty(a.NavigateUri) ? 0 : -1, at, false);
            }
        return (null, -1, at, false);
    }

    // The first run after inline index i that holds a character (empty runs hold none), or null when an
    // object or the paragraph's end comes first.
    private static Run? NextTextRun(System.Collections.Generic.IList<Inline> inls, int i)
    {
        for (int j = i + 1; j < inls.Count; j++)
        {
            if (inls[j] is not Run r) return null;
            if (!string.IsNullOrEmpty(r.Text)) return r;
        }
        return null;
    }

    // The run whose character format the caret shows — TypingSource's, so the toolbar shows what typing
    // will produce — with any pending caret format (a toggle at an empty position) previewed on a clone so
    // the document stays untouched. Shared with the character toggles, which decide on/off from exactly
    // what the toolbar is showing.
    private Run? CaretFormatRun()
    {
        var p = _caret.Paragraph;
        if (p == null) return null;
        var (run, _, _, link) = TypingSource(p, _caret.Offset);
        bool dropLink = run != null && !link && !string.IsNullOrEmpty(run.NavigateUri);
        bool pending = _pendingCaretStyles is { Count: > 0 };
        if (!dropLink && !pending) return run;
        // A clone: parented to the caret paragraph, so a style reading its paragraph (ClearFormatting's
        // heading-aware size) previews what it will do to the typed run.
        var probe = run != null ? (Run)run.Clone() : new Run();
        probe.Parent = p;
        if (dropLink) probe.NavigateUri = null;
        if (pending) foreach (var a in _pendingCaretStyles!) a(probe);
        return probe;
    }

    /// <summary>Returns the formatting snapshot at the current caret position for toolbar state display.</summary>
    public CaretFormat GetCaretFormat()
    {
        var p = _caret.Paragraph;
        var run = CaretFormatRun();
        bool heading = p is { HeadingLevel: >= 1 and <= 6 };
        double headingSize = heading ? HeadingFontSize(p!.HeadingLevel) : 0;
        bool strike = run != null && run.TextDecorations.HasFlag(TextDecorationFlags.Strikethrough);
        // Bold, underline and colour are reported as DRAWN (DrawnBold/DrawnUnderline/DrawnForeground, the
        // renderer's rules): a link is drawn underlined and blue whatever the run says. With no run at all,
        // text typed into a heading takes the heading's preset (TryInsertTextCore), so that is the report.
        return new CaretFormat(
            run != null ? DrawnBold(run) : heading,
            run?.FontStyle == FontStyle.Italic,
            run != null && DrawnUnderline(run),
            strike,
            // The size the text is DRAWN at, by the renderer's own rule (DrawnRunSize): the toolbar shows it
            // and IncreaseFontSize steps from it. Reporting the run's raw size showed 10 for unset text drawn
            // at the host's DefaultFontSize (14) and for an H1 drawn at 20 — and "larger" then SHRANK both,
            // to 10.5. (DefaultFontSize's contract also names "the toolbar's displayed fallback size".)
            run != null ? DrawnRunSize(run, DefaultFontSize)
                        : heading ? headingSize : DefaultFontSize,
            run?.FontFamily,
            p?.TextAlignment ?? TextAlignment.Left,
            p?.ListType ?? ListKind.None,
            p?.HeadingLevel ?? 0,
            run != null ? DrawnForeground(run) : null,
            run?.Background,
            p?.IsQuote ?? false,
            p?.LineSpacing ?? double.NaN,
            p?.ListMarker ?? ListMarkerStyle.Default);
    }

    // ---- automation text bridge (TextPattern in RichEditorAutomationPeer) --------------------------
    // The document as one flat string (paragraphs joined with '\n', matching GetPlainText's logical
    // form before CRLF normalization) plus global-offset <-> (paragraph, local offset) conversion, so
    // the automation ITextRangeProvider can work in simple integer ranges.

    internal string AutomationGetText()
    {
        var sb = new System.Text.StringBuilder();
        bool first = true;
        foreach (var p in AllParagraphs())
        {
            if (!first) sb.Append('\n');
            first = false;
            sb.Append(BuildPlain(p));
        }
        return sb.ToString();
    }

    // The current selection (collapsed caret when none) as global offsets into AutomationGetText().
    internal (int start, int end) AutomationGetSelection()
    {
        int s = AutomationOffsetOf(_selStart), e = AutomationOffsetOf(_selEnd);
        if (s < 0 || e < 0) { int c = Math.Max(0, AutomationOffsetOf(_caret)); return (c, c); }
        return s <= e ? (s, e) : (e, s);
    }

    private int AutomationOffsetOf(TextPointer tp)
    {
        if (tp.Paragraph == null) return -1;
        int pos = 0;
        foreach (var p in AllParagraphs())
        {
            int len = GetParagraphLength(p);
            if (ReferenceEquals(p, tp.Paragraph)) return pos + Math.Clamp(tp.Offset, 0, len);
            pos += len + 1; // '\n'
        }
        return -1;
    }

    private TextPointer AutomationPointerAt(int offset)
    {
        int pos = 0;
        Paragraph? last = null;
        foreach (var p in AllParagraphs())
        {
            int len = GetParagraphLength(p);
            if (offset <= pos + len) return new TextPointer(p, Math.Clamp(offset - pos, 0, len));
            pos += len + 1;
            last = p;
        }
        return new TextPointer(last, last != null ? GetParagraphLength(last) : 0);
    }

    // Selects [start, end) (global offsets) and scrolls the caret end into view (assistive tech drives this).
    internal void AutomationSelect(int start, int end)
    {
        if (Document == null) return;
        if (end < start) (start, end) = (end, start);
        _selStart = AutomationPointerAt(start);
        _selEnd = AutomationPointerAt(end);
        _caret = new TextPointer(_selEnd.Paragraph, _selEnd.Offset);
        ScrollCaretIntoView();
        InvalidateCanvas();
        RaiseStatusChanged();
    }

    // ---- automation geometry (TextPattern bounding rects / point hit) ------------------------------

    // The document-space origin of a paragraph's text layout — top-level or nested in (inline-)table
    // cells — derived from the caret geometry at offset 0: doc caret − in-layout caret = layout origin.
    // Reuses CaretToDocPoint's full descent instead of duplicating the cell walk.
    private (double x, double y)? ParagraphDocOrigin(Paragraph p)
    {
        if (CaretToDocPoint(new TextPointer(p, 0)) is not { } c) return null;
        var layout = BuildTextLayout(p, ParagraphWrapWidth(p));
        var (cx, cy, _, _, _) = CaretInLayout(layout, p, 0, false);
        return (c.X - cx, c.Y - cy);
    }

    /// <summary>Physical-screen bounding rectangles (flattened L,T,W,H quads) for a global-offset range
    /// of the automation text — one rect per rendered line fragment. Capped so a document-wide range
    /// doesn't build layouts for thousands of paragraphs.</summary>
    internal double[] AutomationBoundingRects(int start, int end)
    {
        if (Document == null || end <= start) return System.Array.Empty<double>();
        var result = new System.Collections.Generic.List<double>();
        const int maxRects = 256;
        int pos = 0;
        try
        {
            foreach (var p in AllParagraphs())
            {
                int len = GetParagraphLength(p);
                int ls = System.Math.Max(start - pos, 0), le = System.Math.Min(end - pos, len);
                if (le > ls && ParagraphDocOrigin(p) is { } o)
                {
                    var layout = BuildTextLayout(p, ParagraphWrapWidth(p));
                    foreach (var r in layout.GetCharacterRegions(ls, le - ls))
                    {
                        var lb = r.LayoutBounds;
                        var sr = DocRectToScreen(new Windows.Foundation.Rect(o.x + lb.X, o.y + lb.Y, lb.Width, lb.Height));
                        result.Add(sr.X); result.Add(sr.Y); result.Add(sr.Width); result.Add(sr.Height);
                        if (result.Count >= maxRects * 4) return result.ToArray();
                    }
                }
                pos += len + 1; // '\n'
                if (pos > end) break;
            }
        }
        // partial rects are still useful to assistive tech
        catch (Exception ex) { RichEditorDiagnostics.Report(ex); }
        return result.ToArray();
    }

    /// <summary>The automation-text offset nearest a physical screen point (RangeFromPoint).</summary>
    internal int AutomationOffsetAtScreenPoint(Windows.Foundation.Point screen)
    {
        try
        {
            if (ScreenPointToView(screen) is { } view
                && GetPositionFromPoint(ViewToDoc(view)) is { Paragraph: not null } tp)
                return System.Math.Max(0, AutomationOffsetOf(tp));
        }
        catch (Exception ex) { RichEditorDiagnostics.Report(ex); }
        return 0;
    }

    private static bool IsSepChar(char ch) => ch == ' ' || ch == '\n' || ch == '\t' || ch == '\r';

    // Per-paragraph character/word/hard-line statistics, cached by content signature. GetStatus runs on
    // every StatusChanged (i.e. per keystroke via the status bar), and rescanning every character made
    // typing O(document chars); with this cache only the edited paragraph rescans. Words never span
    // paragraphs (the separator '\n' breaks them), so per-paragraph counts sum exactly.
    // Weak-keyed for the reason spelled out on _heightCache: these entries must not outlive the
    // paragraph they describe.
    private sealed class StatsEntry(long sig, int chars, int words, int breaks)
    {
        public readonly long Sig = sig; public readonly int Chars = chars;
        public readonly int Words = words; public readonly int Breaks = breaks;
    }

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Paragraph, StatsEntry> _statsCache = new();

    private (int chars, int words, int breaks) ParagraphStats(Paragraph p)
    {
        long sig = ParagraphSig(p);
        if (_statsCache.TryGetValue(p, out var hit) && hit.Sig == sig)
            return (hit.Chars, hit.Words, hit.Breaks);

        int chars = 0, words = 0, breaks = 0;
        bool inWord = false;
        void Consume(char ch)
        {
            if (ch == '\n') { breaks++; inWord = false; }
            else { chars++; if (IsSepChar(ch)) inWord = false; else { if (!inWord) words++; inWord = true; } }
        }
        foreach (var inline in p.Inlines)
        {
            if (inline is Run r && r.Text is { } t) { foreach (char ch in t) Consume(ch); }
            else if (inline is not Run) Consume(ObjChar);
        }

        _statsCache.AddOrUpdate(p, new StatsEntry(sig, chars, words, breaks));
        return (chars, words, breaks);
    }

    /// <summary>Returns document statistics: total character count, word count, and the caret's
    /// 1-based (line, column) position. Inline images count as one character.</summary>
    public (int chars, int words, int line, int col) GetStatus()
    {
        var doc = Document;
        if (doc == null) return (0, 0, 1, 1);

        int chars = 0, words = 0;
        int capLine = 1, capCol = 1;
        bool captured = false, firstPara = true;
        int lineAtParaStart = 1; // the global line number of the current paragraph's first hard line

        // Lazy enumeration — this runs on every StatusChanged (per keystroke via the status bar), and
        // AllParagraphs() materialized a fresh List of the whole document each time.
        foreach (var p in ParagraphsInBlocks(doc.Blocks))
        {
            if (!firstPara) lineAtParaStart++; // the '\n' separator between paragraphs
            firstPara = false;

            var st = ParagraphStats(p);

            // Only the CARET paragraph is scanned character-by-character (for line/col); all others
            // contribute their cached totals.
            if (!captured && ReferenceEquals(p, _caret.Paragraph))
            {
                int caretOff = Math.Clamp(_caret.Offset, 0, GetParagraphLength(p));
                int local = 0, lineInPara = 0, colChars = 0;
                bool done = false;
                void Scan(char ch)
                {
                    if (local == caretOff) { done = true; return; }
                    if (ch == '\n') { lineInPara++; colChars = 0; } else colChars++;
                    local++;
                }
                foreach (var inline in p.Inlines)
                {
                    if (inline is Run r && r.Text is { } t) { foreach (char ch in t) { Scan(ch); if (done) break; } }
                    else if (inline is not Run) Scan(ObjChar);
                    if (done) break;
                }
                capLine = lineAtParaStart + lineInPara;
                capCol = colChars + 1;
                captured = true;
            }

            chars += st.chars;
            words += st.words;
            lineAtParaStart += st.breaks;
        }

        return (chars, words, captured ? capLine : 1, captured ? capCol : 1);
    }
}
