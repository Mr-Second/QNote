using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Windows.UI;
using Windows.UI.Text;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using WinUIRichEditor.Controls;
using WinUIRichEditor.Documents;

namespace WinUIRichEditor.Formatters;

/// <summary>
/// Parses a practical subset of RTF — the "Rich Text Format" both Word and the Korean HWP put on
/// the clipboard — into a <see cref="FlowDocument"/>: paragraphs, bold/italic/underline/strike,
/// font size, foreground colour, embedded images (<c>\pict</c> PNG/JPEG, bytes carried inline), and
/// tables (<c>\trowd</c>…<c>\cell</c>…<c>\row</c>) including merged cells, per-cell shading and
/// tables nested in a cell. Zero external dependencies beyond a code-page provider for CJK text
/// (<c>\'hh</c> bytes are decoded with the document's <c>\ansicpg</c>).
/// <para>A horizontal merge is written GEOMETRICALLY — the merged span is one cell whose <c>\cellx</c>
/// sits at its right edge — because HWP dissolves the flag form (<c>\clmgf</c>/<c>\clmrg</c>) that Word
/// also accepts. Reading handles both. Vertical merge has no geometric encoding and keeps the flags.</para>
/// <para>Known losses: a nested table's column widths come back at the default (they live in the
/// ignorable <c>{\*\nesttableprops}</c> group), and a table whose every row is merged identically reads
/// back as one wide column, because nothing in the file then reveals the underlying grid.</para>
/// <para>RTF has no inline table, so an <see cref="InlineTable"/> is written as a block-level table that
/// splits its host paragraph — other applications see exactly that. Our own ignorable
/// <c>{\*\arinline}</c> marker rides along so this reader puts it back on the text line.</para>
/// </summary>
public static class RtfDocumentFormatter
{
    // The left gutter a list item at this nesting level gets, in twips. One place, because the writer adds
    // it to \li and the reader subtracts the same amount back out — if these two ever disagree, a list
    // item's indent grows or shrinks on every save.
    internal static int ListGutterTwips(int level) => 720 * (Math.Clamp(level, 0, 8) + 1);

    // Marker style <-> wire code, spelled out in BOTH directions on purpose: a cast would tie the RTF we
    // write to the enum's declaration order, so inserting a style would silently change the format.
    internal static int MarkerCode(ListMarkerStyle s) => s switch
    {
        ListMarkerStyle.Disc => 1,
        ListMarkerStyle.Circle => 2,
        ListMarkerStyle.Square => 3,
        ListMarkerStyle.Dash => 4,
        ListMarkerStyle.Decimal => 5,
        ListMarkerStyle.DecimalParen => 6,
        ListMarkerStyle.LowerAlpha => 7,
        ListMarkerStyle.UpperAlpha => 8,
        ListMarkerStyle.LowerRoman => 9,
        _ => 0,
    };

    internal static ListMarkerStyle MarkerFromCode(int c) => c switch
    {
        1 => ListMarkerStyle.Disc,
        2 => ListMarkerStyle.Circle,
        3 => ListMarkerStyle.Square,
        4 => ListMarkerStyle.Dash,
        5 => ListMarkerStyle.Decimal,
        6 => ListMarkerStyle.DecimalParen,
        7 => ListMarkerStyle.LowerAlpha,
        8 => ListMarkerStyle.UpperAlpha,
        9 => ListMarkerStyle.LowerRoman,
        _ => ListMarkerStyle.Default,
    };

    static RtfDocumentFormatter()
    {
        // CP949 (Korean), Shift-JIS, GB2312 etc. aren't in .NET's default set ??register them so
        // \'hh runs from HWP/Word decode correctly.
        try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); }
        catch (Exception ex) { RichEditorDiagnostics.Report(ex); }
    }

    /// <summary>True if <paramref name="text"/> starts with the RTF signature.</summary>
    public static bool LooksLikeRtf(string? text)
        => text != null && text.TrimStart().StartsWith(@"{\rtf", StringComparison.Ordinal);

    /// <summary>Parses an RTF string into a <see cref="FlowDocument"/> (empty document on failure).
    /// <para>Failure is indistinguishable from a genuinely empty document here. Callers that would
    /// REPLACE open content with the result — loading a file, not pasting a fragment — must use
    /// <see cref="TryParse"/> instead, or a damaged file silently blanks the document and the next
    /// save writes that blank over the original.</para></summary>
    public static FlowDocument Parse(string rtf)
    {
        // Deliberately NOT TryParse: this path is for pasting a fragment, where whatever was readable is
        // better than nothing, and a truncated clipboard flavour should still contribute its text. The
        // strictness that protects an open document belongs only to TryParse.
        try { return RunNormalizer.Compact(new RtfParser(rtf).Run()); }
        catch (Exception ex) { RichEditorDiagnostics.Report(ex); return new FlowDocument(); }
    }

    /// <summary>Parses an RTF string, reporting whether it succeeded. Returns <see langword="false"/>
    /// only when the RTF is damaged badly enough to abort the parse; a well-formed but empty document
    /// is a success. On failure <paramref name="document"/> is an empty document (matching
    /// <see cref="Parse"/>) and <paramref name="error"/> describes the fault.
    /// <para>This is the safe entry point for anything that replaces open content — it is the RTF
    /// counterpart of the exception <c>DocumentSerializer.Deserialize</c> throws for damaged JSON.
    /// A parse that aborts part-way reports failure rather than returning what it had read so far:
    /// truncated content that LOOKS like a document is the outcome most likely to be saved over the
    /// original by a host that cannot tell it is incomplete.</para></summary>
    public static bool TryParse(string rtf, out FlowDocument document, out string? error)
    {
        try
        {
            var parser = new RtfParser(rtf);
            var parsed = RunNormalizer.Compact(parser.Run());
            // Truncation is the common damage — a half-copied file, a cut-short download — and it does
            // not throw: the reader just runs out of input and finalizes what it has. That looked like a
            // clean parse of a SHORTER document, so LoadRtf replaced the open one with it and the next
            // save wrote the shorter version over the original. Unclosed groups are the giveaway.
            if (parser.UnclosedGroups > 0)
            {
                document = new FlowDocument();
                error = $"The RTF ends inside {parser.UnclosedGroups} unclosed group(s); the file is truncated.";
                return false;
            }
            document = parsed;
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            // Also reported to RichEditorDiagnostics: Parse() discards `error`, so without this a paste
            // that fell back to plain text would be invisible to a host watching only the fault channel.
            RichEditorDiagnostics.Report(ex);
            document = new FlowDocument();
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    /// <summary>Serializes a <see cref="FlowDocument"/> to an RTF string (the inverse of <see cref="Parse"/>):
    /// paragraphs, runs (bold/italic/underline/strike, size, colour, font family), alignment/indent,
    /// headings, lists (as literal markers), tables, and embedded PNG/JPEG images.</summary>
    public static string Write(FlowDocument document) => new RtfWriter().Build(document);
}

// One pass over the RTF char stream. Group state is pushed on '{' and popped on '}', so nested
// formatting restores correctly. Normal text is buffered as bytes and decoded with the document code
// page so multi-byte CJK characters come out whole.
internal sealed class RtfParser
{
    private readonly string _s;
    private int _i;

    private enum Dest { Normal, Skip, ColorTable, Pict, FontTable, FieldInst, PageChrome }

    private struct State
    {
        public bool Bold, Italic, Underline, Strike;
        public double FontSize;   // points; 0 = use the run default
        public int Color;         // index into _colors; -1 = default (black)
        public int Highlight;     // index into _colors (\highlightN); -1 = none
        public int Font;          // index into _fontNames (\fN); -1 = default font
        public string? LinkUrl;   // hyperlink applied to runs inside a \fldrslt group; null = none
        public Dest Dest;
        public int UnicodeSkip;   // chars to swallow after a \uN (set by \ucN)
    }

    private State _st = new() { Color = -1, Highlight = -1, Font = -1, UnicodeSkip = 1 };
    private readonly Stack<State> _stack = new();

    private readonly FlowDocument _doc = new();
    private Paragraph _para = new();
    private readonly StringBuilder _run = new();

    private readonly List<byte> _bytes = new();
    private int _codepage = 1252;
    private Encoding? _enc;
    private Encoding Enc => _enc ??= GetEncoding(_codepage);

    private readonly List<Color> _colors = new();
    private int _ctR, _ctG, _ctB;
    private bool _ctHasColor;

    // \fonttbl: font index -> family name (so \fN runs keep their font — HWP/Word paste otherwise loses
    // every font family), plus per-font \fcharset code pages so CJK bytes (both font NAMES like
    // "맑은 고딕" and run text) decode with the right encoding even when \ansicpg says otherwise.
    private readonly Dictionary<int, string> _fontNames = new();
    private readonly Dictionary<int, int> _fontCodePages = new();
    private readonly Dictionary<int, Encoding> _encCache = new();
    private int _ftblIndex = -1;                      // the \fN entry currently being read
    private readonly List<byte> _ftblBytes = new();   // pending name bytes (code-page text / \'hh)
    private readonly StringBuilder _ftblName = new();

    // \field: the \fldinst instruction is collected here; a HYPERLINK target parsed from it becomes the
    // LinkUrl of the following \fldrslt group's runs (so Word/HWP/own-writer links round-trip).
    private readonly StringBuilder _fldInst = new();
    private string? _pendingFieldUrl;

    private readonly StringBuilder _pictHex = new();
    private string? _pictMime;
    private int _pictWTwips, _pictHTwips;

    // \sl / \slmult state (line spacing; see Apply's "sl"/"slmult" cases).
    private int _slTwips;
    private bool _slMult;

    private void ApplyLineSpacingWords()
    {
        if (_slTwips == 0) { _para.LineSpacing = double.NaN; _para.LineHeight = double.NaN; return; }
        // \slmult is Word's multiple of the natural line (≈1.2 × the size); LineSpacing is HWP's ratio of
        // the size, so ratio = N/240 × 1.2 = N/200 (the writer's inverse).
        if (_slMult) { _para.LineSpacing = _slTwips / 200.0; _para.LineHeight = double.NaN; }
        else { _para.LineHeight = Math.Abs(_slTwips) / 15.0; _para.LineSpacing = double.NaN; } // "at least" ≈ exact
    }

    // A cell is a TableCell, not just its paragraph: Word writes a table inside a cell as nested rows,
    // and those become real nested tables (which the model has supported since the cell became a block
    // container). Intra-cell \par still becomes a newline within the cell's paragraph.
    private List<List<TableCell>>? _tableRows;
    private List<TableCell>? _curRow;
    private List<int> _curCellx = new();
    private List<int>? _tableCellx;
    private List<List<int>>? _tableRowCellx;

    // Nested tables, keyed by RTF nesting depth (\itap): 2 = a table inside a cell, 3 = one deeper, and
    // so on. _nestRows[d] holds the finished rows at that depth and _nestRow[d] the row being filled;
    // both are consumed when the cell one level up closes. Depth comes from \itap, which is how Word
    // tells the levels apart — every \nestcell looks the same otherwise.
    private readonly Dictionary<int, List<List<TableCell>>> _nestRows = new();
    private readonly Dictionary<int, List<TableCell>> _nestRow = new();
    private int _itap = 1;
    // Paragraphs already closed in the cell being filled, keyed by the depth that cell belongs to: text
    // that preceded a nested table stays with ITS cell instead of being taken by the deeper one.
    private readonly Dictionary<int, List<Block>> _cellPending = new();

    // Merge flags per cell definition (\clmgf/\clmrg horizontal, \clvmgf/\clvmrg vertical). Flags
    // precede their \cellx in the row definition; FinalizeTable turns them back into col/row spans.
    // Shading is a \colortbl index (0 = none), resolved to a colour in FinalizeTable — the colour table
    // is fully read by then, and a row definition can precede it in pathological input.
    private struct CellDef { public bool HFirst, HCont, VFirst, VCont; public CellVerticalAlignment VAlign; public int Shading; }
    private CellDef _pendingCellDef;
    private List<CellDef> _curCellDefs = new();
    private List<List<CellDef>>? _tableRowDefs;

    // Set by our own {\*\arinlineN} marker: the next top-level table was an InlineTable and belongs back
    // on a paragraph's text line.
    private bool _nextTableInline;
    // N = 1 when the table OPENS its host paragraph — nothing preceded it, so the writer emitted no \par
    // (that \par would render as a blank line above the table) and there is no closed host paragraph to
    // reuse. Reattaching to the PREVIOUS paragraph in that case merges two paragraphs and swallows the
    // earlier one — and "a paragraph holding nothing but the table" is the ordinary shape of an inline
    // table, so this is the common case, not an edge one.
    private bool _inlineTableOpensHost;

    public RtfParser(string s) => _s = s;

    public FlowDocument Run()
    {
        while (_i < _s.Length)
        {
            char c = _s[_i];
            // Commit the text collected so far BEFORE descending into a group. A group can switch to a
            // destination we skip ({\*\nesttableprops …}, bookmarks, fields — all normal in Word output),
            // and the closing brace's FlushRun then runs with that destination still active and throws the
            // pending run away. Word documents lost the text preceding any such group.
            if (c == '{') { if (_st.Dest == Dest.Normal) FlushRun(); _stack.Push(_st); _i++; }
            else if (c == '}')
            {
                if (_st.Dest == Dest.Pict) FinalizePict();
                else if (_st.Dest == Dest.FieldInst) FinalizeFieldInst();
                else if (_st.Dest == Dest.PageChrome && _stack.Count == _chromeDepth) FinalizePageChrome();
                else FlushRun();
                _st = _stack.Count > 0 ? _stack.Pop() : _st; _i++;
            }
            else if (c == '\\') ReadControl();
            else if (c == '\r' || c == '\n') _i++;
            else if (_st.Dest == Dest.ColorTable && c == ';') { CloseColorEntry(); _i++; }
            else if (_st.Dest == Dest.Pict) { if (Uri.IsHexDigit(c)) _pictHex.Append(c); _i++; }
            else if (_st.Dest == Dest.FontTable) { FontTableChar(c); _i++; }
            else if (_st.Dest == Dest.FieldInst) { _fldInst.Append(c); _i++; }
            // Page chrome collects plain characters too — its text is ordinary text, just bound for the
            // header/footer band instead of the body. AppendByte itself gates on the destination.
            else { if (_st.Dest is Dest.Normal or Dest.PageChrome) AppendByte(c); _i++; }
        }
        EndRow();
        FlushRun();
        FinalizeTable();
        if (_para.Inlines.Count > 0) _doc.Blocks.Add(_para);
        if (_doc.Blocks.Count == 0) _doc.Blocks.Add(new Paragraph());
        // RTF is brace-balanced, so groups still open here mean the input ENDED early. Nothing above
        // notices: the loop simply runs out of characters and every partial structure is finalized as
        // though it had been closed properly, which is why a truncated file used to look like a clean
        // parse. See TryParse — a truncated file must not be allowed to replace an open document.
        UnclosedGroups = _stack.Count;
        return _doc;
    }

    /// How many groups were still open when the input ran out. Non-zero means truncated.
    public int UnclosedGroups { get; private set; }

    // ---- control word / symbol ----

    private void ReadControl()
    {
        _i++; // past '\'
        if (_i >= _s.Length) return;
        char c = _s[_i];

        if (c == '\'') { ReadHexChar(); return; }
        if (!char.IsLetter(c))
        {
            _i++;
            if (_st.Dest == Dest.Normal)
            {
                if (c == '\\' || c == '{' || c == '}') AppendByte(c);
                else if (c == '~') AppendByte(' ');
            }
            if (c == '*') _st.Dest = Dest.Skip;
            return;
        }

        int start = _i;
        while (_i < _s.Length && char.IsLetter(_s[_i])) _i++;
        string word = _s.Substring(start, _i - start);
        int? param = null;
        // A control word's parameter is an optional '-' followed by DIGITS, and NEITHER half of that may
        // abort the parse. The token loop has no per-word recovery, so anything thrown here costs every
        // character after it.
        //
        // 1. A '-' with no digit after it is not a parameter at all — the control word ends there and the
        //    '-' is literal text. Consuming it produced the substring "-", and int.Parse("-") threw:
        //    `{\rtf1\ansi\fs-x hello}` — brace-balanced, complete, and read by Word as `\fs` followed by
        //    the text "-x hello" — came back as an EMPTY document. (Upstream still consumes the '-' here,
        //    so the character silently disappears from its output: a backport candidate.)
        // 2. Digits too long for an int are treated as ABSENT, which every keyword already handles. The
        //    spec caps parameters at 32 bits, so nothing valid is lost — `\cellx99999999999` is simply a
        //    malformed token, and a malformed TOKEN is not a damaged DOCUMENT. This repo used to throw
        //    here on purpose, to give TryParse/LoadRtf a damage signal, and that was inconsistent with
        //    (1): it refused to open a complete, readable file over one out-of-range number. Real damage
        //    is truncation, and UnclosedGroups still catches that. Converged with the upstream peer,
        //    whose reasoning this is.
        bool hasParam = _i < _s.Length &&
            (char.IsDigit(_s[_i]) || (_s[_i] == '-' && _i + 1 < _s.Length && char.IsDigit(_s[_i + 1])));
        if (hasParam)
        {
            int ns = _i;
            if (_s[_i] == '-') _i++;
            while (_i < _s.Length && char.IsDigit(_s[_i])) _i++;
            if (int.TryParse(_s.AsSpan(ns, _i - ns), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                param = parsed;
        }
        if (_i < _s.Length && _s[_i] == ' ') _i++;

        Apply(word, param);
    }

    private void ReadHexChar()
    {
        _i++; // past '\''
        if (_i + 1 >= _s.Length) return;
        string hex = _s.Substring(_i, 2);
        _i += 2;
        if (!byte.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b)) return;
        if (_st.Dest == Dest.Normal) _bytes.Add(b);
        else if (_st.Dest == Dest.FontTable) _ftblBytes.Add(b); // CJK font names arrive as \'hh bytes
    }

    private void Apply(string w, int? p)
    {
        switch (w)
        {
            case "ansicpg": _codepage = p ?? 1252; _enc = null; break;

            case "b": SetBold(p != 0); break;
            case "i": SetItalic(p != 0); break;
            case "ul": SetUnderline(p != 0); break;
            case "ulnone": SetUnderline(false); break;
            case "strike": SetStrike(p != 0); break;
            case "fs": FlushRun(); _st.FontSize = (p ?? 24) / 2.0; break;
            case "cf": FlushRun(); _st.Color = p ?? -1; break;
            case "highlight": FlushRun(); _st.Highlight = p ?? -1; break;
            case "plain": FlushRun(); _st.Bold = _st.Italic = _st.Underline = _st.Strike = false; _st.FontSize = 0; _st.Color = -1; _st.Highlight = -1; _st.Font = -1; break;

            // \fN: inside \fonttbl it BEGINS a font definition; in body text it selects that font for
            // the following run. Pending bytes flush first so they decode with the PREVIOUS font's charset.
            case "f":
                if (_st.Dest == Dest.FontTable) { CommitFontEntry(); _ftblIndex = p ?? 0; }
                else if (_st.Dest == Dest.Normal) { FlushRun(); _st.Font = p ?? -1; }
                break;
            case "fcharset":
                if (_st.Dest == Dest.FontTable && _ftblIndex >= 0 && CharsetCodePage(p ?? -1) is { } fcp)
                    _fontCodePages[_ftblIndex] = fcp;
                break;

            case "par": case "sect":
                _skipMarkerText = false;
                // Inside a header/footer a \par separates lines of a band this model stores as ONE line —
                // it must not reach EndParagraph, which would append a paragraph to the DOCUMENT.
                if (_st.Dest == Dest.PageChrome) { FlushRun(); _chromeText.Append(' '); break; }
                EndParagraph();
                break;
            case "line": if (_st.Dest == Dest.Normal) _bytes.Add(10); break;
            // A \tab also TERMINATES a list marker's text (see armkb/armkn): that one is structure, so it
            // is consumed rather than emitted. Every other \tab is a real tab.
            case "tab":
                if (_skipMarkerText) { _skipMarkerText = false; break; }
                if (_st.Dest == Dest.Normal) _bytes.Add(9);
                break;
            // \pard resets paragraph formatting; alignment/indent/spacing control words follow it.
            case "pard":
                _skipMarkerText = false; // a marker's text never spans a paragraph reset
                _para.TextAlignment = TextAlignment.Left; _para.Indent = 0;
                _para.LineSpacing = double.NaN; _para.LineHeight = double.NaN;
                _slTwips = 0; _slMult = false;
                _paraBottomBorder = false; // a border is paragraph formatting, so the reset clears it
                SetItap(1); // \itap is a paragraph property, so a reset drops back to the body level
                break;
            // A horizontal rule has no control word of its own in RTF; Word — and our writer — spell it
            // as an empty paragraph carrying a bottom border. Only the writer's half of that existed, so
            // every divider came back as a blank line and was gone for good after one save/load.
            case "brdrb": if (_st.Dest == Dest.Normal && _curRow == null) _paraBottomBorder = true; break;
            case "ql": _para.TextAlignment = TextAlignment.Left; break;
            case "qc": _para.TextAlignment = TextAlignment.Center; break;
            case "qr": _para.TextAlignment = TextAlignment.Right; break;
            case "qj": _para.TextAlignment = TextAlignment.Justify; break;
            case "li": _para.Indent = Math.Max(0, (p ?? 0) / 15.0); break; // twips -> px

            // Line spacing: \slN (+\slmult1 = proportional, N/240 lines; \slmult0 = absolute twips,
            // negative meaning "exactly"). The two words arrive in either order, so both re-apply.
            case "sl": _slTwips = p ?? 0; ApplyLineSpacingWords(); break;
            case "slmult": _slMult = (p ?? 0) != 0; ApplyLineSpacingWords(); break;

            // Structure control words act only in the body. Word puts a nested table's row definition in
            // {\*\nesttableprops \trowd …\cellx…\nestrow} — an ignorable group we skip — and acting on it
            // restarted the row we were building, discarding the parent cell's accumulated text.
            case "trowd": if (_st.Dest == Dest.Normal) StartRow(); break;
            case "cell": if (_st.Dest == Dest.Normal) EndCell(); break;
            case "row": if (_st.Dest == Dest.Normal) EndRow(); break;
            case "intbl": break;
            case "cellx":
                if (_st.Dest == Dest.Normal)
                {
                    _curCellx.Add(p ?? 0);
                    _curCellDefs.Add(_pendingCellDef);
                    _pendingCellDef = default;
                }
                break;

            case "clmgf": _pendingCellDef.HFirst = true; break;
            case "clmrg": _pendingCellDef.HCont = true; break;
            case "clvmgf": _pendingCellDef.VFirst = true; break;
            case "clvmrg": _pendingCellDef.VCont = true; break;
            case "clvertalt": _pendingCellDef.VAlign = CellVerticalAlignment.Top; break;
            case "clvertalc": _pendingCellDef.VAlign = CellVerticalAlignment.Center; break;
            case "clvertalb": _pendingCellDef.VAlign = CellVerticalAlignment.Bottom; break;
            // The writer has always been able to emit cell shading and Word/HWP honour it, but the reader
            // skipped it — so a cell background exported and reloaded through our OWN format came back
            // colourless.
            case "clcbpat": _pendingCellDef.Shading = p ?? 0; break;
            // Paragraph shading — the counterpart of \clcbpat for a paragraph rather than a cell. Word
            // writes it too, so this also reads a shaded paragraph pasted IN from Word.
            // Indexed exactly like \clcbpat above: _colors is already RTF-indexed (entry 0 is the
            // colour table's leading ';' = "default"), so the index is used as-is, and A == 0 means
            // that default rather than a real colour.
            case "cbpat":
                if (p is { } shade && shade > 0 && shade < _colors.Count && _colors[shade].A != 0)
                    _para.Background = _colors[shade];
                break;

            // A table inside a cell: the model nests, and the writer emits these, so they come back as a
            // real nested TableBlock in the parent cell rather than flattened tab/newline text.
            case "itap": SetItap(p ?? 1); break;
            case "nestcell": if (_st.Dest == Dest.Normal) EndNestedCell(); break;
            case "nestrow": EndNestedRow(); break; // see EndNestedRow: intentionally not destination-gated
            // The fallback copy of a nested table, for readers that can't nest. We flatten instead, so
            // skip it — otherwise its \par landed as a stray line break in the parent cell and its text
            // arrived twice.
            case "nonesttables": _st.Dest = Dest.Skip; break;
            // Our own inline-table marker (see the InlineTable branch in WriteParagraph). It arrives as
            // {\*\arinline}, so \* has already switched this group to Skip — the group is empty and only
            // the flag matters. A parser field rather than pushed/popped group state, because the marker
            // group CLOSES before the table it describes begins.
            case "arinline": _nextTableInline = true; _inlineTableOpensHost = (p ?? 0) != 0; break;

            case "shptxt": _st.Dest = Dest.Normal; break;
            case "sp": case "sn": case "sv": _st.Dest = Dest.Skip; break;

            // \field {\*\fldinst HYPERLINK "url"}{\fldrslt text}: read the instruction (overriding the
            // \* skip, like \shppict) and hand the parsed URL to the result group's runs.
            case "field": _pendingFieldUrl = null; break;
            case "fldinst": _st.Dest = Dest.FieldInst; _fldInst.Clear(); break;
            case "fldrslt":
                // Not from inside the page chrome: a footer's NUMPAGES field has a ldrslt group, and
                // switching to Normal there would drop its text into the document body.
                if (_st.Dest != Dest.PageChrome) { _st.Dest = Dest.Normal; _st.LinkUrl = _pendingFieldUrl; }
                break;

            case "u": EmitUnicode(p ?? 0); break;
            case "uc": _st.UnicodeSkip = p ?? 1; break;

            // Typographic control words Word emits pervasively (smart quotes for every apostrophe,
            // em/en dashes, bullets). Dropping them silently loses characters ("don't" -> "dont").
            case "rquote": AppendChar('’'); break;
            case "lquote": AppendChar('‘'); break;
            case "rdblquote": AppendChar('”'); break;
            case "ldblquote": AppendChar('“'); break;
            case "emdash": AppendChar('—'); break;
            case "endash": AppendChar('–'); break;
            case "bullet": AppendChar('•'); break;
            case "emspace": AppendChar(' '); break;
            case "enspace": AppendChar(' '); break;
            case "qmspace": AppendChar(' '); break;

            // {\*\pn …} — the legacy list definition (see WriteListMarker). `\*` has already set this
            // group to Skip, which is what keeps {\pntxtb •} out of the text; the control words inside
            // still reach here, because Apply runs for every one of them regardless of destination. That
            // is what makes reading the definition possible without also reading its fallback text.
            //
            // Applied to _para directly: the definition precedes the paragraph's text, and \pard (which
            // resets paragraph formatting) precedes the definition, so nothing later clears it.
            case "pn": _para.ListType = ListKind.Bullet; _para.ListMarker = ListMarkerStyle.Default; break;
            case "pnlvlblt": _para.ListType = ListKind.Bullet; break;
            case "pnlvlbody": _para.ListType = ListKind.Ordered; break;
            case "pndec": _para.ListMarker = ListMarkerStyle.Default; break;
            case "pnlcltr": _para.ListMarker = ListMarkerStyle.LowerAlpha; break;
            case "pnucltr": _para.ListMarker = ListMarkerStyle.UpperAlpha; break;
            case "pnlcrm": _para.ListMarker = ListMarkerStyle.LowerRoman; break;
            // Ours (see WriteListMarker): the list kind + exact marker style, and a signal that the text up
            // to the next \tab is the MARKER rather than content. Every other reader skips the group and
            // renders that text — the only spelling both Word and HWP show — while we drop it.
            // Ours: the \sl just seen is the DEFAULT single spacing this writer states for other readers,
            // not a value the author chose. Put the paragraph back to unset, which is a different rule
            // from 1.0 in this model (natural baseline vs uniform spacing).
            case "arsl": _para.LineSpacing = double.NaN; _para.LineHeight = double.NaN; _slTwips = 0; _slMult = false; break;

            // Ours: the list nesting level, announced before the marker tag. It restores ListLevel (which
            // RTF has no standard place for) and says how much gutter the \li above carried.
            case "arlvl":
                _para.ListLevel = Math.Clamp(p ?? 0, 0, 8);
                _pendingListGutter = RtfDocumentFormatter.ListGutterTwips(_para.ListLevel);
                break;

            case "armkb": _para.ListType = ListKind.Bullet; StartMarkerText(p); break;
            case "armkn": _para.ListType = ListKind.Ordered; StartMarkerText(p); break;

            // {\header …}/{ooter …} and their left/right/first-page variants. Until this existed they
            // were UNKNOWN destinations, so their text flowed straight into the body: opening a Word
            // document with a header put that header in as the document's first paragraph.
            case "header": case "headerl": case "headerr": case "headerf":
                StartPageChrome(header: true); break;
            case "footer": case "footerl": case "footerr": case "footerf":
                StartPageChrome(header: false); break;
            // The page-number placeholders. Inside the chrome they mean "this document wants page numbers",
            // and contribute no text — otherwise a literal "3" would be baked into the footer string.
            case "chpgn": case "pgnstart":
                if (_st.Dest == Dest.PageChrome) { FlushRun(); _chromeNumbers = true; _chromeTextClosed = true; }
                break;

            // Paper size. Only the dimensions are in the file, so the page size is recovered by matching them
            // against the table the control lays out with — an unrecognised paper leaves PageSize alone rather
            // than guessing. Document level (Word) and section level (HWP) carry the same paper; both are read.
            // (Ported from upstream with the page margins, 2026-09-24: this reader dropped the paper entirely.)
            case "paperw": case "pgwsxn":
                if (_st.Dest == Dest.Normal && p is int pw) { _paperW = pw; MatchPaper(); } break;
            case "paperh": case "pghsxn":
                if (_st.Dest == Dest.Normal && p is int ph) { _paperH = ph; MatchPaper(); } break;
            case "landscape": case "lndscpsxn":
                if (_st.Dest == Dest.Normal) { _landscape = true; MatchPaper(); } break;

            // Page margins, in twips — document level (Word) and section level (HWP) again. A file from another
            // word processor carries its own margins; dropping them paginated it differently from its author.
            case "margl": case "marglsxn":
                if (_st.Dest == Dest.Normal && p is int ml) { _marginTwips[0] = ml; ApplyMargins(); } break;
            case "margt": case "margtsxn":
                if (_st.Dest == Dest.Normal && p is int mt) { _marginTwips[1] = mt; ApplyMargins(); } break;
            case "margr": case "margrsxn":
                if (_st.Dest == Dest.Normal && p is int mr) { _marginTwips[2] = mr; ApplyMargins(); } break;
            case "margb": case "margbsxn":
                if (_st.Dest == Dest.Normal && p is int mb) { _marginTwips[3] = mb; ApplyMargins(); } break;

            case "colortbl": _st.Dest = Dest.ColorTable; _colors.Clear(); _ctR = _ctG = _ctB = 0; _ctHasColor = false; break;
            case "fonttbl": _st.Dest = Dest.FontTable; _ftblIndex = -1; _ftblName.Clear(); _ftblBytes.Clear(); break;
            case "stylesheet": case "info": case "pntext": case "themedata":
            case "datastore": case "xmlnstbl": case "rsidtbl": case "generator": case "listtable":
            case "listoverridetable": case "revtbl":
                _st.Dest = Dest.Skip; break;

            case "red": _ctR = p ?? 0; _ctHasColor = true; break;
            case "green": _ctG = p ?? 0; _ctHasColor = true; break;
            case "blue": _ctB = p ?? 0; _ctHasColor = true; break;

            case "shppict": _st.Dest = Dest.Normal; break;
            case "nonshppict": _st.Dest = Dest.Skip; break;
            case "pict": FlushRun(); _st.Dest = Dest.Pict; _pictHex.Clear(); _pictMime = null; _pictWTwips = _pictHTwips = 0; break;
            case "pngblip": _pictMime = "image/png"; break;
            case "jpegblip": _pictMime = "image/jpeg"; break;
            case "dibitmap": _pictMime = "dib"; break; // raw DIB (no BMP file header); wrapped in FinalizePict
            case "picwgoal": _pictWTwips = p ?? 0; break;
            case "pichgoal": _pictHTwips = p ?? 0; break;

            default: break;
        }
    }

    // ---- fields (hyperlinks) ----

    // Called when a group holding \fldinst text closes. An empty buffer means a NESTED group inside the
    // instruction closed first — keep the already-captured URL instead of clobbering it.
    private void FinalizeFieldInst()
    {
        string inst = _fldInst.ToString().Trim();
        _fldInst.Clear();
        if (inst.Length == 0) return;
        var m = System.Text.RegularExpressions.Regex.Match(inst, "HYPERLINK\\s+(?:\"([^\"]+)\"|(\\S+))",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        // A script link (javascript:, vbscript:, data:) is dropped as the HTML reader drops it — RTF is a clipboard
        // flavour, and the link would otherwise go back out in exported and clipboard HTML (2026-09-24).
        if (m.Success) _pendingFieldUrl = HtmlDocumentFormatter.SafeHref(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
    }

    // ---- font table ----

    private void FontTableChar(char c)
    {
        if (c == ';') { CommitFontEntry(); return; }
        if (c < 256) _ftblBytes.Add((byte)c);
        else { FlushFtblBytes(); _ftblName.Append(c); }
    }

    private void FlushFtblBytes()
    {
        if (_ftblBytes.Count == 0) return;
        var enc = _ftblIndex >= 0 && _fontCodePages.TryGetValue(_ftblIndex, out var cp) ? CachedEnc(cp) : Enc;
        _ftblName.Append(enc.GetString(_ftblBytes.ToArray()));
        _ftblBytes.Clear();
    }

    private void CommitFontEntry()
    {
        FlushFtblBytes();
        if (_ftblIndex >= 0)
        {
            string name = _ftblName.ToString().Trim();
            if (name.Length > 0) _fontNames[_ftblIndex] = RunNormalizer.Intern(name);
        }
        _ftblName.Clear();
        _ftblIndex = -1;
    }

    // Windows \fcharsetN -> code page, for the CJK/legacy sets that matter here. Null = no override.
    private static int? CharsetCodePage(int charset) => charset switch
    {
        0 => 1252,    // ANSI
        128 => 932,   // Shift-JIS
        129 => 949,   // Korean (HWP/Word emit this on Korean fonts)
        130 => 1361,  // Johab
        134 => 936,   // GB2312
        136 => 950,   // Big5
        161 => 1253, 162 => 1254, 163 => 1258, 177 => 1255, 178 => 1256,
        186 => 1257, 204 => 1251, 222 => 874, 238 => 1250,
        _ => null,
    };

    private Encoding CachedEnc(int codepage)
    {
        if (!_encCache.TryGetValue(codepage, out var e)) _encCache[codepage] = e = GetEncoding(codepage);
        return e;
    }

    // The encoding for pending BODY bytes: the current font's \fcharset code page when known (more
    // reliable for CJK than the document \ansicpg), else the document encoding.
    private Encoding RunEnc => _st.Font >= 0 && _fontCodePages.TryGetValue(_st.Font, out var cp) ? CachedEnc(cp) : Enc;

    private void CloseColorEntry()
    {
        _colors.Add(_ctHasColor
            ? Color.FromArgb(255, (byte)_ctR, (byte)_ctG, (byte)_ctB)
            : Color.FromArgb(0, 0, 0, 0)); // leading "auto" entry: zero alpha = "use the run default"
        _ctR = _ctG = _ctB = 0;
        _ctHasColor = false;
    }

    private void SetBold(bool v) { if (v != _st.Bold) FlushRun(); _st.Bold = v; }
    private void SetItalic(bool v) { if (v != _st.Italic) FlushRun(); _st.Italic = v; }
    private void SetUnderline(bool v) { if (v != _st.Underline) FlushRun(); _st.Underline = v; }
    private void SetStrike(bool v) { if (v != _st.Strike) FlushRun(); _st.Strike = v; }

    // ---- page setup: paper and margins ----

    // \paperw/\paperh in twips, and whether \landscape was seen. Held until both dimensions are known, since
    // they arrive as separate control words and either order is legal.
    private int _paperW, _paperH;
    private bool _landscape;

    // Margins in twips, left/top/right/bottom, -1 until the file states one. They arrive as four separate
    // control words in any order, and a file may state only some — the rest keep the default.
    private readonly int[] _marginTwips = { -1, -1, -1, -1 };

    // Each stated margin, once the paper is known well enough to check it against. The paper may still be
    // Continuous here (margins but no size, or a size with no name here): its print fallback is A4.
    private void ApplyMargins()
    {
        double Side(int i, double fallback) => _marginTwips[i] >= 0 ? PageSetup.TwipsToMm(_marginTwips[i]) : fallback;
        var d = PageSetup.DefaultMargin;
        var m = new PageMargins(Side(0, d.Left), Side(1, d.Top), Side(2, d.Right), Side(3, d.Bottom));
        var ps = _doc.PageSetup;
        var (w, h) = PageSetup.PaperMillimetres(ps?.PageSize ?? RichEditorPageSize.Continuous,
                                                ps?.Orientation ?? RichEditorPageOrientation.Portrait);
        if (!PageSetup.IsUsableMargin(m, w, h))
        {
            // Not "keep what we had": an earlier call may have accepted these margins against the A4
            // fallback, and the paper the file went on to declare is smaller.
            if (_doc.PageSetup is { } stale) stale.Margin = PageSetup.DefaultMargin;
            return;
        }
        _doc.PageSetup ??= new PageSetup();
        _doc.PageSetup.Margin = m;
    }

    // Turns the paper dimensions back into a named page size by asking PaperDips for each candidate — the same
    // table the control lays out with, so a document written here comes back exactly. Tolerance: 2 twips.
    private void MatchPaper()
    {
        if (_paperW <= 0 || _paperH <= 0) return;
        foreach (RichEditorPageSize size in Enum.GetValues<RichEditorPageSize>())
        {
            if (size == RichEditorPageSize.Continuous) continue;
            var orientation = _landscape ? RichEditorPageOrientation.Landscape : RichEditorPageOrientation.Portrait;
            var (w, h) = PageSetup.PaperDips(size, orientation);
            if (Math.Abs((int)Math.Round(w * 15) - _paperW) <= 2 && Math.Abs((int)Math.Round(h * 15) - _paperH) <= 2)
            {
                _doc.PageSetup ??= new PageSetup();
                _doc.PageSetup.PageSize = size;
                _doc.PageSetup.Orientation = orientation;
                // Margins that arrived first were checked against the A4 fallback; check them against the real paper.
                if (Array.Exists(_marginTwips, t => t >= 0)) ApplyMargins();
                return;
            }
        }
    }

    // ---- page chrome ({\header …} / {\footer …}) ----

    private bool _chromeIsHeader;      // which of the two the current group is
    private int _chromeDepth = -1;     // group depth that opened it, so the matching '}' is identifiable
    private bool _chromeNumbers;       // a \chpgn / page field was seen inside a footer
    // Once the page-number placeholder appears, the rest of the band is its DECORATION, not text: our own
    // writer follows \chpgn with " / " and a NUMPAGES field, and collecting that as footer text made the
    // separator accumulate — "꼬리말/" then "꼬리말//" on the next save. The same guard keeps a Word footer's
    // trailing "of 12" out of the single line this model stores.
    private bool _chromeTextClosed;
    private readonly StringBuilder _chromeText = new();

    private void StartPageChrome(bool header)
    {
        _st.Dest = Dest.PageChrome;
        _chromeIsHeader = header;
        _chromeDepth = _stack.Count;
        _chromeText.Clear();
        _chromeTextClosed = false;
    }

    // Takes the text the chrome group accumulated and puts it on the document's PageSetup instead of into
    // the body. The model holds ONE line per band, so a multi-paragraph header collapses to its text with
    // single spaces — lossy, and honestly so: this reader has nowhere richer to put it.
    private void FinalizePageChrome()
    {
        FlushRun(); // banks whatever the last line collected

        string text = System.Text.RegularExpressions.Regex.Replace(_chromeText.ToString(), @"\s+", " ").Trim();
        _chromeText.Clear();
        _chromeDepth = -1;
        _chromeTextClosed = false;

        if (text.Length == 0 && !_chromeNumbers) return;
        _doc.PageSetup ??= new PageSetup();
        if (_chromeIsHeader)
        {
            if (text.Length > 0 && string.IsNullOrEmpty(_doc.PageSetup.Header)) _doc.PageSetup.Header = text;
        }
        else
        {
            if (text.Length > 0 && string.IsNullOrEmpty(_doc.PageSetup.Footer)) _doc.PageSetup.Footer = text;
            if (_chromeNumbers) _doc.PageSetup.ShowPageNumbers = true;
        }
        _chromeNumbers = false;
    }

    // True from a {\*\armkb|armkn} tag until the \tab that closes the list marker's literal text, which
    // this reader must NOT take as content (other readers render it — that is why it is written at all).
    private bool _skipMarkerText;

    // The gutter the writer folded into \li for the list level last announced by {\*\arlvl}.
    private int _pendingListGutter;

    // Also carries the exact marker style in the tag's parameter, and takes the list gutter back out of
    // the indent: the writer adds it to \li so other readers lay the item out, and \li is read here as the
    // author's own indent. Without the subtraction a list item's indent grew by the gutter on every cycle.
    private void StartMarkerText(int? code)
    {
        if (code is { } c) _para.ListMarker = RtfDocumentFormatter.MarkerFromCode(c);
        if (_pendingListGutter > 0)
        {
            _para.Indent = Math.Max(0, _para.Indent - _pendingListGutter / 15.0);
            _pendingListGutter = 0;
        }
        _skipMarkerText = true;
    }

    private void EmitUnicode(int code)
    {
        FlushBytes();
        if (code < 0) code += 65536;
        if ((_st.Dest == Dest.Normal || _st.Dest == Dest.PageChrome) && !_skipMarkerText)
        {
            if (code >= 0 && code <= 0xFFFF) _run.Append((char)code);
        }
        else if (_st.Dest == Dest.FontTable && code >= 0 && code <= 0xFFFF)
        {
            FlushFtblBytes();
            _ftblName.Append((char)code); // font names may arrive as \uN too
        }
        for (int k = 0; k < _st.UnicodeSkip && _i < _s.Length; k++)
        {
            if (_s[_i] == '\\')
                _i += (_i + 1 < _s.Length && _s[_i + 1] == '\'') ? 4 : 2;
            else if (_s[_i] == '{' || _s[_i] == '}') break;
            else _i++;
        }
    }

    // ---- building ----

    private void AppendByte(char c)
    {
        if ((_st.Dest != Dest.Normal && _st.Dest != Dest.PageChrome) || _skipMarkerText) return;
        if (c < 256) _bytes.Add((byte)c);
        else { FlushBytes(); _run.Append(c); }
    }

    // Emits a literal Unicode character produced by a control word (\rquote, \emdash, …). Pending
    // code-page bytes flush first so the character lands in document order.
    private void AppendChar(char ch)
    {
        if ((_st.Dest != Dest.Normal && _st.Dest != Dest.PageChrome) || _skipMarkerText) return;
        FlushBytes();
        _run.Append(ch);
    }

    private void FlushBytes()
    {
        if (_bytes.Count == 0) return;
        _run.Append(RunEnc.GetString(_bytes.ToArray()));
        _bytes.Clear();
    }

    private void FlushRun()
    {
        FlushBytes();
        if (_run.Length == 0) return;
        // Page chrome banks its text instead of discarding it. This is the ONE place that has to know,
        // because FlushRun is called from everywhere — every group close, every font/bold change — and a
        // header whose text was already collected would otherwise be thrown away by the first of those.
        // A footer with a page-number field hit exactly that: the field's closing brace ran FlushRun with
        // the chrome destination still active, and the footer text vanished.
        if (_st.Dest == Dest.PageChrome)
        {
            if (!_chromeTextClosed) _chromeText.Append(_run);
            _run.Clear();
            return;
        }
        if (_st.Dest != Dest.Normal) { _run.Clear(); return; }
        _para.Inlines.Add(MakeRun(_run.ToString()));
        _run.Clear();
    }

    private Run MakeRun(string text)
    {
        var r = new Run
        {
            Text = text,
            FontWeight = _st.Bold ? FontWeightValues.Bold : FontWeightValues.Normal,
            FontStyle = _st.Italic ? FontStyle.Italic : FontStyle.Normal,
            FontSize = _st.FontSize > 0 ? _st.FontSize : 10, // pt; body default
        };
        var dec = TextDecorationFlags.None;
        if (_st.Underline) dec |= TextDecorationFlags.Underline;
        if (_st.Strike) dec |= TextDecorationFlags.Strikethrough;
        r.TextDecorations = dec;
        if (_st.Font >= 0 && _fontNames.TryGetValue(_st.Font, out var fam)) r.FontFamily = fam;
        r.NavigateUri = _st.LinkUrl; // non-null only inside a \fldrslt with a parsed HYPERLINK
        if (_st.Color >= 0 && _st.Color < _colors.Count)
        {
            var col = _colors[_st.Color];
            if (col.A != 0) r.Foreground = col;
        }
        if (_st.Highlight >= 0 && _st.Highlight < _colors.Count)
        {
            var bg = _colors[_st.Highlight];
            if (bg.A != 0) r.Background = bg;
        }
        return r;
    }

    private void EndParagraph()
    {
        // Inside a cell, \par is a real paragraph break — the model has held several paragraphs per cell
        // since cells became block containers, and folding them into one paragraph with newlines lost
        // that structure on every Word/HWP paste. It also made our own round trip non-idempotent: the
        // count of paragraphs in a cell changed between the first and second cycle, because only a \par
        // that happened to sit next to an \itap transition survived as a break (found by the fuzz).
        if (_curRow != null)
        {
            FlushRun();
            if (_para.Inlines.Count > 0)
            {
                if (!_cellPending.TryGetValue(_itap, out var pending)) _cellPending[_itap] = pending = new List<Block>();
                pending.Add(_para);
                _para = new Paragraph();
            }
            return;
        }
        FlushRun();
        FinalizeTable();
        // RTF has no block picture: our writer emits one as `\pard <pict>\par`, so that \par TERMINATES
        // the image's own paragraph rather than starting a new one. Reading it as content added a blank
        // paragraph after every image — and another on the next cycle, and the next, so a document saved
        // and reopened a few times grew a gap under each picture. Same rule as the \par before a nested
        // table: a \par at a structural boundary is structure, not a blank line.
        //
        // A blank line the author really did put under an image still survives: the writer emits it as
        // its OWN `\pard\par`, so the first \par is consumed here and the second one lands as usual.
        bool structural = _imageOwnsNextPar && _para.Inlines.Count == 0;
        _imageOwnsNextPar = false;
        if (structural) return;

        // An empty paragraph with a bottom border is a horizontal rule (see the \brdrb case above).
        if (_paraBottomBorder && _para.Inlines.Count == 0)
        {
            _paraBottomBorder = false;
            _doc.Blocks.Add(new DividerBlock());
            _para = new Paragraph();
            return;
        }
        _paraBottomBorder = false;
        _doc.Blocks.Add(_para);
        _para = new Paragraph();
    }

    // True immediately after a block picture was added, while its terminating \par is still pending.
    private bool _imageOwnsNextPar;
    // True while the paragraph being read carries a bottom border (\brdrb) — see EndParagraph.
    private bool _paraBottomBorder;

    // ---- tables ----

    // Note for the repeated row definition our writer emits (see BuildRowDefinition): the second \trowd
    // arrives after every \cell, so it only clears _curCellx/_curCellDefs, which the repeated \cellx words
    // then refill with the identical values before \row consumes them. _curRow (the cells themselves) is
    // preserved by the ??= below, so the repeat is a no-op on the way back in.
    private void StartRow()
    {
        _tableRows ??= new List<List<TableCell>>();
        _curRow ??= new List<TableCell>();
        _curCellx = new List<int>();
        _curCellDefs = new List<CellDef>();
        _pendingCellDef = default;
        _para = new Paragraph();
    }

    private void EndCell()
    {
        if (_curRow == null) StartRow();
        _curRow!.Add(TakeCell(_itap + 1)); // a top-level cell's nested table lives at \itap 2
    }

    private void EndRow()
    {
        if (_curRow == null) return;
        _tableRows ??= new List<List<TableCell>>();
        _tableRows.Add(_curRow);
        _curRow = null;
        if (_tableCellx == null && _curCellx.Count > 0) _tableCellx = _curCellx;
        // EVERY row's boundaries, not just the first: a horizontally merged cell is one wide \cellx, so
        // the column grid is the union of all rows' boundaries and a row's own list says which grid
        // columns each of its cells spans (see BuildTableFromGeometry).
        (_tableRowCellx ??= new List<List<int>>()).Add(_curCellx);
        (_tableRowDefs ??= new List<List<CellDef>>()).Add(_curCellDefs);
        _curCellDefs = new List<CellDef>();
    }

    // \nestcell ends one cell of the nested row at the current depth. That cell may itself contain the
    // table one level deeper, which is why it goes through the same builder as a top-level cell.
    private void EndNestedCell()
    {
        int depth = Math.Max(2, _itap);
        if (!_nestRow.TryGetValue(depth, out var row)) _nestRow[depth] = row = new List<TableCell>();
        row.Add(TakeCell(depth + 1));
    }

    // \nestrow ends the nested row at the current depth. It arrives inside {\*\nesttableprops …}, which
    // is an ignorable destination, so this one is deliberately NOT gated on the destination — a reader
    // that supports nesting has to act on it there. The row's \cellx widths are in that same group and
    // are not read, so a nested table comes back at the default column width.
    private void EndNestedRow()
    {
        int depth = Math.Max(2, _itap);
        if (!_nestRow.TryGetValue(depth, out var row) || row.Count == 0) return;
        if (!_nestRows.TryGetValue(depth, out var rows)) _nestRows[depth] = rows = new List<List<TableCell>>();
        rows.Add(row);
        _nestRow.Remove(depth);
    }

    // Builds the table from the rows' \cellx GEOMETRY, which is where a horizontal merge lives: a merged
    // span is one cell whose boundary jumps several columns. The column grid is the union of every row's
    // boundaries, and a cell's colspan is the number of grid columns its boundary swallows. This reads
    // both writers' output — ours (geometric) and Word's (one \cellx per column plus \clmgf/\clmrg), the
    // latter because the flags are folded in on top: an \clmrg cell extends the one before it.
    // Returns null when the input carries no boundaries at all, so the caller can fall back.
    private TableBlock? BuildTableFromGeometry(List<List<TableCell>> rows, List<List<int>>? rowCellx,
                                               List<List<CellDef>>? defs)
    {
        if (rowCellx == null || rows.Count == 0) return null;

        var grid = new List<int>();
        foreach (var rc in rowCellx)
            foreach (int b in rc)
                if (b > 0 && !grid.Contains(b)) grid.Add(b);
        if (grid.Count == 0) return null;
        grid.Sort();

        // Per row: where each emitted cell starts in the grid and how many columns it spans.
        var placed = new List<List<(int start, int span, TableCell cell, CellDef def)>>();
        for (int r = 0; r < rows.Count; r++)
        {
            var line = new List<(int, int, TableCell, CellDef)>();
            var bounds = r < rowCellx.Count ? rowCellx[r] : new List<int>();
            var rdefs = defs != null && r < defs.Count ? defs[r] : new List<CellDef>();
            int prev = 0;
            for (int i = 0; i < rows[r].Count; i++)
            {
                var def = i < rdefs.Count ? rdefs[i] : default;
                // Word's flag form: this cell continues the previous one, so widen that instead.
                if (def.HCont && line.Count > 0)
                {
                    var last = line[^1];
                    int extra = i < bounds.Count ? SpanOf(prev, bounds[i]) : 1;
                    line[^1] = (last.Item1, last.Item2 + Math.Max(1, extra), last.Item3, last.Item4);
                    if (i < bounds.Count) prev = bounds[i];
                    continue;
                }
                int start = CountAtOrBelow(prev);
                int span = i < bounds.Count ? SpanOf(prev, bounds[i]) : 1;
                if (start >= grid.Count) break;                     // more cells than the grid describes
                span = Math.Max(1, Math.Min(span, grid.Count - start));
                line.Add((start, span, rows[r][i], def));
                if (i < bounds.Count) prev = bounds[i];
            }
            placed.Add(line);
        }

        int cols = grid.Count;
        var tb = new TableBlock(rows.Count, cols);
        tb.Cells.Clear();
        tb.ColSpans.Clear();
        tb.RowSpans.Clear();
        for (int r = 0; r < rows.Count; r++)
        {
            var cells = new List<TableCell>(cols);
            for (int c = 0; c < cols; c++) cells.Add(new TableCell());
            foreach (var (start, _, cell, _) in placed[r]) cells[start] = cell;
            tb.Cells.Add(cells);
            var cs = new List<int>(cols); var rs = new List<int>(cols);
            for (int c = 0; c < cols; c++) { cs.Add(1); rs.Add(1); }
            tb.ColSpans.Add(cs); tb.RowSpans.Add(rs);
        }
        tb.Rows = rows.Count;
        tb.Columns = cols;

        tb.ColumnWidths.Clear();
        for (int c = 0; c < cols; c++)
        {
            double wpx = (grid[c] - (c == 0 ? 0 : grid[c - 1])) / 15.0;
            tb.ColumnWidths.Add(wpx >= 16 ? wpx : 100);
        }

        // Cell properties, then the spans. Vertical merge has no geometry — it stays on the flags.
        foreach (var line in placed)
            foreach (var (start, _, cell, def) in line)
            {
                cell.VerticalAlignment = def.VAlign;
                if (def.Shading > 0 && def.Shading < _colors.Count)
                {
                    var shade = _colors[def.Shading];
                    if (shade.A != 0) cell.Background = shade;
                }
            }
        for (int r = 0; r < placed.Count; r++)
            foreach (var (start, span, _, def) in placed[r])
            {
                if (def.VCont) continue; // covered from above; its anchor stamps it
                int rspan = 1;
                for (int r2 = r + 1; r2 < placed.Count; r2++)
                {
                    var below = placed[r2].Find(e => e.start == start);
                    if (below.cell == null || !below.def.VCont) break;
                    rspan++;
                }
                if (span > 1 || rspan > 1) tb.SetSpan(r, start, span, rspan);
            }
        return tb;

        // Grid columns fully inside (from, to].
        int SpanOf(int from, int to)
        {
            int n = 0;
            foreach (int b in grid) if (b > from && b <= to) n++;
            return Math.Max(1, n);
        }
        int CountAtOrBelow(int v)
        {
            int n = 0;
            foreach (int b in grid) if (b <= v) n++;
            return n;
        }
    }

    // Builds a rectangular TableBlock from accumulated rows. Shared by the top-level table (FinalizeTable,
    // which then applies the merge flags) and by nested tables, whose \cellx widths live in an ignorable
    // group we don't read — those pass cellx: null and come out at the default column width.
    private static TableBlock? BuildTable(List<List<TableCell>> rows, List<int>? cellx)
    {
        if (rows.Count == 0) return null;
        int cols = 0;
        foreach (var r in rows) if (r.Count > cols) cols = r.Count;
        if (cols == 0) return null;

        var tb = new TableBlock(rows.Count, cols);
        tb.Cells.Clear();
        foreach (var r in rows)
        {
            var cells = new List<TableCell>(cols);
            for (int c = 0; c < cols; c++) cells.Add(c < r.Count ? r[c] : new TableCell());
            tb.Cells.Add(cells);
        }
        tb.Rows = rows.Count;
        tb.Columns = cols;
        if (cellx != null && cellx.Count > 0)
        {
            tb.ColumnWidths.Clear();
            int prev = 0;
            for (int c = 0; c < cols; c++)
            {
                int boundary = c < cellx.Count ? cellx[c] : prev + 1500;
                double wpx = (boundary - prev) / 15.0;
                tb.ColumnWidths.Add(wpx >= 16 ? wpx : 100);
                prev = boundary;
            }
        }
        tb.ColSpans.Clear();
        tb.RowSpans.Clear();
        for (int r = 0; r < rows.Count; r++)
        {
            var cs = new List<int>(cols);
            var rs = new List<int>(cols);
            for (int c = 0; c < cols; c++) { cs.Add(1); rs.Add(1); }
            tb.ColSpans.Add(cs);
            tb.RowSpans.Add(rs);
        }
        return tb;
    }

    // The cell that just ended: the paragraphs it collected, the table nested one level deeper (if any),
    // and the paragraph currently being filled — in the order they appeared.
    private TableCell TakeCell(int childDepth)
    {
        FlushRun();
        var cell = new TableCell(_para);
        _para = new Paragraph();

        // A nested row left open (no \nestrow seen, e.g. truncated input) still counts.
        int saved = _itap;
        _itap = childDepth;
        EndNestedRow();
        _itap = saved;

        int at = 0;
        if (_cellPending.TryGetValue(childDepth - 1, out var pending))
        {
            _cellPending.Remove(childDepth - 1);
            foreach (var b in pending) { b.Parent = cell; cell.Blocks.Insert(at++, b); }
        }
        if (_nestRows.TryGetValue(childDepth, out var rows))
        {
            _nestRows.Remove(childDepth);
            if (BuildTable(rows, null) is { } inner) { inner.Parent = cell; cell.Blocks.Insert(at, inner); }
        }
        return cell;
    }

    // Drops one trailing '\n' from a paragraph, and the run that held it if it becomes empty.
    private static void TrimOneTrailingNewline(Paragraph p)
    {
        for (int i = p.Inlines.Count - 1; i >= 0; i--)
        {
            if (p.Inlines[i] is not Run r) return;              // an object inline ends it; nothing to trim
            if (string.IsNullOrEmpty(r.Text)) continue;          // skip empties and keep looking back
            if (r.Text[^1] != '\n') return;
            r.Text = r.Text[..^1];
            if (r.Text.Length == 0) p.Inlines.RemoveAt(i);
            return;
        }
    }

    // \itap<N> switches nesting depth. Going deeper closes the text collected so far as a paragraph of
    // the cell being filled, so "text, then a nested table" keeps that order instead of the text being
    // swallowed into the nested table's first cell.
    private void SetItap(int depth)
    {
        if (depth == _itap) return;
        if (depth > _itap)
        {
            FlushRun();
            // The writer MUST close this paragraph with \par before descending, or Word glues its text
            // onto the nested table's first cell — so that break is the paragraph boundary itself, not
            // content. Inside a cell \par is otherwise read as a newline, which made the separator come
            // back as text: one more '\n' before the nested table on every save/load cycle.
            TrimOneTrailingNewline(_para);
            if (_para.Inlines.Count > 0)
            {
                // The paragraph belongs to the cell being filled at the CURRENT depth, not the deeper one.
                if (!_cellPending.TryGetValue(_itap, out var pending))
                    _cellPending[_itap] = pending = new List<Block>();
                pending.Add(_para);
                _para = new Paragraph();
            }
        }
        _itap = depth;
    }

    private void FinalizeTable()
    {
        var rows = _tableRows;
        var cellx = _tableCellx;
        var rowCellx = _tableRowCellx;
        var defs = _tableRowDefs;
        _tableRows = null;
        _curRow = null;
        _tableCellx = null;
        _tableRowCellx = null;
        _tableRowDefs = null;
        bool inlineMark = _nextTableInline;
        bool opensHost = _inlineTableOpensHost;
        _nextTableInline = false;
        _inlineTableOpensHost = false;
        // A top-level table is done, so nothing nested inside it can still be pending: every nested row is
        // consumed by TakeCell when the cell one level up closes. Anything left is orphaned by truncated
        // or malformed input — and left in place it was picked up by the NEXT table's first cell, which
        // made a nested table teleport into an unrelated table further down the document.
        _nestRow.Clear();
        _nestRows.Clear();
        _cellPending.Clear();
        _itap = 1;
        if (rows == null) return;
        if ((BuildTableFromGeometry(rows, rowCellx, defs) ?? BuildTable(rows, cellx)) is not { } tb) return;

        // Marked as an inline table by our own writer ({\*\arinline}): put it back on the preceding
        // paragraph's line and continue that paragraph, so "text, table, more text" is one line again
        // instead of three blocks. The host paragraph was closed by the \par the writer emits before it.
        if (inlineMark && (opensHost || (_doc.Blocks.Count > 0 && _doc.Blocks[^1] is Paragraph)))
        {
            // Reuse the paragraph the writer closed just before the table; when the table OPENED its host
            // there is no such paragraph and a fresh one is the host.
            Paragraph host;
            if (opensHost) host = new Paragraph();
            else { host = (Paragraph)_doc.Blocks[^1]; _doc.Blocks.RemoveAt(_doc.Blocks.Count - 1); }
            var it = new InlineTable { Table = tb, Parent = host };
            host.Inlines.Add(it);
            // Whatever the current paragraph has collected is the text that followed the table.
            foreach (var inl in new List<Inline>(_para.Inlines))
            {
                _para.Inlines.Remove(inl);
                inl.Parent = host;
                host.Inlines.Add(inl);
            }
            _para = host; // the caller adds it back
            return;
        }
        _doc.Blocks.Add(tb);
    }

    // ---- images ----

    // Places the accumulated \pict bytes: small (<64px) inline, larger as its own block. Twips ??px is
    // /15. Natural size (when \picwgoal/\pichgoal absent) is read from the encoded header (no GPU decode).
    private void FinalizePict()
    {
        var hex = _pictHex.ToString();
        _pictHex.Clear();
        if (hex.Length < 8) return;
        var bytes = HexToBytes(hex);
        if (bytes == null || bytes.Length == 0) return;

        // \dibitmap carries a header-less DIB — prepend a BITMAPFILEHEADER so it decodes as BMP.
        if (_pictMime == "dib") { bytes = WrapDibAsBmp(bytes); if (bytes == null) return; _pictMime = "image/bmp"; }
        // No recognized blip keyword (\pngblip/\jpegblip): sniff the bytes — some writers omit or use
        // other words but still embed a decodable image. Genuinely unsupported formats (WMF/EMF) fail
        // the sniff and are dropped here (the paste path then prefers the HTML flavor's images).
        else if (_pictMime == null)
        {
            var (sw, sh) = ImageInfo.GetPixelSize(bytes);
            if (sw <= 0 || sh <= 0) return;
            _pictMime = ImageMime.Detect(bytes);
        }

        double w = _pictWTwips > 0 ? _pictWTwips / 15.0 : 0;
        double h = _pictHTwips > 0 ? _pictHTwips / 15.0 : 0;
        if (w <= 0 || h <= 0)
        {
            var (nw, nh) = ImageInfo.GetPixelSize(bytes);
            if (nw <= 0 || nh <= 0) return; // not a decodable/known PNG/JPEG after all
            w = nw; h = nh;
        }
        string mime = ImageMime.Detect(bytes);

        if (w < 64 && h < 64)
        {
            var img = new InlineImage { Width = w, Height = h };
            img.SetImageData(bytes, mime);
            _para.Inlines.Add(img);
        }
        else if (_curRow != null)
        {
            // Inside a table row the document body is the wrong destination: _para is the CELL's
            // paragraph (EndParagraph suppresses flushing while a row is open), so emitting a block here
            // made the picture escape its cell and land in the body, out of document order. Keep it in
            // the cell as an inline image at the same size.
            var cellImg = new InlineImage { Width = w, Height = h };
            cellImg.SetImageData(bytes, mime);
            _para.Inlines.Add(cellImg);
        }
        else
        {
            // A table is not appended to the document until FinalizeTable runs, and until then it is
            // only pending rows — so a picture that follows `\row` would be added FIRST and end up
            // ahead of the table it came after. Every other block append goes through EndParagraph,
            // which finalizes; this one has to do the same.
            FinalizeTable();
            if (_para.Inlines.Count > 0) { _doc.Blocks.Add(_para); _para = new Paragraph(); }
            var ib = new ImageBlock { Width = w, Height = h };
            ib.SetImageData(bytes, mime);
            _doc.Blocks.Add(ib);
            _imageOwnsNextPar = true;
        }
    }

    // Prepends the 14-byte BITMAPFILEHEADER a raw DIB lacks (offset = header + palette), so WIC/our
    // sniffing see a standard BMP. Returns null when the blob doesn't start with a known info header.
    private static byte[]? WrapDibAsBmp(byte[] dib)
    {
        if (dib.Length < 40) return null;
        int hdr = BitConverter.ToInt32(dib, 0);
        if (hdr != 40 && hdr != 108 && hdr != 124) return null; // BITMAPINFO/V4/V5 header sizes
        int bitCount = BitConverter.ToUInt16(dib, 14);
        int compression = BitConverter.ToInt32(dib, 16);
        int clrUsed = BitConverter.ToInt32(dib, 32);
        int palette = bitCount <= 8 ? (clrUsed > 0 ? clrUsed : 1 << bitCount) * 4 : 0;
        int masks = hdr == 40 && compression == 3 ? 12 : 0; // BI_BITFIELDS masks follow a plain header
        int offset = 14 + hdr + masks + palette;
        var bmp = new byte[14 + dib.Length];
        bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
        BitConverter.GetBytes(bmp.Length).CopyTo(bmp, 2);
        BitConverter.GetBytes(offset).CopyTo(bmp, 10);
        dib.CopyTo(bmp, 14);
        return bmp;
    }

    private static byte[]? HexToBytes(string hex)
    {
        if ((hex.Length & 1) != 0) hex = hex.Substring(0, hex.Length - 1);
        // The collector only accepts hex digits (Uri.IsHexDigit), so this can't realistically throw;
        // the catch keeps the old "null on bad input" contract. FromHexString beats the former
        // per-2-chars byte.TryParse loop on multi-megabyte pasted pictures.
        try { return Convert.FromHexString(hex); }
        catch (FormatException ex) { RichEditorDiagnostics.Report(ex); return null; }
    }

    private static Encoding GetEncoding(int codepage)
    {
        try { return Encoding.GetEncoding(codepage); }
        catch (Exception ex) { RichEditorDiagnostics.Report(ex); return Encoding.Latin1; }
    }
}

// Serializes a FlowDocument to RTF ??the inverse of RtfParser, covering the same subset.
internal sealed class RtfWriter
{
    private readonly StringBuilder _body = new();
    private readonly List<string> _fonts = new() { "" };  // \f0 = default
    private readonly List<Color> _colors = new();          // \colortbl entry 0 is "auto"; these are 1-based
    private readonly Dictionary<string, int> _fontIndex = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, int> _colorIndex = new();

    public string Build(FlowDocument doc)
    {
        // Per-ListLevel ordered counters, mirroring the editor's rendering (RichEditor.EnsureBlockLayout):
        // nested ordered lists number 1,2,… per level instead of continuing the parent's count; bullets
        // keep the context alive, non-list blocks reset it.
        var ordCounters = new List<int>();
        // Pictures are written as hex — 2 chars per byte — and dominate the output. Sized up front, the body
        // is one allocation of that payload instead of a chain of doubling re-allocations (measured
        // 2026-09-16, CopyAllocationProbeTests: one 10 MB picture allocated 160 MB writing RTF).
        long hexChars = 4096;
        foreach (var bytes in BlockWalk.PictureBytes(doc.Blocks)) hexChars += bytes.Length * 2L + 128;
        _body.EnsureCapacity((int)Math.Min(hexChars, int.MaxValue / 2));
        foreach (var block in doc.Blocks)
        {
            int ordered = 0;
            if (block is Paragraph { IsListItem: true } p)
            {
                int lvl = Math.Clamp(p.ListLevel, 0, 16);
                if (ordCounters.Count > lvl + 1) ordCounters.RemoveRange(lvl + 1, ordCounters.Count - lvl - 1);
                if (p.ListType == ListKind.Ordered)
                {
                    while (ordCounters.Count <= lvl) ordCounters.Add(0);
                    ordered = ++ordCounters[lvl];
                }
            }
            else ordCounters.Clear();
            WriteBlock(block, ordered);
        }

        var sb = new StringBuilder();
        // \uc1: each \uN Unicode escape is followed by exactly one fallback character (the '?' WriteEscaped
        // emits). It is the default, but readers that saw a different \ucN earlier in their session have
        // been known to carry it over — stating it removes the ambiguity for a two-byte cost.
        sb.Append(@"{\rtf1\ansi\ansicpg1252\uc1\deff0");
        sb.Append(@"{\fonttbl");
        for (int i = 0; i < _fonts.Count; i++)
            sb.Append($@"{{\f{i}\fnil ").Append(EscapeText(_fonts[i].Length == 0 ? "Default" : _fonts[i])).Append(";}");
        sb.Append('}');
        sb.Append(@"{\colortbl;");
        foreach (var c in _colors) sb.Append($@"\red{c.R}\green{c.G}\blue{c.B};");
        sb.Append('}').Append('\n');
        // The header/footer destinations belong to the document area, BEFORE the body — Word puts them
        // right after the tables. Nothing wrote them before, so a document with a header exported to Word
        // or HWP simply lost it, while the model and .flow carried it correctly (the same one-sided gap
        // this round found for paragraph fills).
        WritePageChrome(sb, doc.PageSetup);
        // The header is small and the body holds the pictures: build the result straight from both, rather
        // than appending the body into the header's builder (a whole copy) and then calling ToString (another).
        return string.Create(sb.Length + _body.Length + 1, (sb, _body), static (dest, parts) =>
        {
            parts.sb.CopyTo(0, dest, parts.sb.Length);
            int at = parts.sb.Length;
            foreach (var chunk in parts._body.GetChunks())
            {
                chunk.Span.CopyTo(dest[at..]);
                at += chunk.Length;
            }
            dest[at] = '}';
        });
    }

    private void WriteBlock(Block block, int ordered)
    {
        switch (block)
        {
            case Paragraph p: WriteParagraph(p, ordered); break;
            case TableBlock tb: WriteTable(tb); break;
            case ImageBlock ib when PictBytes(ib.RawBytes, ib.MimeType, ib.Image) is { } pic:
                _body.Append(@"\pard ");
                WritePict(pic.bytes, pic.mime, ib.Width, ib.Height);
                _body.Append(@"\par").Append('\n');
                break;
            case DividerBlock:
                _body.Append(@"\pard\brdrb\brdrs\brdrw10\brsp20 \par").Append('\n');
                break;
        }
    }

    // {\header …} / {\footer …} — the page chrome the editor draws in the margin bands. Written into the
    // document area before the body, which is where Word puts them and where readers look.
    //
    // The footer carries the page number when the document asks for it, at a RIGHT TAB STOP on the content
    // edge — that is how the editor draws it (footer text left, "3 / 12" right), and without the explicit
    // stop the number would land on whatever default tab the reader happens to have. The width comes from
    // the same page-geometry table the control lays out with, so the two cannot disagree.
    private void WritePageChrome(StringBuilder sb, PageSetup? ps)
    {
        if (ps == null) return;
        bool hasHeader = !string.IsNullOrEmpty(ps.Header);
        bool hasFooter = !string.IsNullOrEmpty(ps.Footer) || ps.ShowPageNumbers;

        // Paper size and margins (ported from upstream, 2026-09-24): without them a document set to A4 arrived on
        // whatever paper the reader defaults to (Letter in a US install). Continuous has no paper to state.
        if (ps.PageSize != RichEditorPageSize.Continuous)
        {
            var (pw, ph) = PageSetup.PaperDips(ps.PageSize, ps.Orientation);
            sb.Append($@"\paperw{(int)Math.Round(pw * 15)}\paperh{(int)Math.Round(ph * 15)}");
            // Margins are millimetres; RTF wants twips (1440 per inch).
            sb.Append($@"\margl{PageSetup.MmToTwips(ps.Margin.Left)}\margr{PageSetup.MmToTwips(ps.Margin.Right)}");
            sb.Append($@"\margt{PageSetup.MmToTwips(ps.Margin.Top)}\margb{PageSetup.MmToTwips(ps.Margin.Bottom)}");
            if (ps.Orientation == RichEditorPageOrientation.Landscape) sb.Append(@"\landscape");
            // The same numbers again at SECTION level: Word reads the document-level ones, HWP only these.
            sb.Append($@"\sectd\pgwsxn{(int)Math.Round(pw * 15)}\pghsxn{(int)Math.Round(ph * 15)}");
            sb.Append($@"\marglsxn{PageSetup.MmToTwips(ps.Margin.Left)}\margrsxn{PageSetup.MmToTwips(ps.Margin.Right)}");
            sb.Append($@"\margtsxn{PageSetup.MmToTwips(ps.Margin.Top)}\margbsxn{PageSetup.MmToTwips(ps.Margin.Bottom)}");
            if (ps.Orientation == RichEditorPageOrientation.Landscape) sb.Append(@"\lndscpsxn");
            sb.Append('\n');
        }

        if (!hasHeader && !hasFooter) return;

        // The writer emits into _body; borrow it so WriteEscaped can be reused, then move the result.
        int mark = _body.Length;

        if (hasHeader)
        {
            _body.Append(@"{\header\pard\plain\ql ");
            WriteEscaped(ps.Header!);
            _body.Append(@"\par}").Append('\n');
        }
        if (hasFooter)
        {
            var (w, _) = PageSetup.PaperDips(ps.PageSize, ps.Orientation);
            int contentTwips = (int)Math.Round(w * 15) - PageSetup.MmToTwips(ps.Margin.Left) - PageSetup.MmToTwips(ps.Margin.Right);
            _body.Append(@"{\footer\pard\plain\ql");
            if (ps.ShowPageNumbers) _body.Append(@"\tqr\tx").Append(contentTwips);
            _body.Append(' ');
            if (!string.IsNullOrEmpty(ps.Footer)) WriteEscaped(ps.Footer!);
            if (ps.ShowPageNumbers)
            {
                // \chpgn is the current page; the total needs a field, which is what Word writes. Both are
                // read back by this parser as "the document wanted page numbers" rather than as text.
                _body.Append(@"\tab \chpgn / {\field{\*\fldinst NUMPAGES}{\fldrslt }}");
            }
            _body.Append(@"\par}").Append('\n');
        }

        sb.Append(_body, mark, _body.Length - mark);
        _body.Length = mark;
    }

    // "\pard" + this paragraph's own properties. Factored out because a paragraph that hosts an inline
    // table is emitted as several RTF paragraphs (see WriteParagraph) and each one must restate them.
    private void WriteParagraphProps(Paragraph p)
    {
        _body.Append(@"\pard");
        WriteParagraphPropsBody(p); // ends with the delimiter space for the last control word
    }

    // The properties themselves, without the \pard. Split out because a CELL paragraph opens with
    // `\pard\intbl\itapN` and used to follow it with a hardcoded `\ql` — so a centred, indented or
    // custom-spaced paragraph inside a table cell exported as none of that, while the same paragraph at
    // the top level exported correctly. Not a limitation of RTF: the top-level path has always written
    // these, and the cell path simply never called it. (Reported from a Word/HWP paste: "문단 정렬이
    // 들어오지 않음". The attribute matrix had recorded the loss and mis-filed it as a format limit,
    // because it only compared RTF against RTF and both cells looked equally lossy.)
    private void WriteParagraphPropsBody(Paragraph p)
    {
        // ALWAYS emit the alignment, including \ql for left. In the spec \pard resets alignment to left,
        // but HWP treats \pard as "back to the current defaults" and keeps a previously seen \qr — so a
        // single right-aligned paragraph turned every following one right-aligned on paste. Being explicit
        // costs 3 bytes per paragraph and removes the reader-dependent behaviour entirely.
        switch (p.TextAlignment)
        {
            case TextAlignment.Center: _body.Append(@"\qc"); break;
            case TextAlignment.Right: _body.Append(@"\qr"); break;
            case TextAlignment.Justify: _body.Append(@"\qj"); break;
            default: _body.Append(@"\ql"); break;
        }
        // A list item needs a real hanging indent, not just a marker followed by \tab. Without one the tab
        // lands on the reader's next DEFAULT tab stop — in HWP that is far to the right, so the text was
        // thrown across the line while the marker sat alone at the margin (reported from a paste).
        // \fi-360 hangs the marker, \li puts the text at the gutter, \tx pins the tab there. This is what
        // Word writes for its own lists.
        //
        // The gutter is added INTO \li, and our reader reads \li as the author's indent — so the marker
        // tag carries the level and the reader subtracts the gutter back out (see StartMarkerText).
        int indentTwips = (int)(p.Indent * 15);
        if (p.IsListItem)
        {
            int gutter = RtfDocumentFormatter.ListGutterTwips(p.ListLevel);
            _body.Append($@"\fi-360\li{indentTwips + gutter}\tx{gutter}");
        }
        else if (indentTwips > 0) _body.Append($@"\li{indentTwips}");
        // Paragraph shading, through the colour table like every other colour. Nothing wrote it before, so
        // a paragraph fill set in the editor reached no RTF consumer at all (reported from Word and HWP
        // pastes) — while the HTML flavour carried it correctly, which is what made the gap one-sided.
        // blackIsDefault: false for the same reason cell shading passes it — a black fill is a choice, and
        // index 0 means "no shading" here.
        int paraShade = ColorIndex(p.Background, blackIsDefault: false);
        if (paraShade > 0) _body.Append($@"\cbpat{paraShade}");
        // Line spacing (see the parser's \sl/\slmult cases): proportional = \slmult1, absolute = negative
        // twips ("exactly") with \slmult0. LineSpacing is HWP's ratio of the font size while \slmult is
        // Word's multiple of the natural line (≈1.2 × the size), so N = ratio × 240 / 1.2 = ratio × 200.
        //
        // The unset case is stated EXPLICITLY as the HWP default 160% (\sl320), then tagged as "this was
        // the default". Writing nothing means "use the reader's default" — Word's is single — so the text
        // would arrive tighter than the editor drew it.
        //
        // The tag keeps a round trip from turning unset into an explicit 1.6, so the paragraph keeps
        // following the default. Other readers ignore the ignorable group and just get 160%.
        bool defaulted = false;
        if (!double.IsNaN(p.LineSpacing) && p.LineSpacing > 0)
            _body.Append($@"\sl{(int)Math.Round(p.LineSpacing * 200)}\slmult1");
        else if (!double.IsNaN(p.LineHeight) && p.LineHeight > 0)
            _body.Append($@"\sl-{(int)Math.Round(p.LineHeight * 15)}\slmult0");
        else
        {
            _body.Append(@"\sl320\slmult1");
            defaulted = true;
        }
        // The delimiter for the last CONTROL WORD goes here, before the tag. A space after a group's `}`
        // is not a delimiter — it is content, and it showed up as a leading space in the paragraph the
        // first time this tag was appended before it.
        _body.Append(' ');
        if (defaulted) _body.Append(@"{\*\arsl}");
    }

    // A list item's marker, in the legacy RTF list representation Word itself writes: the literal
    // fallback text inside {\pntext …}, plus a {\*\pn …} definition of the list.
    //
    // It used to be written as BARE TEXT followed by \tab, and that injected the glyph into the document's
    // content on the way back in — a bulleted item reopened as the plain text "•\t항목", list gone, bullet
    // now part of what the user typed. Save as .rtf and reopen and every list item had grown a "•<tab>"
    // prefix. No round-trip fuzz could see it, because the result is PERFECTLY IDEMPOTENT: cycle 2 reads
    // back exactly what cycle 1 produced, and the marker is only written for a paragraph that still has a
    // ListType — which this one no longer has. (Found by a human pasting into HWP and noticing a stray ■.)
    //
    // Both groups are ones our reader already drops: `pntext` is in its skip list, and `{\*\pn}` is an
    // ignorable destination. So the fallback text stays available to other readers and stays out of our
    // content, which is exactly the split the bare form could not express.
    private void WriteListMarker(Paragraph p, int ordered)
    {
        // `{\*\armkb<code>}` / `{\*\armkn<code>}` — ours, ignorable, so every other reader skips it. It
        // says: the text that follows, up to and including the next \tab, is the MARKER, not content —
        // and the code carries the exact style (b = bullet, n = numbered), which is more than any standard
        // construct expresses.
        //
        // Why not the standard `{\pntext …}{\*\pn …}` pair (which this did emit for one commit): HWP skips
        // BOTH of them, so the markers vanished from HWP entirely — measured from a real paste. Word does
        // honour `{\*\pn}` and made real lists from it, but a bullet that disappears in one of the two
        // targets is worse than a bullet that is literal text in both. The literal text is what every
        // reader can render; the tag is what keeps it out of OUR content. (Reading `{\*\pn}` is still
        // wired up, because WORD writes it and that gives an incoming Word list its ListType.)
        string marker = ListMarkers.Text(p.ListType, p.ListMarker, ordered);
        // The nesting level, ahead of the marker tag: it tells the reader how much gutter the \li above
        // included so it can be taken back out, and it restores ListLevel, which RTF otherwise loses.
        _body.Append(@"{\*\arlvl").Append(Math.Clamp(p.ListLevel, 0, 8)).Append('}');
        _body.Append(p.ListType == ListKind.Bullet ? @"{\*\armkb" : @"{\*\armkn")
             .Append(RtfDocumentFormatter.MarkerCode(p.ListMarker)).Append('}');
        WriteEscaped(marker);
        _body.Append(@"\tab ");
    }


    private void WriteParagraph(Paragraph p, int ordered)
    {
        WriteParagraphProps(p);

        // Whether anything has been written into the paragraph currently open. Only a paragraph with
        // content needs closing with \par before a table interrupts it; without this an inline table that
        // is the FIRST thing in its host paragraph — the ordinary "글자처럼 취급" shape, where the table is
        // all the paragraph holds — emitted a \par against an empty paragraph and Word/HWP showed a blank
        // line above the table. (The empty paragraph AFTER a table is different: RTF requires one.)
        bool wrote = false;

        if (p.ListType != ListKind.None)
        {
            WriteListMarker(p, ordered);
            wrote = true;
        }

        foreach (var inline in p.Inlines)
        {
            if (inline is Run r && !string.IsNullOrEmpty(r.Text)) { WriteRun(r); wrote = true; }
            else if (inline is InlineImage img && PictBytes(img.RawBytes, img.MimeType, img.Image) is { } ipic)
            { WritePict(ipic.bytes, ipic.mime, img.Width, img.Height); wrote = true; }
            else if (inline is InlineTable itbl)
            {
                // An INLINE table ("treat as character") lives in the paragraph's text flow, but RTF has
                // no inline grid: the only way to keep it a TABLE is to close this paragraph, emit real
                // rows, and reopen the paragraph for whatever follows — which is exactly what Word does
                // for a table between two runs of text. Flattening it to tab-separated text (the first
                // attempt) kept the words but lost the grid, which reads as "the table disappeared".
                // A trailing empty paragraph is deliberate: RTF requires a paragraph after a table.
                bool opensHost = !wrote; // nothing preceded it, so no \par closes a host paragraph below
                if (wrote) _body.Append(@"\par").Append('\n');
                // Ours, and deliberately an ignorable destination: {\*\...} groups are skipped by
                // definition, so every other reader still sees exactly the block-level table it saw
                // before. Our own reader takes the marker and puts the table back on the text line, so
                // RTF joins .flow/JSON/HTML in round-tripping an inline table through our save/load.
                // The parameter says whether the table OPENS its host paragraph: without it the reader
                // cannot tell that shape from "text, table, more text" and reattaches the table to the
                // PREVIOUS paragraph, swallowing it.
                _body.Append(opensHost ? @"{\*\arinline1}" : @"{\*\arinline0}");
                WriteTable(itbl.Table);   // ends with \pard\plain
                WriteParagraphProps(p);
                wrote = false;            // the reopened paragraph starts empty again
                continue;
            }
        }
        _body.Append(@"\par").Append('\n');
    }

    // A heading's bold and size are on its runs (HeadingStyle), so a run is written as it is. This used to add
    // \b to every heading run and the heading size to unsized ones, mirroring the renderer that forced them —
    // which would now re-bold heading text the user un-bolded.
    private void WriteRun(Run r)
    {
        // Hyperlink as a real field: {\field{\*\fldinst{HYPERLINK "url"}}{\fldrslt {...}}} — Word/HWP
        // then keep the target address (the bare \ul form only looked like a link).
        bool link = !string.IsNullOrEmpty(r.NavigateUri);
        if (link)
            _body.Append(@"{\field{\*\fldinst{HYPERLINK """).Append(EscapeText(r.NavigateUri!)).Append("\"}}{\\fldrslt ");
        _body.Append('{');
        if (r.FontWeight.IsBold()) _body.Append(@"\b");
        if (r.FontStyle == FontStyle.Italic) _body.Append(@"\i");
        if (r.TextDecorations.HasFlag(TextDecorationFlags.Underline) || !string.IsNullOrEmpty(r.NavigateUri)) _body.Append(@"\ul");
        if (r.TextDecorations.HasFlag(TextDecorationFlags.Strikethrough)) _body.Append(@"\strike");
        int f = FontIndex(r.FontFamily);
        if (f > 0) _body.Append($@"\f{f}");
        double size = r.FontSize <= 0 ? 10 : r.FontSize; // pt; body default
        _body.Append($@"\fs{(int)Math.Round(size * 2)}"); // \fs is half-points
        int c = ColorIndex(r.Foreground);
        if (c > 0) _body.Append($@"\cf{c}");
        int hb = ColorIndex(r.Background, blackIsDefault: false);
        if (hb > 0) _body.Append($@"\highlight{hb}"); // run highlight, round-trips with the parser
        _body.Append(' ');
        WriteEscaped(r.Text!);
        _body.Append('}');
        if (link) _body.Append("}}");
    }

    // depth 1 = a top-level table (\cell/\row); deeper = a table inside a cell, written as Word does with
    // \itapN + \nestcell/\nestrow so it comes back as a real nested table instead of flattened text.
    private void WriteTable(TableBlock tb, int depth = 1)
    {
        for (int row = 0; row < tb.Rows; row++)
        {
            // The row definition (\trowd + every \cellx) is emitted TWICE at the top level: once before
            // the cell text and once again immediately before \row. The spec allows the single form, but
            // Word writes the repeated form and strict readers — HWP among them — need the definition
            // still in scope at \row. Without the repeat HWP dropped the row structure entirely and
            // pasted the cells as plain lines (the "표가 사라져" report).
            string rowDef = BuildRowDefinition(tb, row);

            if (depth == 1) _body.Append(@"\pard").Append(rowDef);
            for (int col = 0; col < tb.Columns; col++)
            {
                var (ar, ac) = tb.AnchorOf(row, col);
                var (cs, _) = tb.SpanOf(ar, ac);
                // One cell per horizontal span — the row definition emits one \cellx for it, and the two
                // must agree or the reader loses the row. (Vertically covered slots still get their own
                // cell; only the horizontal run collapses.)
                if (col != ac + cs - 1) continue;

                // \itapN = nesting depth; modern readers use it to tell table paragraphs from body text.
                _body.Append(@"\pard\intbl\itap").Append(depth);
                // Only the merge anchor carries content; a vertically covered slot emits an empty cell.
                if (ar == row && WriteCellContent(tb.Cells[row][ac], depth))
                {
                    // A nested table leaves \itap at ITS depth, so re-declare this cell's own before
                    // closing — otherwise the reader books this cell into the deeper table.
                    _body.Append(@"\pard\intbl\itap").Append(depth);
                }
                _body.Append(depth == 1 ? @"\cell" : @"\nestcell");
            }
            if (depth == 1)
                _body.Append(rowDef).Append(@"\row").Append('\n');
            else
                // \nesttableprops is ignorable, so a reader that doesn't do nested tables still sees the
                // cell text rather than a corrupt document. \nonesttables is the same fallback for very
                // old readers — our own parser skips it so its \par doesn't land in the parent cell.
                _body.Append(@"{\*\nesttableprops").Append(rowDef).Append(@"\nestrow}{\nonesttables\par}").Append('\n');
        }
        // \plain too: reset the character formatting the last cell left in scope, so body text after the
        // table doesn't inherit it.
        if (depth == 1) _body.Append(@"\pard\plain").Append('\n');
    }

    // Everything a cell can hold: several paragraphs (separated by \par), block images, dividers, and
    // tables — nested ones and the inline tables living in a cell paragraph, both written one \itap
    // deeper. Returns true when it wrote such a table, so the caller can re-declare this cell's depth.
    private bool WriteCellContent(TableCell cell, int depth)
    {
        bool first = true, wroteNested = false;

        // A nested table leaves \itap at ITS depth and consumes the paragraph properties. Anything this
        // cell writes afterwards has to re-open the cell's own level first, or Word books that text into
        // the deeper table and drops it (a paragraph after a nested table vanished entirely).
        // `resume` is the paragraph whose remaining inlines continue after the nested table: \pard threw
        // its properties away, so they have to be restated or the tail of the paragraph loses the
        // alignment/indent/spacing that its head had. Null when a whole block (not a paragraph) follows,
        // because the next paragraph states its own.
        void ReopenCell(Paragraph? resume = null)
        {
            _body.Append(@"\pard\intbl\itap").Append(depth);
            if (resume != null) WriteParagraphPropsBody(resume); // ends with its own delimiter space
            // A fresh paragraph is now open at this cell's level, so the next block writes straight into
            // it rather than prefixing another \par (which would leave a blank line).
            first = true;
        }
        // Terminate whatever this cell has written so far before descending into a nested table. Without
        // it the preceding paragraph's text is not closed and Word glues it onto the first nested cell.
        void CloseBeforeNested()
        {
            if (!first) _body.Append(@"\par ");
            first = false;
        }

        foreach (var blk in cell.Blocks)
        {
            if (blk is Paragraph cpara)
            {
                if (!first) _body.Append(@"\par ");
                first = false;
                // This paragraph's OWN alignment / indent / line spacing. The cell prelude opens with
                // `\pard\intbl\itapN` and used to hardcode `\ql`, so every cell paragraph exported as
                // left-aligned, un-indented and single-spaced no matter what it was — while the identical
                // paragraph at the top level exported correctly. These are paragraph properties in scope
                // until the next \par or \pard, so stating them here is all that was missing.
                WriteParagraphPropsBody(cpara); // ends with its own delimiter space
                if (cpara.ListType != ListKind.None) WriteListMarker(cpara, 1);
                foreach (var inline in cpara.Inlines)
                {
                    if (inline is InlineTable it)
                    {
                        CloseBeforeNested();
                        WriteTable(it.Table, depth + 1);
                        wroteNested = true;
                        ReopenCell(cpara); // the rest of this paragraph belongs to THIS cell, not the inner table
                    }
                    else if (inline is Run r && !string.IsNullOrEmpty(r.Text)) WriteRun(r);
                    else if (inline is InlineImage cii && PictBytes(cii.RawBytes, cii.MimeType, cii.Image) is { } cipic)
                        WritePict(cipic.bytes, cipic.mime, cii.Width, cii.Height);
                }
            }
            else if (blk is TableBlock nested)
            {
                CloseBeforeNested();
                WriteTable(nested, depth + 1);
                wroteNested = true;
                ReopenCell();
            }
            else if (blk is ImageBlock cib && PictBytes(cib.RawBytes, cib.MimeType, cib.Image) is { } cbpic)
            {
                if (!first) _body.Append(@"\par ");
                first = false;
                WritePict(cbpic.bytes, cbpic.mime, cib.Width, cib.Height);
            }
            else if (blk is DividerBlock)
            {
                if (!first) _body.Append(@"\par ");
                first = false; // no per-cell rule in this subset; the break keeps blocks separated
            }
        }
        return wroteNested;
    }

    // "\trowd" + the per-cell properties and \cellx boundaries for one row. Built as a string because
    // WriteTable emits it both before the cells and again before \row (see there). Not static: cell
    // shading has to register its colour in the document's colour table (ColorIndex).
    private string BuildRowDefinition(TableBlock tb, int row)
    {
        var d = new StringBuilder();
        // \trgaph = half the gap between cell text and border; \trleft = row origin. Word always writes
        // both, and readers that default them differently otherwise lay the row out at the wrong offset.
        d.Append(@"\trowd\trgaph108\trleft0");
        // Pin the row's width and turn AUTOFIT OFF. Without this Word stretches a pasted table to the full
        // text column — reported from a real paste — because its default on paste is to fit the window,
        // and the \cellx positions alone do not say "this width is intentional". \trftsWidth3 = the width
        // that follows is absolute twips; \trwWidth is the row's total. Both are written after the loop,
        // which is where the total is known.
        int totalTwips = 0;
        for (int col = 0; col < tb.Columns; col++)
            totalTwips += (col < tb.ColumnWidths.Count ? (int)tb.ColumnWidths[col] : 100) * 15;
        d.Append(@"\trautofit0\trftsWidth3\trwWidth").Append(totalTwips);
        int x = 0;
        for (int col = 0; col < tb.Columns; col++)
        {
            var (ar, ac) = tb.AnchorOf(row, col);
            var (cs, rs) = tb.SpanOf(ar, ac);

            // HORIZONTAL merge is expressed geometrically: the merged span is ONE cell whose \cellx sits
            // at the far edge, so covered columns contribute width but no \cellx of their own. This is the
            // base RTF table model; Word also accepts the flag form (one \cellx per column with
            // \clmgf/\clmrg), but HWP does not — it dissolved the merge and showed the covered columns as
            // separate empty cells. Word reads the geometric form correctly too, so one form serves both.
            // (Writer rule, re-learned: the target is real readers, not the spec.)
            int wpx = col < tb.ColumnWidths.Count ? (int)tb.ColumnWidths[col] : 100;
            x += wpx * 15;
            // Emit at the LAST column of the horizontal span, so \cellx is the merged cell's right edge
            // and it has swallowed every covered column's width.
            if (col != ac + cs - 1) continue;

            // VERTICAL merge has no geometric encoding — every row still has a cell in this column — so it
            // keeps the flag form, which HWP does honour.
            if (rs > 1) d.Append(row == ar ? @"\clvmgf" : @"\clvmrg");
            switch (tb.Cells[ar][ac].VerticalAlignment)
            {
                case CellVerticalAlignment.Center: d.Append(@"\clvertalc"); break;
                case CellVerticalAlignment.Bottom: d.Append(@"\clvertalb"); break;
            }
            // Cell shading uses the colour table, like text colour. Without it the cell background set
            // in the editor (right-click ▸ 셀 배경색) was dropped by every RTF consumer — including our
            // own reader, so it didn't even survive our save/load.
            // blackIsDefault: false — a black cell background is a real choice, not "unset" (same reason
            // highlights pass it; index 0 means "no shading" here).
            int bg = ColorIndex(tb.Cells[ar][ac].Background, blackIsDefault: false);
            if (bg > 0) d.Append($@"\clcbpat{bg}");
            // Per-cell borders (single line, ~0.5pt) on all four sides — must precede this cell's
            // \cellx. Without them HWP/Word render the table with no visible lines (transparent grid).
            d.Append(@"\clbrdrt\brdrs\brdrw10\clbrdrl\brdrs\brdrw10\clbrdrb\brdrs\brdrw10\clbrdrr\brdrs\brdrw10");
            d.Append($@"\cellx{x}");
        }
        return d.ToString();
    }

    // {\*\shppict{\pict ...}} ??the modern wrapper our parser un-skips; bytes go out as hex, size in twips.
    // The bytes to write for an image element. RawBytes are the source of truth; an image that was set
    // as a decoded bitmap instead (the `Image` setter clears RawBytes by design) is PNG-encoded — the
    // same fallback DocumentSerializer.PoolImage and HtmlDocumentFormatter.ImgTag have always applied.
    // RTF was the one writer missing it, so such a picture vanished from .rtf saves AND from every
    // clipboard copy, since the RTF flavour is the one Word and HWP prefer.
    private static (byte[] bytes, string? mime)? PictBytes(byte[]? rawBytes, string? mime, Microsoft.Graphics.Canvas.CanvasBitmap? bmp)
    {
        if (rawBytes != null) return (rawBytes, mime);
        var encoded = ImageEncoder.ToPngBytes(bmp);
        return encoded == null ? null : (encoded, "image/png");
    }

    private void WritePict(byte[] bytes, string? mime, double w, double h)
    {
        _body.Append(@"{\*\shppict{\pict");
        _body.Append(mime != null && mime.Contains("jpeg", StringComparison.OrdinalIgnoreCase) ? @"\jpegblip" : @"\pngblip");
        // Natural pixel size (\picw/\pich) alongside the display goals: some consumers (HWP notably)
        // ignore or misplace a \pict that only carries goals.
        var (nw, nh) = ImageInfo.GetPixelSize(bytes);
        if (nw > 0 && nh > 0) _body.Append($@"\picw{(int)nw}\pich{(int)nh}");
        if (w > 0) _body.Append($@"\picwgoal{(int)(w * 15)}");
        if (h > 0) _body.Append($@"\pichgoal{(int)(h * 15)}");
        _body.Append(' ');
        // This runs on EVERY copy that contains an image (SetClipboardFromSelection writes RTF). A per-byte
        // ToString("x2") loop once allocated a string per byte; one ToHexStringLower after it still made a
        // string of the whole payload before appending it.
        AppendHexLower(_body, bytes);
        _body.Append("}}");
    }

    // Hex straight into the builder, a slice at a time: no string of the whole payload is ever made.
    private static void AppendHexLower(StringBuilder sb, ReadOnlySpan<byte> data)
    {
        const int SliceBytes = 32 * 1024;
        char[] chars = System.Buffers.ArrayPool<char>.Shared.Rent(SliceBytes * 2);
        try
        {
            while (!data.IsEmpty)
            {
                var slice = data[..Math.Min(SliceBytes, data.Length)];
                Convert.TryToHexStringLower(slice, chars, out int written);
                sb.Append(chars, 0, written);
                data = data[slice.Length..];
            }
        }
        finally { System.Buffers.ArrayPool<char>.Shared.Return(chars); }
    }

    private int FontIndex(string? family)
    {
        if (string.IsNullOrEmpty(family)) return 0;
        if (_fontIndex.TryGetValue(family, out var i)) return i;
        i = _fonts.Count;
        _fonts.Add(family);
        _fontIndex[family] = i;
        return i;
    }

    private int ColorIndex(Color? c, bool blackIsDefault = true)
    {
        if (c is not { } col) return 0;
        // Black is the default text colour ??no \cf needed (matches the model default where a null
        // foreground also renders black). Highlights pass blackIsDefault: false — index 0 means
        // "no highlight" there, so a black highlight must get a real colour-table entry.
        if (blackIsDefault && col.R == 0 && col.G == 0 && col.B == 0) return 0;
        uint key = ((uint)col.R << 16) | ((uint)col.G << 8) | col.B;
        if (_colorIndex.TryGetValue(key, out var i)) return i;
        _colors.Add(col);
        i = _colors.Count; // 1-based: \colortbl entry 0 is the auto colour
        _colorIndex[key] = i;
        return i;
    }

    private void WriteEscaped(string text) => _body.Append(EscapeText(text));

    private static string EscapeText(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char ch in text)
        {
            if (ch == '\\' || ch == '{' || ch == '}') sb.Append('\\').Append(ch);
            else if (ch == '\n') sb.Append(@"\line ");
            else if (ch == '\r') { /* skip */ }
            else if (ch < 128) sb.Append(ch);
            else { int code = ch > 0x7FFF ? ch - 0x10000 : ch; sb.Append(@"\u").Append(code.ToString(CultureInfo.InvariantCulture)).Append('?'); }
        }
        return sb.ToString();
    }
}
