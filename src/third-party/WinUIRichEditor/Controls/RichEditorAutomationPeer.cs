using System;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Automation.Text;

namespace WinUIRichEditor.Controls;

// Accessibility peer: exposes the editor to screen readers as an editable text control. IValueProvider
// carries the whole plain text (read + wholesale replace); ITextProvider adds caret/selection tracking
// and unit-wise range navigation (character/word/line/paragraph/document) over the flat text the
// editor's automation bridge exposes, so assistive tech can follow the caret instead of re-reading
// everything. Ranges are plain [start, end) offsets; GetBoundingRectangles/RangeFromPoint map them
// to/from physical screen coordinates (formatting attributes are not surfaced — GetAttributeValue null).
internal sealed partial class RichEditorAutomationPeer : FrameworkElementAutomationPeer, IValueProvider, ITextProvider
{
    private readonly RichEditor _owner;

    public RichEditorAutomationPeer(RichEditor owner) : base(owner) => _owner = owner;

    protected override object GetPatternCore(PatternInterface patternInterface)
        => patternInterface is PatternInterface.Value or PatternInterface.Text ? this : base.GetPatternCore(patternInterface);

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Edit;

    protected override string GetClassNameCore() => nameof(RichEditor);

    protected override string GetNameCore()
    {
        var name = base.GetNameCore();
        return string.IsNullOrEmpty(name) ? "Rich text editor" : name;
    }

    protected override bool IsContentElementCore() => true;
    protected override bool IsControlElementCore() => true;

    // IValueProvider
    public bool IsReadOnly => _owner.IsReadOnly;

    public string Value => _owner.GetPlainText();

    public void SetValue(string value)
    {
        if (_owner.IsReadOnly) return;
        // One <p> per line so a multi-line value keeps its breaks; each line is HTML-encoded so '<'/'&'
        // stay literal. Mirrors GetPlainText (joins paragraphs with '\n'), so the value round-trips.
        var sb = new System.Text.StringBuilder();
        foreach (var line in (value ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            sb.Append("<p>").Append(System.Net.WebUtility.HtmlEncode(line)).Append("</p>");
        _owner.LoadHtml(sb.ToString());
    }

    // Raised by the owner when IsReadOnly toggles, so assistive tech learns the control's editability
    // changed. Per-keystroke Value notifications are intentionally not raised (with only the Value
    // pattern they would make screen readers re-announce the whole document on every keystroke).
    public void NotifyReadOnlyChanged(bool oldValue, bool newValue)
        => RaisePropertyChangedEvent(ValuePatternIdentifiers.IsReadOnlyProperty, oldValue, newValue);

    // ---- ITextProvider ------------------------------------------------------
    public ITextRangeProvider DocumentRange => new TextRangeProvider(this, 0, _owner.AutomationGetText().Length);

    public SupportedTextSelection SupportedTextSelection => SupportedTextSelection.Single;

    public ITextRangeProvider[] GetSelection()
    {
        var (s, e) = _owner.AutomationGetSelection();
        return new ITextRangeProvider[] { new TextRangeProvider(this, s, e) };
    }

    public ITextRangeProvider[] GetVisibleRanges() => new[] { DocumentRange };

    public ITextRangeProvider RangeFromChild(IRawElementProviderSimple childElement) => DocumentRange;

    public ITextRangeProvider RangeFromPoint(Windows.Foundation.Point screenLocation)
    {
        int off = _owner.AutomationOffsetAtScreenPoint(screenLocation);
        return new TextRangeProvider(this, off, off);
    }

    // A [start, end) span over the editor's flat automation text. The text is re-fetched per call so a
    // stale range degrades gracefully (offsets clamp) instead of throwing after edits.
    private sealed partial class TextRangeProvider : ITextRangeProvider
    {
        private readonly RichEditorAutomationPeer _peer;
        private int _start, _end;

        public TextRangeProvider(RichEditorAutomationPeer peer, int start, int end)
        {
            _peer = peer;
            _start = Math.Max(0, Math.Min(start, end));
            _end = Math.Max(_start, Math.Max(start, end));
        }

        private string Text => _peer._owner.AutomationGetText();

        private void Clamp(string text)
        {
            _start = Math.Clamp(_start, 0, text.Length);
            _end = Math.Clamp(_end, _start, text.Length);
        }

        public ITextRangeProvider Clone() => new TextRangeProvider(_peer, _start, _end);

        public bool Compare(ITextRangeProvider textRangeProvider)
            => textRangeProvider is TextRangeProvider o && o._start == _start && o._end == _end;

        public int CompareEndpoints(TextPatternRangeEndpoint endpoint, ITextRangeProvider targetRange, TextPatternRangeEndpoint targetEndpoint)
        {
            int mine = endpoint == TextPatternRangeEndpoint.Start ? _start : _end;
            int theirs = targetRange is TextRangeProvider o
                ? (targetEndpoint == TextPatternRangeEndpoint.Start ? o._start : o._end)
                : 0;
            return mine.CompareTo(theirs);
        }

        public void ExpandToEnclosingUnit(TextUnit unit)
        {
            string text = Text;
            Clamp(text);
            switch (unit)
            {
                case TextUnit.Character:
                    _end = Math.Min(text.Length, _start + 1);
                    break;
                case TextUnit.Word:
                    _start = WordStart(text, _start);
                    _end = WordEnd(text, Math.Max(_start, Math.Min(_end, text.Length)));
                    break;
                case TextUnit.Line:
                case TextUnit.Paragraph:
                case TextUnit.Format:
                    _start = LineStart(text, _start);
                    _end = LineEnd(text, Math.Max(_start, _end));
                    break;
                default: // Page / Document
                    _start = 0;
                    _end = text.Length;
                    break;
            }
        }

        public ITextRangeProvider? FindAttribute(int attribute, object value, bool backward) => null;

        public ITextRangeProvider? FindText(string text, bool backward, bool ignoreCase)
        {
            string hay = Text;
            Clamp(hay);
            string span = hay.Substring(_start, _end - _start);
            var cmp = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            int idx = backward ? span.LastIndexOf(text, cmp) : span.IndexOf(text, cmp);
            return idx < 0 ? null : new TextRangeProvider(_peer, _start + idx, _start + idx + text.Length);
        }

        public object? GetAttributeValue(int attribute) => null;

        // Physical-screen line rects for the range — lets Magnifier/Narrator visually track the text.
        public void GetBoundingRectangles(out double[] returnValue)
        {
            Clamp(Text);
            returnValue = _peer._owner.AutomationBoundingRects(_start, _end);
        }

        public IRawElementProviderSimple[] GetChildren() => Array.Empty<IRawElementProviderSimple>();

        public IRawElementProviderSimple GetEnclosingElement() => _peer.ProviderFromPeer(_peer);

        public string GetText(int maxLength)
        {
            string text = Text;
            Clamp(text);
            int len = _end - _start;
            if (maxLength >= 0 && maxLength < len) len = maxLength;
            return text.Substring(_start, len);
        }

        public int Move(TextUnit unit, int count)
        {
            if (count == 0) return 0;
            string text = Text;
            Clamp(text);
            // Standard approximation: collapse to start, step unit boundaries, then span one unit.
            int pos = _start, moved = 0, dir = Math.Sign(count);
            for (int i = 0; i < Math.Abs(count); i++)
            {
                int next = StepUnit(text, pos, unit, dir);
                if (next == pos) break;
                pos = next;
                moved += dir;
            }
            if (moved != 0)
            {
                _start = _end = pos;
                ExpandToEnclosingUnit(unit);
            }
            return moved;
        }

        public int MoveEndpointByUnit(TextPatternRangeEndpoint endpoint, TextUnit unit, int count)
        {
            if (count == 0) return 0;
            string text = Text;
            Clamp(text);
            int pos = endpoint == TextPatternRangeEndpoint.Start ? _start : _end;
            int moved = 0, dir = Math.Sign(count);
            for (int i = 0; i < Math.Abs(count); i++)
            {
                int next = StepUnit(text, pos, unit, dir);
                if (next == pos) break;
                pos = next;
                moved += dir;
            }
            if (endpoint == TextPatternRangeEndpoint.Start) { _start = pos; if (_end < _start) _end = _start; }
            else { _end = pos; if (_start > _end) _start = _end; }
            return moved;
        }

        public void MoveEndpointByRange(TextPatternRangeEndpoint endpoint, ITextRangeProvider targetRange, TextPatternRangeEndpoint targetEndpoint)
        {
            if (targetRange is not TextRangeProvider o) return;
            int v = targetEndpoint == TextPatternRangeEndpoint.Start ? o._start : o._end;
            if (endpoint == TextPatternRangeEndpoint.Start) { _start = v; if (_end < _start) _end = _start; }
            else { _end = v; if (_start > _end) _start = _end; }
        }

        public void Select() => _peer._owner.AutomationSelect(_start, _end);

        public void AddToSelection() { /* single-selection control */ }
        public void RemoveFromSelection() { /* single-selection control */ }

        public void ScrollIntoView(bool alignToTop) => _peer._owner.AutomationSelect(_start, _start);

        // One unit-boundary step from pos in the given direction; returns pos when already at the edge.
        private static int StepUnit(string text, int pos, TextUnit unit, int dir)
        {
            switch (unit)
            {
                case TextUnit.Character:
                    return Math.Clamp(pos + dir, 0, text.Length);
                case TextUnit.Word:
                    return dir > 0 ? NextWordStart(text, pos) : PrevWordStart(text, pos);
                default: // Line / Paragraph / Format / Page / Document — '\n' boundaries or edges
                    if (unit is TextUnit.Page or TextUnit.Document) return dir > 0 ? text.Length : 0;
                    return dir > 0 ? NextLineStart(text, pos) : PrevLineStart(text, pos);
            }
        }

        private static bool IsWordChar(char ch) => !char.IsWhiteSpace(ch);

        private static int WordStart(string t, int i)
        {
            i = Math.Clamp(i, 0, t.Length);
            while (i > 0 && IsWordChar(t[i - 1])) i--;
            return i;
        }

        private static int WordEnd(string t, int i)
        {
            i = Math.Clamp(i, 0, t.Length);
            while (i < t.Length && IsWordChar(t[i])) i++;
            while (i < t.Length && char.IsWhiteSpace(t[i]) && t[i] != '\n') i++; // trailing gap belongs to the word (UIA)
            return i;
        }

        private static int NextWordStart(string t, int i)
        {
            i = Math.Clamp(i, 0, t.Length);
            while (i < t.Length && IsWordChar(t[i])) i++;
            while (i < t.Length && !IsWordChar(t[i])) i++;
            return i;
        }

        private static int PrevWordStart(string t, int i)
        {
            i = Math.Clamp(i, 0, t.Length);
            while (i > 0 && !IsWordChar(t[i - 1])) i--;
            while (i > 0 && IsWordChar(t[i - 1])) i--;
            return i;
        }

        private static int LineStart(string t, int i)
        {
            i = Math.Clamp(i, 0, t.Length);
            int nl = i > 0 ? t.LastIndexOf('\n', i - 1) : -1;
            return nl + 1;
        }

        private static int LineEnd(string t, int i)
        {
            i = Math.Clamp(i, 0, t.Length);
            int nl = t.IndexOf('\n', i);
            return nl < 0 ? t.Length : nl + 1; // include the break, per UIA line semantics
        }

        private static int NextLineStart(string t, int i)
        {
            i = Math.Clamp(i, 0, t.Length);
            int nl = t.IndexOf('\n', i);
            return nl < 0 ? t.Length : nl + 1;
        }

        private static int PrevLineStart(string t, int i)
        {
            i = Math.Clamp(i, 0, t.Length);
            int cur = LineStart(t, i);
            if (i > cur) return cur;           // mid-line: to this line's start
            return cur == 0 ? 0 : LineStart(t, cur - 1); // at a line start: to the previous line's
        }
    }
}
