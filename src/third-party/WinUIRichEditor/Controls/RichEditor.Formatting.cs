using System;
using System.Collections.Generic;
using System.Linq;
using Windows.UI;
using Windows.UI.Text;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using WinUIRichEditor.Documents;

namespace WinUIRichEditor.Controls;

// Character/paragraph formatting commands (toolbar surface), list toggling with hard-line splitting.
// Ported from the Avalonia original with type swaps (IBrush→Color, FontWeight struct, TextDecorationFlags).
public partial class RichEditor
{
    // Word-style pending format: a toggle made at an empty caret applies to the next typed text.
    private List<Action<Run>>? _pendingCaretStyles;

    // ---- format painter ---------------------------------------------------
    // Character-format snapshot captured from the source selection; non-null while armed.
    private (FontWeight w, FontStyle st, TextDecorationFlags dec, double size, string? family, Color? fg, Color? bg)? _painterFmt;

    /// <summary>True while the format painter is armed (the next selection receives the captured format).</summary>
    public bool IsFormatPainterActive => _painterFmt != null;

    /// <summary>Captures character formatting from the caret/selection and arms the format painter: the
    /// next selection receives that formatting. Calling again while armed cancels (toggle).</summary>
    public void StartFormatPainter()
    {
        if (IsReadOnly) return;
        if (_painterFmt != null) { CancelFormatPainter(); return; }
        var p = HasSelection ? _selStart.Paragraph : _caret.Paragraph;
        if (p == null) return;
        int off = HasSelection ? _selStart.Offset : _caret.Offset;
        var src = RunAtOffset(p, off) ?? RunAtOffset(p, Math.Max(0, off - 1)) ?? TypingSource(p, off).Run;
        if (src == null) return;
        _painterFmt = (src.FontWeight, src.FontStyle, src.TextDecorations, src.FontSize, src.FontFamily, src.Foreground, src.Background);
        RaiseStatusChanged();
    }

    /// <summary>Disarms the format painter without applying.</summary>
    public void CancelFormatPainter()
    {
        if (_painterFmt == null) return;
        _painterFmt = null;
        RaiseStatusChanged();
    }

    // Applies the captured format to the current selection (called on pointer-release while armed).
    private void ApplyFormatPainterToSelection()
    {
        if (_painterFmt is not { } f || !HasSelection) return;
        ApplyStyleToSelection(r =>
        {
            r.FontWeight = f.w; r.FontStyle = f.st; r.TextDecorations = f.dec;
            r.FontSize = f.size; r.FontFamily = f.family; r.Foreground = f.fg; r.Background = f.bg;
        });
        CancelFormatPainter();
    }

    /// <summary>Toggles bold on the current selection (or the caret word), Word-style: turns it off only
    /// when every character already has it, otherwise turns it on for all of them.</summary>
    public void ToggleBold() => ToggleCharacterFormat(
        r => r.FontWeight.IsBold(), (r, on) => r.FontWeight = on ? FontWeights.Bold : FontWeights.Normal);
    /// <summary>Toggles italic on the current selection (or the caret word), Word-style (see <see cref="ToggleBold"/>).</summary>
    public void ToggleItalic() => ToggleCharacterFormat(
        r => r.FontStyle == FontStyle.Italic, (r, on) => r.FontStyle = on ? FontStyle.Italic : FontStyle.Normal);
    /// <summary>Toggles underline, Word-style (see <see cref="ToggleBold"/>).</summary>
    public void ToggleUnderline() => ToggleCharacterFormat(
        r => r.TextDecorations.HasFlag(TextDecorationFlags.Underline), (r, on) => SetDecoration(r, TextDecorationFlags.Underline, on),
        forced: (r, _) => !string.IsNullOrEmpty(r.NavigateUri)); // a link is drawn underlined (DrawnUnderline)
    /// <summary>Toggles strikethrough, Word-style (see <see cref="ToggleBold"/>).</summary>
    public void ToggleStrikethrough() => ToggleCharacterFormat(
        r => r.TextDecorations.HasFlag(TextDecorationFlags.Strikethrough), (r, on) => SetDecoration(r, TextDecorationFlags.Strikethrough, on));

    private static void SetDecoration(Run r, TextDecorationFlags flag, bool on)
        => r.TextDecorations = on ? r.TextDecorations | flag : r.TextDecorations & ~flag;

    // Word's rule for the character toggles: OFF only when every character in the target already has the
    // format, otherwise ON for all of them. Flipping each run on its own (what this did until 2026-09-11,
    // and what upstream still does) turned "plain BOLD plain" into "BOLD plain BOLD". The target is
    // resolved ONCE and handed to the apply step, so the check and the change cannot disagree about which
    // text they mean. With no text to look at (a pending caret style), the caret's own format decides —
    // pending styles included, which is what makes two presses before typing cancel out.
    //
    // `forced`: where the renderer draws the format whatever the run says (a link's underline — a heading's
    // bold was one until it became a run attribute, see HeadingStyle). Such text counts as having it — the
    // toggle judges what is SHOWN, as the toolbar reports it — and when every targeted character is forced
    // the toggle does nothing and records no undo step:
    // no change it could make would show. It used to flip a hidden flag there, the screen unchanged.
    private void ToggleCharacterFormat(Func<Run, bool> has, Action<Run, bool> set, Func<Run, Paragraph, bool>? forced = null)
    {
        if (IsReadOnly) return;
        var target = ResolveStyleTarget();
        bool Shown(Run r, Paragraph p) => has(r) || (forced?.Invoke(r, p) ?? false);
        bool allOn;
        if (target.Pending)
        {
            if (_caret.Paragraph is not { } cp) return;
            var shown = CaretFormatRun() ?? new Run();
            if (forced?.Invoke(shown, cp) == true) return;
            allOn = Shown(shown, cp);
        }
        else
        {
            var runs = TargetRuns(target).ToList();
            if (forced != null && runs.Count > 0 && runs.All(x => forced(x.Run, x.Para))) return;
            allOn = runs.Count > 0 && runs.All(x => Shown(x.Run, x.Para));
        }
        bool on = !allOn;
        ApplyStyleToSelection(r => set(r, on), target);
    }
    /// <summary>Sets the font size (pt) of the current selection (or the caret word).</summary>
    public void SetFontSize(double size) => ApplyStyleToSelection(r => r.FontSize = size);

    // Standard point-size ladder for the 크게/작게 (larger/smaller) commands.
    private static readonly double[] FontSizeLadder =
        { 8, 9, 10, 10.5, 11, 12, 14, 16, 18, 20, 24, 28, 32, 36, 40, 48, 56, 72, 96 };

    /// <summary>Bumps the font size to the next larger step on the standard ladder (based on the caret size).</summary>
    public void IncreaseFontSize() => StepFontSize(+1);
    /// <summary>Drops the font size to the next smaller step on the standard ladder (based on the caret size).</summary>
    public void DecreaseFontSize() => StepFontSize(-1);

    private void StepFontSize(int dir)
    {
        double cur = GetCaretFormat().FontSize;
        double target;
        if (dir > 0)
        {
            target = FontSizeLadder[^1];
            foreach (var v in FontSizeLadder) if (v > cur + 0.01) { target = v; break; }
        }
        else
        {
            target = FontSizeLadder[0];
            for (int i = FontSizeLadder.Length - 1; i >= 0; i--) if (FontSizeLadder[i] < cur - 0.01) { target = FontSizeLadder[i]; break; }
        }
        SetFontSize(target);
    }
    /// <summary>Sets the foreground color; pass <see langword="null"/> to clear back to the automatic
    /// default (<see cref="TextForeground"/> — matters for dark-theme hosts, where a stamped explicit
    /// black would vanish against the background).</summary>
    public void SetForeground(Color? color) => ApplyStyleToSelection(r => r.Foreground = color);
    /// <summary>Sets the font family.</summary>
    public void SetRunFontFamily(string family) => ApplyStyleToSelection(r => r.FontFamily = family);
    /// <summary>Sets the highlight (background) color; pass <see langword="null"/> to clear.</summary>
    public void SetHighlight(Color? color) => ApplyStyleToSelection(r => r.Background = color);

    /// <summary>Clears character formatting on the current selection (or caret word).</summary>
    public void ClearFormatting() => ApplyStyleToSelection(r =>
    {
        // Back to the paragraph's own look: body text to the host default, heading text to the heading's
        // preset (bold, its size) — the format the heading applied when it was set (HeadingStyle). Writing
        // DefaultFontSize in a heading shrank an H1 from 20 to 14 with a host default of 14.
        int level = r.Parent is Paragraph hp && HeadingStyle.IsHeading(hp.HeadingLevel) ? hp.HeadingLevel : 0;
        r.FontWeight = level > 0 ? FontWeights.Bold : FontWeights.Normal;
        r.FontStyle = FontStyle.Normal;
        r.FontSize = level > 0 ? HeadingStyle.Size(level) : DefaultFontSize;
        r.Foreground = null;
        r.Background = null;
        r.FontFamily = null;
        r.TextDecorations = TextDecorationFlags.None;
        r.NavigateUri = null;
    });

    /// <summary>Sets the text alignment of the selected paragraphs (or the caret paragraph). Applies to
    /// every paragraph in a table-cell block selection.</summary>
    public void SetTextAlignment(TextAlignment align)
    {
        if (_caret.Paragraph == null || IsReadOnly) return;
        if (SelectedParagraphs().All(p => p.TextAlignment == align)) return; // no undo step that undoes nothing
        PushUndo(null);
        foreach (var p in SelectedParagraphs()) p.TextAlignment = align;
        AfterFormat();
    }

    /// <summary>Sets the heading level (1–6 = h1–h6, 0 = body) of the selected paragraphs (or the caret
    /// paragraph). Applies to every paragraph in a table-cell block selection.</summary>
    public void SetHeading(int level)
    {
        if (_caret.Paragraph == null || IsReadOnly) return;
        PushUndo(null);
        // Setting a level writes the heading's format onto its text (HeadingStyle.Retype): the preset on
        // first application, the new size on a level change, off again back to body. After that the text
        // holds ordinary run attributes — bold can be turned off, any size set.
        foreach (var p in SelectedParagraphs())
        {
            HeadingStyle.Retype(p, p.HeadingLevel, level, DefaultFontSize);
            p.HeadingLevel = level;
        }
        AfterFormat();
    }

    /// <summary>Sets a proportional line spacing on the paragraphs in the selection as HWP % ÷ 100 — line
    /// box = largest font size × <paramref name="spacing"/> (1.6 = 160%). Pass <see cref="double.NaN"/> to
    /// clear back to the HWP default 160%.</summary>
    public void SetLineSpacing(double spacing)
    {
        if (_caret.Paragraph == null || IsReadOnly) return;
        // Equals: NaN matches NaN. Nothing to change -> no undo step that undoes nothing (upstream round 34).
        if (SelectedParagraphs().All(p => p.LineSpacing.Equals(spacing) && double.IsNaN(p.LineHeight))) return;
        PushUndo(null);
        foreach (var p in SelectedParagraphs()) { p.LineSpacing = spacing; p.LineHeight = double.NaN; }
        AfterFormat();
    }

    /// <summary>Sets an absolute line height in DIPs on the paragraphs in the selection. Pass
    /// <see cref="double.NaN"/> to clear back to natural spacing.</summary>
    public void SetLineHeight(double height)
    {
        if (_caret.Paragraph == null || IsReadOnly) return;
        if (SelectedParagraphs().All(p => p.LineHeight.Equals(height) && double.IsNaN(p.LineSpacing))) return; // see SetLineSpacing
        PushUndo(null);
        foreach (var p in SelectedParagraphs()) { p.LineHeight = height; p.LineSpacing = double.NaN; }
        AfterFormat();
    }

    // The paragraphs the current selection intersects (or the caret paragraph when collapsed). A
    // rectangular table-cell block selection yields every paragraph in the selected cells (the same cells
    // the renderer tints), not the row-major linear span — so paragraph formatting matches the highlight.
    private System.Collections.Generic.IEnumerable<Paragraph> SelectedParagraphs()
    {
        if (CellBlockSelection() is { } cb)
        {
            foreach (var (r, c, cell) in cb.tb.LogicalCells())
                if (r >= cb.r0 && r <= cb.r1 && c >= cb.c0 && c <= cb.c1)
                    foreach (var p in ParagraphsInBlocks(cell.Blocks))
                        yield return p;
            yield break;
        }
        if (!HasSelection) { if (_caret.Paragraph != null) yield return _caret.Paragraph; yield break; }
        TextPointer s = _selStart, e = _selEnd;
        if (s.CompareTo(e) > 0) (s, e) = (e, s);
        bool inRange = false;
        foreach (var p in AllParagraphs())
        {
            if (ReferenceEquals(p, s.Paragraph)) inRange = true;
            if (inRange) yield return p;
            if (ReferenceEquals(p, e.Paragraph)) break;
        }
    }

    /// <summary>Toggles blockquote styling on the selected paragraphs (or the caret paragraph). When the
    /// selection mixes quoted and unquoted paragraphs, the toggle turns them all on (only clears when every
    /// paragraph is already quoted). Applies across a table-cell block selection.</summary>
    public void ToggleQuote()
    {
        if (_caret.Paragraph == null || IsReadOnly) return;
        var paras = SelectedParagraphs().ToList();
        if (paras.Count == 0) return;
        PushUndo(null);
        bool allQuote = paras.All(p => p.IsQuote);
        foreach (var p in paras) p.IsQuote = !allQuote;
        AfterFormat();
    }

    /// <summary>Adjusts the indent of the selected paragraphs (or the caret paragraph) by
    /// <paramref name="delta"/> px (clamped 0–400). Applies across a table-cell block selection.</summary>
    public void Indent(double delta)
    {
        if (_caret.Paragraph == null || IsReadOnly) return;
        // Outdent at 0 (or indent at the cap) changes nothing: no undo step that undoes nothing.
        if (SelectedParagraphs().All(p => Math.Clamp(p.Indent + delta, 0, 400) == p.Indent)) return;
        PushUndo(null);
        foreach (var p in SelectedParagraphs()) p.Indent = Math.Clamp(p.Indent + delta, 0, 400);
        AfterFormat();
    }

    /// <summary>Toggles a bullet list on the selected paragraphs.</summary>
    public void ToggleBullet() => SetListType(ListKind.Bullet);
    /// <summary>Toggles a numbered list on the selected paragraphs.</summary>
    public void ToggleNumbering() => SetListType(ListKind.Ordered);

    /// <summary>Applies a specific list marker style to the selected paragraphs, turning the matching list
    /// kind on (bullet glyphs vs number formats). Unlike the toggles, a style pick never turns the list off.</summary>
    public void SetListStyle(ListMarkerStyle style) => SetListType(ListMarkerStyleKind(style), style);

    /// <summary>Removes any list attribute — bullet or numbering, including the marker style and nesting
    /// level — from the selected paragraphs (or the caret paragraph). The explicit counterpart to the
    /// toggles, which only turn off the matching kind of the caret paragraph. Applies across a
    /// table-cell block selection.</summary>
    public void RemoveList()
    {
        if (_caret.Paragraph == null || IsReadOnly) return;
        // No list anywhere in it: no undo step that undoes nothing.
        if (SelectedParagraphs().All(p => p.ListType == ListKind.None && p.ListMarker == ListMarkerStyle.Default && p.ListLevel == 0)) return;
        PushUndo(null);
        foreach (var p in SelectedParagraphs()) ClearList(p);
        AfterFormat();
    }

    // Turning a list OFF clears the WHOLE list state, not just the kind. ListLevel keeps indenting the
    // paragraph either way — ParaLeft adds `ListLevel * 20` regardless of ListType — so clearing only
    // ListType left a formerly nested item with a phantom indent and no marker to explain it. ListMarker
    // would likewise resurface if the paragraph became a list again later. Shared by RemoveList and every
    // toggle-off path in SetListType, so "off" means the same thing everywhere (this is what let the
    // toolbar's redundant "없음" picker entry go away).
    private static void ClearList(Paragraph p)
    {
        p.ListType = ListKind.None;
        p.ListMarker = ListMarkerStyle.Default;
        p.ListLevel = 0;
    }

    // The list kind a marker style belongs to (number formats -> Ordered, everything else -> Bullet).
    private static ListKind ListMarkerStyleKind(ListMarkerStyle s) => s switch
    {
        ListMarkerStyle.Decimal or ListMarkerStyle.DecimalParen or ListMarkerStyle.LowerAlpha
            or ListMarkerStyle.UpperAlpha or ListMarkerStyle.LowerRoman => ListKind.Ordered,
        _ => ListKind.Bullet,
    };

    private void SetListType(ListKind kind, ListMarkerStyle? marker = null)
    {
        if (_caret.Paragraph == null || Document == null || IsReadOnly) return;
        PushUndo(null);
        bool turningOff = marker == null && _caret.Paragraph.ListType == kind;
        void ApplyMarker(Paragraph par) { if (marker.HasValue) par.ListMarker = marker.Value; }

        // Caret inside a table cell — with a rectangular cell-block selection, with a plain selection
        // spanning several paragraphs of ONE cell, or with no selection at all. All three set the list
        // flag in place on EVERY selected paragraph: the top-level newline-splitting/reindexing path
        // below only handles Document.Blocks paragraphs (a cell paragraph isn't there, so
        // SplitByNewlines' RemoveAt/Insert would no-op and the toggle would silently do nothing), and a
        // cell paragraph's soft line breaks stay one list item anyway.
        // Collecting via SelectedParagraphs is what makes the multi-paragraph-in-one-cell case work —
        // that isn't a CellBlockSelection (both endpoints are in the same cell), so it used to fall
        // through to a branch that only ever touched the CARET paragraph and left the rest unbulleted.
        // Toggle is group-based: turn all off only when every selected paragraph already has this kind.
        if (CellBlockSelection() != null || (_caret.Paragraph is { } cp0 && FindCell(cp0) is not null))
        {
            var cellParas = SelectedParagraphs().ToList();
            if (cellParas.Count == 0) cellParas.Add(_caret.Paragraph);
            bool off = marker == null && cellParas.All(p => p.ListType == kind);
            foreach (var p in cellParas)
            {
                if (off) ClearList(p);
                else { p.ListType = kind; ApplyMarker(p); }
            }
            UpdateParents(Document);
            AfterFormat();
            return;
        }

        var targets = SelectedTopLevelParagraphs();
        if (targets.Count == 0)
        {
            if (turningOff) ClearList(_caret.Paragraph);
            else { _caret.Paragraph.ListType = kind; ApplyMarker(_caret.Paragraph); }
            AfterFormat();
            return;
        }
        if (turningOff)
        {
            foreach (var tp in targets) ClearList(tp);
            UpdateParents(Document);
            AfterFormat();
            return;
        }

        var ssP = _selStart.Paragraph; int ssO = _selStart.Offset;
        var seP = _selEnd.Paragraph; int seO = _selEnd.Offset;
        var cpP = _caret.Paragraph; int cpO = _caret.Offset;
        TextPointer? nSs = null, nSe = null, nCp = null;

        (Paragraph, int) MapInto(List<Paragraph> items, Paragraph tp, int off)
        {
            string plain = BuildPlain(tp);
            int line = 0, lineStart = 0, lim = Math.Min(off, plain.Length);
            for (int i = 0; i < lim; i++) if (plain[i] == '\n') { line++; lineStart = i + 1; }
            var it = items[Math.Min(line, items.Count - 1)];
            return (it, Math.Min(off - lineStart, GetParagraphLength(it)));
        }

        foreach (var tp in targets.OrderByDescending(t => Document.Blocks.IndexOf(t)))
        {
            int idx = Document.Blocks.IndexOf(tp);
            if (idx < 0) { tp.ListType = kind; ApplyMarker(tp); continue; }
            var items = SplitByNewlines(tp);
            foreach (var it in items) { it.ListType = kind; ApplyMarker(it); it.Parent = Document; }
            Document.Blocks.RemoveAt(idx);
            for (int k = 0; k < items.Count; k++) Document.Blocks.Insert(idx + k, items[k]);
            if (tp == ssP) { var (p2, o2) = MapInto(items, tp, ssO); nSs = new TextPointer(p2, o2); }
            if (tp == seP) { var (p2, o2) = MapInto(items, tp, seO); nSe = new TextPointer(p2, o2); }
            if (tp == cpP) { var (p2, o2) = MapInto(items, tp, cpO); nCp = new TextPointer(p2, o2); }
        }
        if (nSs != null) _selStart = nSs;
        if (nSe != null) _selEnd = nSe;
        if (nCp != null) _caret = nCp;
        UpdateParents(Document);
        AfterFormat();
    }

    private List<Paragraph> SelectedTopLevelParagraphs()
    {
        var result = new List<Paragraph>();
        if (Document == null) return result;
        var all = AllParagraphs();
        int si = _selStart.Paragraph != null ? all.IndexOf(_selStart.Paragraph) : -1;
        int ei = _selEnd.Paragraph != null ? all.IndexOf(_selEnd.Paragraph) : -1;
        if (si < 0 || ei < 0)
        {
            if (_caret.Paragraph != null && Document.Blocks.Contains(_caret.Paragraph))
                result.Add(_caret.Paragraph);
            return result;
        }
        if (si > ei) (si, ei) = (ei, si);
        for (int i = si; i <= ei; i++)
            if (Document.Blocks.Contains(all[i])) result.Add(all[i]);
        return result;
    }

    private List<Paragraph> SplitByNewlines(Paragraph p)
    {
        var result = new List<Paragraph>();
        // Full paragraph-format inheritance. The old hand-picked copy carried only 5 fields, so turning a
        // multi-line paragraph into list items silently dropped ListMarker (the ◦ / "a)" glyph), custom
        // LineSpacing/LineHeight, MarginRight/MarginTop/Bottom, IsQuote and HeadingLevel — the same loss
        // CloneFormat was introduced to fix for the Enter-split path.
        Paragraph NewPara() => p.CloneFormat();
        var cur = NewPara();
        foreach (var inl in p.Inlines)
        {
            if (inl is Run run && run.Text != null && run.Text.Contains('\n'))
            {
                var parts = run.Text.Split('\n');
                for (int k = 0; k < parts.Length; k++)
                {
                    if (k > 0) { result.Add(cur); cur = NewPara(); }
                    if (parts[k].Length > 0)
                    {
                        var nr = (Run)run.Clone();
                        nr.Text = parts[k];
                        nr.Parent = cur;
                        cur.Inlines.Add(nr);
                    }
                }
            }
            else
            {
                // MOVE, don't clone. Only a run straddling a '\n' has to become new objects; everything
                // else appears exactly once in the output, and the source paragraph is spliced out of the
                // document on return. Cloning an InlineImage or InlineTable here replaced it with a copy
                // and left the original — with its cell paragraphs — detached, so a caret inside an
                // inline table's cell was orphaned by toggling a bullet on its host paragraph: it pointed
                // into a subtree no longer in the document, and typing went nowhere visible. (Safe to
                // re-parent in place: the enumeration doesn't modify p.Inlines, and the undo checkpoint
                // was taken before any of this.)
                inl.Parent = cur;
                cur.Inlines.Add(inl);
            }
        }
        result.Add(cur);
        foreach (var pp in result)
            if (pp.Inlines.Count == 0) pp.Inlines.Add(new Run { Text = "" });
        return result;
    }

    // What a character command acts on, decided once: a rectangular cell block, a text range (the
    // selection, or the word at a collapsed caret), or — no word at the caret — a pending style for the
    // next typed text. Shared by ApplyStyleToSelection and the toggles' "is it all on already?" check.
    private readonly record struct StyleTarget(
        (TableBlock tb, int r0, int c0, int r1, int c1)? Cells, TextRange? Range, bool Pending);

    private StyleTarget ResolveStyleTarget()
    {
        // A rectangular table-cell block selection (drag across cells): style every selected cell in
        // full. The linear TextRange path only partially styles the first/last cell (from/to the drag
        // offsets) and, because it walks cells in row-major order, bleeds into cells outside the
        // selected column band — so multi-cell formatting wouldn't match the highlighted rectangle.
        if (CellBlockSelection() is { } cb) return new StyleTarget(cb, null, false);
        if (HasSelection) return new StyleTarget(null, new TextRange(_selStart, _selEnd), false);
        if (_caret.Paragraph is not { } p) return new StyleTarget(null, null, false);
        string plain = BuildPlain(p);
        int off = Math.Clamp(_caret.Offset, 0, plain.Length);
        static bool IsWord(char ch) => char.IsLetterOrDigit(ch) || ch == '_';
        bool inWord = (off < plain.Length && IsWord(plain[off])) || (off > 0 && IsWord(plain[off - 1]));
        if (!inWord) return new StyleTarget(null, null, true);
        var (ws, we) = WordBoundsAt(plain, off);
        return new StyleTarget(null, new TextRange(new TextPointer(p, ws), new TextPointer(p, we)), false);
    }

    // The runs a resolved target covers, with their paragraphs (a heading's bold is forced per paragraph),
    // read WITHOUT touching the document — ApplyPropertyValue would split runs before the undo checkpoint.
    // A range is walked in the paragraph order ApplyPropertyValue uses; a cell block is every run of every
    // selected cell, as ApplyStyleToCellRange styles them. A run whose covered part holds no visible
    // character (empty, or only line breaks) gets no say in a toggle.
    private IEnumerable<(Run Run, Paragraph Para)> TargetRuns(StyleTarget t)
    {
        if (t.Cells is { } cb)
        {
            foreach (var (r, c, cell) in cb.tb.LogicalCells())
            {
                if (r < cb.r0 || r > cb.r1 || c < cb.c0 || c > cb.c1) continue;
                foreach (var p in ParagraphsInBlocks(cell.Blocks))
                    foreach (var inl in p.Inlines)
                        if (inl is Run run && !string.IsNullOrEmpty(run.Text)) yield return (run, p);
            }
        }
        else if (t.Range is { } range)
        {
            var (s, e) = (range.Start, range.End);
            bool inRange = false;
            foreach (var p in AllParagraphs())
            {
                if (ReferenceEquals(p, s.Paragraph)) inRange = true;
                if (inRange)
                {
                    int a = ReferenceEquals(p, s.Paragraph) ? s.Offset : 0;
                    int b = ReferenceEquals(p, e.Paragraph) ? e.Offset : int.MaxValue;
                    int pos = 0;
                    foreach (var inl in p.Inlines)
                    {
                        int len = InlineLen(inl);
                        if (inl is Run run && len > 0 && pos < b && pos + len > a)
                        {
                            int from = Math.Max(a, pos) - pos, to = Math.Min(b, pos + len) - pos;
                            if (run.Text!.AsSpan(from, to - from).Trim('\n').Length > 0) yield return (run, p);
                        }
                        pos += len;
                    }
                }
                if (ReferenceEquals(p, e.Paragraph)) break;
            }
        }
    }

    private void ApplyStyleToSelection(Action<Run> styleAction, StyleTarget? resolved = null)
    {
        if (IsReadOnly) return;
        var target = resolved ?? ResolveStyleTarget();
        if (target.Cells is { } cb)
        {
            PushUndo(null);
            ApplyStyleToCellRange(cb.tb, cb.r0, cb.c0, cb.r1, cb.c1, styleAction);
        }
        else if (target.Range is { } range)
        {
            PushUndo(null);
            range.ApplyPropertyValue(styleAction);
        }
        else if (target.Pending)
        {
            // Pending: applies to the next typed text (the undo checkpoint comes with that typing).
            (_pendingCaretStyles ??= new List<Action<Run>>()).Add(styleAction);
        }
        AfterFormat();
    }

    // Applies a character style to every logical cell whose anchor lies inside the selected rectangle
    // [r0..r1]×[c0..c1] — the same cells the renderer tints (DrawNestedTable) — styling each cell's
    // paragraphs in full (nested content included). Empty placeholder runs are styled directly so text
    // typed next in a selected empty cell inherits the format.
    private void ApplyStyleToCellRange(TableBlock tb, int r0, int c0, int r1, int c1, Action<Run> styleAction)
    {
        foreach (var (r, c, cell) in tb.LogicalCells())
        {
            if (r < r0 || r > r1 || c < c0 || c > c1) continue;
            foreach (var p in ParagraphsInBlocks(cell.Blocks))
            {
                int len = GetParagraphLength(p);
                if (len > 0)
                    new TextRange(new TextPointer(p, 0), new TextPointer(p, len)).ApplyPropertyValue(styleAction);
                else
                    foreach (var inl in p.Inlines) if (inl is Run run) styleAction(run);
            }
        }
    }

    private static (int start, int end) WordBoundsAt(string s, int off)
    {
        static bool IsWord(char ch) => char.IsLetterOrDigit(ch) || ch == '_';
        int a = Math.Clamp(off, 0, s.Length), b = a;
        while (a > 0 && IsWord(s[a - 1])) a--;
        while (b < s.Length && IsWord(s[b])) b++;
        return (a, b);
    }

    // Applies any pending caret styles to the just-inserted [start, start+len) range. During an IME
    // composition the styles must survive (each TextUpdating replaces the composed range, so they are
    // re-applied per update and only cleared when the composition commits) — pass clear: false there.
    private void ApplyPendingStyles(Paragraph p, int start, int len, bool clear = true)
    {
        if (_pendingCaretStyles is not { Count: > 0 } pend || len <= 0) return;
        if (clear) _pendingCaretStyles = null;
        new TextRange(new TextPointer(p, start), new TextPointer(p, start + len))
            .ApplyPropertyValue(r => { foreach (var a in pend) a(r); });
    }

    // After a formatting change: re-measure (size/heading/list change heights) and repaint.
    private void AfterFormat()
    {
        _coalesceKey = null;
        // A linear selection can span table cells while the caret ends OUTSIDE the table (drag from above
        // a table to below it, then Ctrl+B / change the size): the cells' paragraphs are restyled — both
        // TextRange and SelectedParagraphs walk into cells — but the caret-ancestor invalidation below
        // would miss that table, leaving its rows measured for the old formatting. Formatting commands
        // are discrete user actions (not per-keystroke), so clearing every cached row height is cheap.
        if (HasSelection) _tableRowHeights.Clear();
        InvalidateCaretTableMeasure();
        RelayoutToViewport();
        SyncIme();
        RestartBlink();
        RaiseStatusChanged();
    }
}
