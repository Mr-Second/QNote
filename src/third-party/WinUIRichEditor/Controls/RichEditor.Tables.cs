using System;
using System.Collections.Generic;
using Windows.Foundation;
using Microsoft.Graphics.Canvas;
using WinUIRichEditor.Documents;

namespace WinUIRichEditor.Controls;

// Phase 2b: recursive table geometry (LayoutTable) + read-only table rendering. The same geometry
// primitive will feed hit-testing in Phase 3 (shared single source of truth, like the Avalonia original).
public partial class RichEditor
{
    // Inset between a table cell border and its content.
    private const double CellPad = 5;
    private const double InlineTablePad = 2;

    private readonly struct TableLayout
    {
        public readonly double[] ColX;   // length Columns+1: left edge of each column + right end
        public readonly double[] RowY;   // length Rows+1: top edge of each row + bottom end
        public readonly double TableWidth;
        public readonly double TotalHeight;
        public readonly List<(int r, int c, Rect rect)> AnchorRects;
        public TableLayout(double[] colX, double[] rowY, double w, double h, List<(int, int, Rect)> anchors)
        { ColX = colX; RowY = rowY; TableWidth = w; TotalHeight = h; AnchorRects = anchors; }
    }

    // Measured row heights per table (position-independent — depends only on content + column widths),
    // cached by table identity. Cleared with the layout cache on any document/format change.
    // Weak-keyed for the reason spelled out on _heightCache: a table dropped from the document must not
    // be pinned by its measured row heights.
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<TableBlock, double[]> _tableRowHeights = new();

    // Build-or-return a paragraph's rendered height at a wrap width (single source for measure + draw).
    private double ParagraphHeight(Paragraph p, double width)
    {
        long sig = ParagraphSig(p);
        if (_heightCache.TryGetValue(p, out var hc) && hc.Sig == sig && hc.Width == width)
            return hc.Height;
        // Pagination measures the same paragraph at the same width and keeps the height it found. When it
        // ran first (print, PDF, a page count asked for before a relayout) that measurement is already
        // here, and building a second transient layout for a number we have is pure waste.
        if (_lineCache.TryGetValue(p, out var lc) && lc.Sig == sig && lc.Width == width)
            return lc.Height;

        // Transient: measured then released, never cached. forMeasure drops the colour/underline/
        // strikethrough range calls — DirectWrite DRAWS with those, it does not lay out with them.
        using var layout = CreateLayout(p, width, forMeasure: true);
        double h = Math.Max(EmptyLineHeight(p), layout.LayoutBounds.Height);

        _heightCache.AddOrUpdate(p, new HeightEntry(sig, width, h));
        return h;
    }

    // ---- the cell block-list walk (one advance, four consumers) ------------
    // Everything a walk over a cell's block list needs to know about one entry: where it sits, how tall
    // it is, where a paragraph's text starts and how wide it wraps, and a nested table's geometry.
    //
    // Four walks used to derive all of this SEPARATELY — MeasureCellContentHeight, DrawCellBlockList,
    // HitTestBlockList and CaretInBlockList — and the arithmetic had to agree in four places or the
    // rendered text and the caret/hit-test geometry drift apart (rule #1: one layout, one source). The
    // comments on those four all said "must match the draw walk", which is the code asking a human to
    // hold an invariant it could hold itself. It holds it here now.
    private readonly record struct CellSlot(
        double Y, double Height, double ParaX, double ParaWidth, TableLayout? Table, int OrderedStart);

    // Places one block at `y` inside a content box that starts at `ox` and is `innerW` wide.
    private CellSlot NextCellSlot(Block b, double ox, double y, double innerW, ref List<int>? ordCounters)
    {
        int orderedStart = OrderedStartFor(b, ref ordCounters);
        switch (b)
        {
            case Paragraph p:
            {
                // The list/indent gutter, applied identically by every walk — hit-testing a bulleted
                // paragraph in a cell lands on the wrong character without it.
                double pl = CellParaLeft(p);
                double pw = Math.Max(10, innerW - pl);
                return new CellSlot(y, ParagraphHeight(p, pw), ox + pl, pw, null, orderedStart);
            }
            case ImageBlock im:
                return new CellSlot(y, CellImageSize(im, innerW).h, ox, innerW, null, 0);
            case DividerBlock:
                return new CellSlot(y, DividerHeight, ox, innerW, null, 0);
            case TableBlock nt:
            {
                // Derived once and carried: the draw walk hands it to DrawNestedTable, the hit-test and
                // caret walks read its AnchorRects, and everyone advances by its TotalHeight.
                var tl = LayoutTable(nt, ox, y);
                return new CellSlot(y, tl.TotalHeight, ox, innerW, tl, 0);
            }
            default:
                return new CellSlot(y, 0, ox, innerW, null, 0);
        }
    }

    // Ordered-list numbering, shared by the top-level block map (EnsureBlockLayout) and the cell walk.
    // Counting is PER ListLevel (HTML nested-<ol> semantics): a deeper sublist starts at 1, returning to
    // a shallower level continues that level's own count, and re-entering a deeper level restarts (its
    // counter is dropped on the way up). Bullet items keep the surrounding numbering context alive — a
    // bulleted sublist does not reset its parent's numbers — and ANY non-list block breaks it.
    //
    // `counters` is created on demand: most block lists hold no list paragraph at all, and the walks that
    // do not draw markers (measure, hit-test, caret) should not allocate for numbering they never read.
    private static int OrderedStartFor(Block block, ref List<int>? counters)
    {
        if (block is Paragraph { IsListItem: true } lp)
        {
            counters ??= new List<int>();
            int lvl = Math.Clamp(lp.ListLevel, 0, 16);
            if (counters.Count > lvl + 1) counters.RemoveRange(lvl + 1, counters.Count - lvl - 1);
            if (lp.ListType != ListKind.Ordered) return 0;
            while (counters.Count <= lvl) counters.Add(0);
            int start = counters[lvl];
            counters[lvl] += HardLineCount(lp);
            return start;
        }
        counters?.Clear();
        return 0;
    }

    // The content height of a cell's block list laid out in innerWidth (sum of block heights). Mutually
    // recursive with LayoutTable through nested tables, closing the recursion at any depth.
    private double MeasureCellContentHeight(TableCell cell, double innerWidth)
    {
        double h = 0;
        double w = Math.Max(10, innerWidth);
        List<int>? ord = null;
        foreach (var b in cell.Blocks)
            h += NextCellSlot(b, 0, h, w, ref ord).Height;
        return h;
    }

    // The wrap width the render/hit-test walks actually use for paragraph p: the cell's inner width when
    // p lives in a table cell (sum of the anchor's spanned column widths minus padding — column widths are
    // position-independent, so no table origin is needed), else the top-level content-width formula.
    // Callers that only have the top-level formula (e.g. Home/End) would otherwise build the layout at the
    // wrong width for cell paragraphs, landing on wrong visual-line boundaries and thrashing the cache.
    private double ParagraphWrapWidth(Paragraph p)
    {
        if (FindCell(p) is { } loc)
        {
            var (ar, ac) = loc.tb.AnchorOf(loc.r, loc.c);
            var (cs, _) = loc.tb.SpanOf(ar, ac);
            double w = 0;
            for (int c = ac; c < ac + cs; c++)
                w += c < loc.tb.ColumnWidths.Count ? loc.tb.ColumnWidths[c] : 100;
            return Math.Max(10, w - 2 * CellPad - CellParaLeft(p)); // list/indent gutter, as the walks do
        }
        return Math.Max(10, _layoutWidth - 20 - ParaLeft(p) - p.MarginRight);
    }

    // A block image's drawn size inside a cell of content width innerWidth: declared size scaled to fit.
    private static (double w, double h) CellImageSize(ImageBlock im, double innerWidth)
    {
        var (w, h) = BlockImageDims(im);
        if (w > innerWidth && w > 0) { h *= innerWidth / w; w = innerWidth; }
        return (w, h);
    }

    // Single source of truth for a table's geometry. Render and (later) hit-tests consume this so merged
    // cell rects and skipped (covered) cells stay identical across every consumer.
    private TableLayout LayoutTable(TableBlock tb, double startX, double top)
    {
        int cols = tb.Columns, rows = tb.Rows;

        var colX = new double[cols + 1];
        colX[0] = startX;
        for (int c = 0; c < cols; c++)
            colX[c + 1] = colX[c] + ((c < tb.ColumnWidths.Count) ? tb.ColumnWidths[c] : 100);

        double[] rowH;
        if (_tableRowHeights.TryGetValue(tb, out var cachedH) && cachedH.Length == rows)
        {
            rowH = cachedH;
        }
        else
        {
            rowH = new double[rows];
            for (int r = 0; r < rows; r++) rowH[r] = 20;

            // Base row heights from single-row cells (rowSpan == 1), measured at their merged width.
            foreach (var (r, c, cell) in tb.LogicalCells())
            {
                var (cs, rs) = tb.SpanOf(r, c);
                if (rs != 1) continue;
                double w = colX[Math.Min(c + cs, cols)] - colX[c];
                double ch = MeasureCellContentHeight(cell, w - 2 * CellPad);
                if (ch + 2 * CellPad > rowH[r]) rowH[r] = ch + 2 * CellPad;
            }
            for (int r = 0; r < rows; r++)
                if (r < tb.RowHeights.Count && tb.RowHeights[r] > rowH[r]) rowH[r] = tb.RowHeights[r];

            // Row-spanning cells: if content needs more than the spanned rows provide, grow the last row.
            foreach (var (r, c, cell) in tb.LogicalCells())
            {
                var (cs, rs) = tb.SpanOf(r, c);
                if (rs <= 1) continue;
                double w = colX[Math.Min(c + cs, cols)] - colX[c];
                double need = MeasureCellContentHeight(cell, w - 2 * CellPad) + 2 * CellPad, have = 0;
                for (int rr = r; rr < r + rs && rr < rows; rr++) have += rowH[rr];
                int last = Math.Min(r + rs - 1, rows - 1);
                if (need > have) rowH[last] += need - have;
            }

            _tableRowHeights.AddOrUpdate(tb, rowH);
        }

        return AssembleTableLayout(tb, colX, rowH, startX, top);
    }

    private static TableLayout AssembleTableLayout(TableBlock tb, double[] colX, double[] rowH, double startX, double top)
    {
        int cols = tb.Columns, rows = tb.Rows;
        var rowY = new double[rows + 1];
        rowY[0] = top;
        for (int r = 0; r < rows; r++) rowY[r + 1] = rowY[r] + rowH[r];

        var anchors = new List<(int, int, Rect)>();
        foreach (var (r, c, cell) in tb.LogicalCells())
        {
            var (cs, rs) = tb.SpanOf(r, c);
            int cEnd = Math.Min(c + cs, cols), rEnd = Math.Min(r + rs, rows);
            anchors.Add((r, c, new Rect(colX[c], rowY[r], colX[cEnd] - colX[c], rowY[rEnd] - rowY[r])));
        }
        return new TableLayout(colX, rowY, colX[cols] - startX, rowY[rows] - top, anchors);
    }

    // ---- read-only table drawing ------------------------------------------

    // Draws a top-level table at its document origin and recurses into each anchor cell. `known` is the
    // geometry the caller already computed for this exact (tb, startX, top) — the top-level draw walk
    // needs the layout anyway (for the recorded hit-test rect and the selection chrome), and re-deriving
    // it here allocated a second colX/rowY pair and a second anchor list per table PER FRAME. Row heights
    // were cached; assembling the rectangles was not.
    private void DrawTableBlock(CanvasDrawingSession ds, TableBlock tb, double startX, double top, TableLayout? known = null)
        => DrawNestedTable(ds, tb, startX, top, known);

    private void DrawNestedTable(CanvasDrawingSession ds, TableBlock tb, double startX, double top, TableLayout? known = null)
    {
        var tl = known ?? LayoutTable(tb, startX, top);
        if (!_printMode) RecordTableResizeBoundaries(tb, top, tl); // column/row drag handles for every table
        // A table in a cell: its border band (OnTableSelectBorder) and, when selected, the chrome a top-level
        // table gets from its caller. Recorded before its cells are drawn, so a table nested in one of them
        // comes later in the list and is found first.
        if (!_printMode && tb.Parent is TableCell)
        {
            _cellTableRects.Add((tb, new Rect(tl.ColX[0], top, tl.TableWidth, tl.TotalHeight)));
            DrawTableSelectionChrome(ds, tb, tl.ColX[0], top, tl);
        }
        var cellSel = _printMode ? null : _renderCellSel; // per-pass cache (see DrawDocument)
        var tableBox = new Rect(tl.ColX[0], top, tl.TableWidth, tl.TotalHeight);
        bool topLevel = tb.Parent is FlowDocument;
        foreach (var (r, c, rect) in tl.AnchorRects)
        {
            var cell = tb.Cells[r][c];
            if (cell.Background is { } bg) ds.FillRectangle(rect, bg);
            // Cell-block selection (drag across cells): tint the selected cells.
            if (cellSel is { } cb && ReferenceEquals(cb.tb, tb)
                && r >= cb.r0 && r <= cb.r1 && c >= cb.c0 && c <= cb.c1)
                ds.FillRectangle(rect, SelectionFill);
            ds.DrawRectangle(topLevel ? InsetTableEdges(rect, tableBox) : rect, GrayBorderColor, 1f);
            DrawCellBlockList(ds, cell.Blocks, rect.X + CellPad, rect.Y + CellPad + CellContentOffsetY(cell, rect),
                Math.Max(10, rect.Width - 2 * CellPad));
        }
    }

    // A 1px pen is centred on the rect it strokes, so a cell on the table's edge put half its line OUTSIDE the
    // table's box — and a page's content clip, which starts exactly there, cut it (upstream PR #52: at a page
    // break, 67% of a line; measured here on the print clip's left edge: 34%). The edges that ARE the table's
    // boundary are pulled half a pen inwards, so all of a table's ink is inside the box pagination knows about.
    // Interior edges are shared by two cells and stay centred — insetting those would draw each line twice.
    private static Rect InsetTableEdges(Rect cell, Rect table)
    {
        const double half = 0.5, eps = 0.01;
        double left = cell.X + (Math.Abs(cell.X - table.X) < eps ? half : 0);
        double top = cell.Y + (Math.Abs(cell.Y - table.Y) < eps ? half : 0);
        double right = cell.X + cell.Width - (Math.Abs(cell.X + cell.Width - (table.X + table.Width)) < eps ? half : 0);
        double bottom = cell.Y + cell.Height - (Math.Abs(cell.Y + cell.Height - (table.Y + table.Height)) < eps ? half : 0);
        return new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    // Extra Y offset placing a cell's content per its vertical alignment: 0 for Top, half/all of the
    // slack (cell inner height − content height) for Center/Bottom. Shared by the render, hit-test and
    // caret-geometry walks so all three agree on where the content sits.
    private double CellContentOffsetY(TableCell cell, Rect rect)
    {
        if (cell.VerticalAlignment == CellVerticalAlignment.Top) return 0;
        double innerW = Math.Max(10, rect.Width - 2 * CellPad);
        double slack = rect.Height - 2 * CellPad - MeasureCellContentHeight(cell, innerW);
        if (slack <= 0) return 0;
        return cell.VerticalAlignment == CellVerticalAlignment.Center ? slack / 2 : slack;
    }

    // A ONE-cell block — F5, the menu's "셀 선택", Shift+arrow shrinking back to the anchor. Two selection
    // endpoints in one cell are otherwise a text selection, so this is a marker: the cell plus the exact
    // _selStart/_selEnd objects the block was made with. It is live only while those very objects are the
    // selection and still span the cell (MarkedCell) — any caret move or selection change builds new pointers,
    // so the block ends by itself, and a later selection of the same range (the Ctrl+A cell stage) cannot
    // revive it. Upstream kept a mode flag that every entry point had to reset, and one did not: Shift+arrow
    // across cells painted a block the commands ignored (measured 2026-09-13). Unified on this, both repos.
    private (TableCell cell, TextPointer s, TextPointer e)? _cellBlockMark;

    // The marked one-cell block, if it is still the selection (see _cellBlockMark).
    private (TableBlock tb, int r, int c)? MarkedCell()
    {
        if (_cellBlockMark is not { } m || !ReferenceEquals(_selStart, m.s) || !ReferenceEquals(_selEnd, m.e)) return null;
        if (m.cell.Parent is not TableBlock tb || CellContentEnds(m.cell) is not { } ends) return null;
        if (!ReferenceEquals(m.s.Paragraph, ends.first) || m.s.Offset != 0
            || !ReferenceEquals(m.e.Paragraph, ends.last) || m.e.Offset != GetParagraphLength(ends.last)) return null;
        foreach (var (r, c, cell) in tb.LogicalCells())
            if (ReferenceEquals(cell, m.cell)) return (tb, r, c);
        return null;
    }

    // First and last paragraph of a cell's content in document order (nested tables' cells included).
    private static (Paragraph first, Paragraph last)? CellContentEnds(TableCell cell)
    {
        Paragraph? first = null, last = null;
        foreach (var q in ParagraphsInBlocks(cell.Blocks)) { first ??= q; last = q; }
        return first != null ? (first, last!) : null;
    }

    private static (int r0, int c0, int r1, int c1) SpanRect(TableBlock tb, int r, int c)
    {
        var (cs, rs) = tb.SpanOf(r, c);
        return (r, c, r + rs - 1, c + cs - 1);
    }

    // Selects `cell` as a one-cell block. False when it has no paragraph to hold the selection.
    private bool SelectCellAsBlock(TableCell cell)
    {
        if (CellContentEnds(cell) is not { } ends) return false;
        ClearObjectSelection();
        _selStart = new TextPointer(ends.first, 0);
        _selEnd = new TextPointer(ends.last, GetParagraphLength(ends.last));
        _caret = Clone(_selEnd);
        _cellBlockMark = (cell, _selStart, _selEnd);
        InvalidateCanvas();
        RaiseStatusChanged();
        return true;
    }

    // F5 (HWP): the caret's cell as a one-cell block. False outside a table.
    private bool SelectCellAtCaret()
    {
        if (_caret.Paragraph is not { } p || FindCell(p) is not { } loc) return false;
        var (ar, ac) = loc.tb.AnchorOf(loc.r, loc.c);
        return SelectCellAsBlock(loc.tb.Cells[ar][ac]);
    }

    // Shift+arrow on a cell block (HWP): the anchor corner stays, the active corner — the selection end's cell —
    // steps one cell (dr, dc), past its own span. Back onto the anchor it is a one-cell block again; at the
    // table's edge nothing moves.
    private void ExtendCellBlock(TableBlock tb, int dr, int dc)
    {
        (int r, int c) anchor, active;
        if (MarkedCell() is { } mk) anchor = active = (mk.r, mk.c);
        else if (CellIn(tb, _selStart) is { } a && CellIn(tb, _selEnd) is { } b) (anchor, active) = (a, b);
        else return;
        var (cs, rs) = tb.SpanOf(active.r, active.c);
        int nr = dr < 0 ? active.r - 1 : dr > 0 ? active.r + rs : active.r;
        int nc = dc < 0 ? active.c - 1 : dc > 0 ? active.c + cs : active.c;
        if (nr < 0 || nc < 0 || nr >= tb.Rows || nc >= tb.Columns) return;
        (int r, int c) next = tb.AnchorOf(nr, nc);
        if (next == anchor) { SelectCellAsBlock(tb.Cells[anchor.r][anchor.c]); return; }
        if (CellContentEnds(tb.Cells[anchor.r][anchor.c]) is not { } from
            || CellContentEnds(tb.Cells[next.r][next.c]) is not { } to) return;
        _selStart = new TextPointer(from.first, 0);
        _selEnd = new TextPointer(to.last, GetParagraphLength(to.last));
        _caret = Clone(_selEnd);
        InvalidateCanvas();
        RaiseStatusChanged();
    }

    // The anchor cell of `tb` holding pointer p directly, or null (another table, nested or outside).
    private static (int r, int c)? CellIn(TableBlock tb, TextPointer p)
        => p.Paragraph is { } q && FindCell(q) is { } loc && ReferenceEquals(loc.tb, tb) ? tb.AnchorOf(loc.r, loc.c) : null;

    // The table whose cells are block-selected, with the span-aware cell rectangle: selection endpoints in
    // different cells of one table (a drag, Shift+arrow), or a one-cell block (_cellBlockMark). Null for
    // ordinary text selections. The renderer's fill and every command read this — one source.
    private (TableBlock tb, int r0, int c0, int r1, int c1)? CellBlockSelection()
    {
        if (MarkedCell() is { } mk) { var one = SpanRect(mk.tb, mk.r, mk.c); return (mk.tb, one.r0, one.c0, one.r1, one.c1); }
        if (_selStart.Paragraph == null || _selEnd.Paragraph == null) return null;
        if (FindCell(_selStart.Paragraph) is not { } s) return null;
        if (FindCell(_selEnd.Paragraph) is not { } e || !ReferenceEquals(s.tb, e.tb)) return null;
        if (SelectedCellRange(s.tb) is not { } rg) return null;
        return (s.tb, rg.r0, rg.c0, rg.r1, rg.c1);
    }

    // Recursive primitive: draws a cell's block list top-to-bottom inside its content box.
    private void DrawCellBlockList(CanvasDrawingSession ds, IList<Block> blocks, double ox, double oy, double innerW)
    {
        double by = 0;
        // Ordered-list numbering is per cell — the top-level counters live in the block layout map, which
        // only covers Document.Blocks. The level semantics are EnsureBlockLayout's, because both now go
        // through OrderedStartFor.
        List<int>? ord = null;
        foreach (var b in blocks)
        {
            var slot = NextCellSlot(b, ox, oy + by, innerW, ref ord);
            by += slot.Height;

            switch (b)
            {
                case Paragraph para:
                    // The same painting the top-level walk does, because it is literally the same code.
                    DrawParagraphContent(ds, para, BuildTextLayout(para, slot.ParaWidth),
                        slot.ParaX, slot.Y, slot.ParaWidth, slot.Height, leftLimit: ox, slot.OrderedStart);
                    break;
                case ImageBlock cimg:
                {
                    var (iw, _) = CellImageSize(cimg, innerW);
                    var ir = new Rect(ox, slot.Y, iw, slot.Height);
                    var bmp = ImageToDraw(ds, cimg, cimg.RawBytes, cimg.Image, ir);
                    if (bmp != null) DrawPicture(ds, bmp, ir); else DrawPlaceholder(ds, ir, "");
                    TrackCellImage(ds, cimg, ir); // selection/resize registry — see _cellImageRects
                    break;
                }
                case DividerBlock:
                {
                    double ly = slot.Y + DividerHeight / 2;
                    ds.DrawLine((float)ox, (float)ly, (float)(ox + innerW), (float)ly, GrayBorderColor, 1f);
                    break;
                }
                case TableBlock nt:
                    // Once, not twice: DrawNestedTable used to derive this layout again for itself while
                    // the advance asked for it separately — per nested table, per frame. The slot carries
                    // the one the walk already built. (The identical duplicate at the top level lived in
                    // DrawContentWalk; that the two had to be found separately is the split doing what it
                    // always does.)
                    DrawNestedTable(ds, nt, ox, slot.Y, slot.Table);
                    break;
            }
        }
    }

    // ---- Phase 3b: Tab cell navigation ------------------------------------

    // Tab/Shift+Tab move between table cells in document order; outside a table, on a list item they
    // change the nesting level (Word/HWP behavior), otherwise Tab inserts spaces.
    // Tab past the document's last cell appends a row to the enclosing top-level table.
    private void HandleTab(bool shift)
    {
        var loc = _caret.Paragraph != null ? FindCell(_caret.Paragraph) : null;
        if (loc == null)
        {
            // List item: Tab demotes (deeper level), Shift+Tab promotes. Cells keep cell-first priority.
            if (_caret.Paragraph is { IsListItem: true } lp)
            {
                int nl = Math.Clamp(lp.ListLevel + (shift ? -1 : 1), 0, 8);
                if (nl != lp.ListLevel)
                {
                    PushUndo(null);
                    lp.ListLevel = nl;
                    AfterFormat();
                }
                return;
            }
            if (shift) return; // Shift+Tab outside lists/cells: nothing to outdent
            // Tab completes the token before it the way a typed space does — AutoLinkOnType's contract
            // names space, tab and Enter. The key inserts four spaces and InsertText auto-links only a single
            // typed whitespace, so Tab never linked (measured). The link joins the spaces' undo group: their
            // checkpoint was taken before either.
            var tabPara = _caret.Paragraph;
            int tabAt = _caret.Offset;
            InsertText("    ");
            if (AutoLinkOnType && tabPara != null)
            {
                TryAutoLink(tabPara, tabAt);
                AfterEdit(); // the link changes the paragraph's layout (underline, colour)
            }
            return;
        }

        var (tb, r, c) = loc.Value;
        var (ar, ac) = tb.AnchorOf(r, c);
        var current = tb.Cells[ar][ac];
        var all = AllCellsInOrder();
        int idx = all.IndexOf(current);
        if (idx < 0) return;

        // The outermost table around the caret (through nested and in-cell inline tables); nested tables don't
        // grow via Tab — use the right-click menu.
        var top = tb;
        while (EnclosingTableOf(top) is { } up) top = up;

        if (shift)
        {
            if (idx > 0) FocusCell(all[idx - 1].Para); // else: first cell of the document -> no-op
        }
        else if (idx + 1 < all.Count && IsWithin(all[idx + 1], top))
        {
            FocusCell(all[idx + 1].Para);
        }
        else
        {
            // Past this table's last cell: add a row to it (Word/HWP). Only the document's last table used to
            // grow — from any other, Tab jumped into the next table below (upstream round 34, user decision).
            PushUndo(null);
            top.InsertRow(top.Rows);
            if (Document != null) UpdateParents(Document);
            _tableRowHeights.Remove(top);
            RelayoutToViewport();
            FocusCell(top.Cells[top.Rows - 1][0].Para);
        }
    }

    // Takes one inline object's character (a picture, an inline table) out of `p` and returns the offset it sat at.
    // Caret and selection ends past it are pulled back one: they kept their offsets, so a caret after a trailing
    // object sat one past the paragraph's end and the next keys went nowhere (upstream round 34).
    private int RemoveInlineCharacter(Paragraph p, Inline obj)
    {
        int idx = p.Inlines.IndexOf(obj), off = 0;
        for (int i = 0; i < idx; i++) off += InlineLen(p.Inlines[i]);
        p.Inlines.Remove(obj);
        if (p.Inlines.Count == 0) p.Inlines.Add(new Run { Text = "" });
        TextPointer Back(TextPointer t) => ReferenceEquals(t.Paragraph, p) && t.Offset > off ? new TextPointer(p, t.Offset - 1) : t;
        _caret = Back(_caret);
        _selStart = Back(_selStart);
        _selEnd = Back(_selEnd);
        return off;
    }

    // Whether `e` is `ancestor` or lies inside it, through the parent chain (cells, nested and inline tables).
    // Only for nodes in the document: a detached subtree keeps its Parent links.
    private static bool IsWithin(object? e, object ancestor)
    {
        for (object? cur = e; cur != null; cur = (cur as TextElement)?.Parent)
            if (ReferenceEquals(cur, ancestor)) return true;
        return false;
    }

    // Selects the whole content of a cell (caret at end), redirecting covered cells to their merge anchor.
    private void FocusCell(Paragraph cell)
    {
        if (FindCell(cell) is { } loc && loc.tb.IsCovered(loc.r, loc.c))
        {
            var (ar, ac) = loc.tb.AnchorOf(loc.r, loc.c);
            cell = loc.tb.Cells[ar][ac].Para;
        }
        int len = GetParagraphLength(cell);
        _caret = new TextPointer(cell, len);
        _selStart = new TextPointer(cell, 0);
        _selEnd = new TextPointer(cell, len);
        _coalesceKey = null;
        _pendingCaretStyles = null;
        RestartBlink();
        SyncIme();
        ScrollCaretIntoView();
        InvalidateCanvas();
        RaiseStatusChanged();
    }

    // All anchor cells in document order, descending into nested tables: each cell is followed by the
    // cells of any tables nested inside it, so Tab enters a nested table right after its host cell and
    // resumes at the host's sibling once the nested cells are exhausted. Covered cells are excluded.
    private List<TableCell> AllCellsInOrder()
    {
        var result = new List<TableCell>();
        if (Document != null) CollectCells(Document.Blocks, result);
        return result;
    }

    private static void CollectCells(IEnumerable<Block> blocks, List<TableCell> outList)
    {
        foreach (var b in blocks)
        {
            if (b is TableBlock tb)
                foreach (var (_, _, cell) in tb.LogicalCells())
                {
                    outList.Add(cell);
                    CollectCells(cell.Blocks, outList);
                }
            else if (b is Paragraph para)
                foreach (var inl in para.Inlines)
                    if (inl is InlineTable it)
                        foreach (var (_, _, cell) in it.Table.LogicalCells())
                        {
                            outList.Add(cell);
                            CollectCells(cell.Blocks, outList);
                        }
        }
    }

    // The table whose cell directly holds p, straight off the PARENT CHAIN (wired by UpdateParents after
    // every structural edit) — O(1), no grid scan. Callers that only need the table identity (the
    // per-drawn-paragraph selection guard) use this instead of FindCell.
    private static TableBlock? CellTableOf(Paragraph p)
        => p.Parent is TableCell tc && tc.Parent is TableBlock tb ? tb : null;

    // The innermost table + cell directly holding paragraph p. Resolved through the parent chain: p.Parent
    // IS the containing cell, so nested and inline tables fall out for free (the old version scanned the
    // WHOLE document recursively per call — with 16 call sites on the render and caret hot paths, notably
    // one per drawn paragraph in DrawSelectionHighlight, that was O(visible × document) per frame).
    // Only the (r,c) lookup scans, and only within that one table.
    private static (TableBlock tb, int r, int c)? FindCell(Paragraph p)
    {
        if (p.Parent is not TableCell tc || tc.Parent is not TableBlock tb) return null;
        for (int r = 0; r < tb.Rows && r < tb.Cells.Count; r++)
            for (int c = 0; c < tb.Columns && c < tb.Cells[r].Count; c++)
                if (ReferenceEquals(tb.Cells[r][c], tc)) return (tb, r, c);
        return null;
    }

    // ---- Phase 5: public insert commands (toolbar) ------------------------

    /// <summary>Inserts a <paramref name="rows"/>×<paramref name="cols"/> table at the caret, sized with
    /// equal columns spanning the available width (the document content width, or the enclosing cell's
    /// width when nested).</summary>
    public void InsertTable(int rows, int cols)
    {
        if (Document == null || IsReadOnly || !AllowTables || rows < 1 || cols < 1 || !CaretCanHostBlock) return;
        PushUndo(null);
        var tb = new TableBlock(rows, cols);

        double avail, minCol;
        var loc = _caret.Paragraph != null ? FindCell(_caret.Paragraph) : null;
        if (loc is { } cell)
        {
            // Nested: fit the enclosing cell's content width (its column-span widths minus padding), with a
            // low per-column floor so a deeply nested table doesn't overflow its cell.
            double cellW = 0;
            var (cs, _) = cell.tb.SpanOf(cell.r, cell.c);
            for (int k = cell.c; k < cell.c + cs && k < cell.tb.ColumnWidths.Count; k++) cellW += cell.tb.ColumnWidths[k];
            avail = Math.Max(20, cellW - 10);
            minCol = 15;
        }
        else { avail = _layoutWidth > 60 ? _layoutWidth - 20 : 600; minCol = 40; }
        double w = Math.Max(minCol, avail / cols);
        for (int c = 0; c < tb.ColumnWidths.Count; c++) tb.ColumnWidths[c] = w;

        InsertBlockAtCaret(tb);
        AfterEdit();
    }

    /// <summary>Inserts a horizontal divider at the caret.</summary>
    public void InsertDivider()
    {
        if (Document == null || IsReadOnly || !CaretCanHostBlock) return;
        PushUndo(null);
        InsertBlockAtCaret(new DividerBlock());
        AfterEdit();
    }

    // ---- Phase 5: table-structure operations (right-click menu) ------------

    // Where the caret may land in cell (r,c). A covered cell is not in LogicalCells(), so nothing renders
    // it, no click can reach it and no formatter walks it — a caret parked there types into a paragraph
    // the document cannot show. FocusCell does this redirect for the navigation paths; the four row/column
    // operations below set the caret on a fixed slot and need it too, because inserting inside a merge
    // extends it over the new cells, and deleting next to one can clamp onto a covered slot.
    // (Same name and body as upstream, which had already fixed this; the port's command fuzz found it
    // at 2000 seeds.)
    private static Paragraph CellCaretTarget(TableBlock tb, int r, int c)
    {
        if (!tb.IsCovered(r, c)) return tb.Cells[r][c].Para;
        var (ar, ac) = tb.AnchorOf(r, c);
        return tb.Cells[ar][ac].Para;
    }

    // Where "insert row below" / "insert column right" put the new row or column for the cell at (r,c):
    // past the WHOLE cell, merge included (Word's behaviour). The menu used to pass r + 1 / c + 1, which on
    // a merged cell is INSIDE the merge — InsertRow grows the merge over the new row, so the new cells showed
    // up only beside the merged cell, never under it (reported from a live check; upstream has the same +1).
    internal static int RowBelowIndex(TableBlock tb, int r, int c) => r + Math.Max(1, tb.SpanOf(r, c).rs);
    internal static int ColumnRightIndex(TableBlock tb, int r, int c) => c + Math.Max(1, tb.SpanOf(r, c).cs);

    private bool TableInsertRow(TableBlock tb, int at)
    {
        if (Document == null || at < 0) return false;
        PushUndo(null);
        tb.InsertRow(at);
        UpdateParents(Document);
        int ar = Math.Clamp(at, 0, tb.Rows - 1);
        SetCaretToCell(CellCaretTarget(tb, ar, 0));
        AfterStructuralEdit(tb);
        return true;
    }

    private bool TableDeleteRow(TableBlock tb, int at)
    {
        if (Document == null || tb.Rows <= 1 || at < 0) return false;
        PushUndo(null);
        tb.DeleteRow(at);
        UpdateParents(Document);
        int nr = Math.Clamp(at, 0, tb.Rows - 1);
        SetCaretToCell(CellCaretTarget(tb, nr, 0));
        AfterStructuralEdit(tb);
        return true;
    }

    private bool TableInsertColumn(TableBlock tb, int at)
    {
        if (Document == null || at < 0) return false;
        PushUndo(null);
        tb.InsertColumn(at);
        UpdateParents(Document);
        int ac = Math.Clamp(at, 0, tb.Columns - 1);
        SetCaretToCell(CellCaretTarget(tb, 0, ac));
        AfterStructuralEdit(tb);
        return true;
    }

    private bool TableDeleteColumn(TableBlock tb, int at)
    {
        if (Document == null || tb.Columns <= 1 || at < 0) return false;
        PushUndo(null);
        tb.DeleteColumn(at);
        UpdateParents(Document);
        int nc = Math.Clamp(at, 0, tb.Columns - 1);
        SetCaretToCell(CellCaretTarget(tb, 0, nc));
        AfterStructuralEdit(tb);
        return true;
    }

    // ---- public structure commands (upstream PR #48) ----------------------------------------------------
    // The four commands above were reachable only from the context menu, so a host that builds its own
    // toolbar - or generates a document by script - had to edit TableBlock itself, which skips the undo
    // checkpoint, the parent wiring and the layout invalidation they do.
    //
    // Two shapes over one body: the Table* four name the table and the index (scripting), the caret six act
    // where the caret is (a toolbar button). Both return false when nothing changed, and both stop on a
    // read-only editor. AllowTables is NOT consulted: it gates CREATING tables, and the context menu's
    // row/column items are not gated by it either.

    // A caller's table is not trusted: one from another document would push an undo checkpoint for, and
    // edit, a tree this editor does not show. Every table in the document owns at least one cell paragraph,
    // so this reuses the document walker (AllParagraphs) + FindCell rather than a table walker of its own.
    // (A parent-chain check would call a detached subtree "inside" — see the round-34 backport.)
    private bool TableIsInDocument(TableBlock table)
    {
        foreach (var p in AllParagraphs())
            if (FindCell(p) is { } loc && ReferenceEquals(loc.tb, table)) return true;
        return false;
    }

    // Insertion accepts one past the end (append); deletion does not.
    private bool EditableTable(TableBlock? table, int at, int count, bool insert)
        => table != null && Document != null && !IsReadOnly
           && at >= 0 && at <= (insert ? count : count - 1)
           && TableIsInDocument(table);

    /// <summary>Inserts an empty row into <paramref name="table"/> before row <paramref name="at"/>
    /// (<paramref name="at"/> == <see cref="TableBlock.Rows"/> appends). A cell crossing that boundary as
    /// part of a vertical merge grows over the new row. One undo step; the caret lands in the new row.</summary>
    /// <returns><see langword="false"/> when the editor is read-only or has no document, the table is not
    /// in that document, or <paramref name="at"/> is out of range - nothing changes in those cases.</returns>
    public bool InsertTableRow(TableBlock table, int at)
        => EditableTable(table, at, table?.Rows ?? 0, insert: true) && TableInsertRow(table!, at);

    /// <summary>Deletes row <paramref name="at"/> of <paramref name="table"/> with the cells in it.
    /// One undo step; the caret lands in the row that takes its place.</summary>
    /// <returns><see langword="false"/> under the same conditions as <see cref="InsertTableRow"/>, plus a
    /// table of a single row, which always keeps it.</returns>
    public bool DeleteTableRow(TableBlock table, int at)
        => EditableTable(table, at, table?.Rows ?? 0, insert: false) && TableDeleteRow(table!, at);

    /// <summary>Inserts an empty column into <paramref name="table"/> before column <paramref name="at"/>
    /// (<paramref name="at"/> == <see cref="TableBlock.Columns"/> appends), at the default width.
    /// One undo step; the caret lands in the new column.</summary>
    /// <returns><see langword="false"/> under the same conditions as <see cref="InsertTableRow"/>.</returns>
    public bool InsertTableColumn(TableBlock table, int at)
        => EditableTable(table, at, table?.Columns ?? 0, insert: true) && TableInsertColumn(table!, at);

    /// <summary>Deletes column <paramref name="at"/> of <paramref name="table"/> with the cells in it.
    /// One undo step; the caret lands in the column that takes its place.</summary>
    /// <returns><see langword="false"/> under the same conditions as <see cref="InsertTableRow"/>, plus a
    /// table of a single column, which always keeps it.</returns>
    public bool DeleteTableColumn(TableBlock table, int at)
        => EditableTable(table, at, table?.Columns ?? 0, insert: false) && TableDeleteColumn(table!, at);

    // The caret's cell as its merge anchor (what the context menu passes too), or null when the caret is
    // not in a table - including a table held whole, which names no cell.
    private (TableBlock tb, int r, int c)? CaretCell()
    {
        if (_caret.Paragraph is not { } p || FindCell(p) is not { } loc) return null;
        var (ar, ac) = loc.tb.AnchorOf(loc.r, loc.c);
        return (loc.tb, ar, ac);
    }

    /// <summary>Inserts a row above the caret's row. Nothing happens when the caret is not in a table.</summary>
    /// <returns>Whether a row was inserted.</returns>
    public bool InsertRowAbove()
        => CaretCell() is { } at && InsertTableRow(at.tb, at.r);

    /// <summary>Inserts a row below the caret's row - below the whole merged area when the caret's cell
    /// spans several rows. Nothing happens when the caret is not in a table.</summary>
    /// <returns>Whether a row was inserted.</returns>
    public bool InsertRowBelow()
        => CaretCell() is { } at && InsertTableRow(at.tb, RowBelowIndex(at.tb, at.r, at.c));

    /// <summary>Deletes the caret's row. Nothing happens when the caret is not in a table, or when the
    /// table has a single row.</summary>
    /// <returns>Whether the row was deleted.</returns>
    public bool DeleteRow()
        => CaretCell() is { } at && DeleteTableRow(at.tb, at.r);

    /// <summary>Inserts a column to the left of the caret's column. Nothing happens when the caret is not
    /// in a table.</summary>
    /// <returns>Whether a column was inserted.</returns>
    public bool InsertColumnLeft()
        => CaretCell() is { } at && InsertTableColumn(at.tb, at.c);

    /// <summary>Inserts a column to the right of the caret's column - right of the whole merged area when
    /// the caret's cell spans several columns. Nothing happens when the caret is not in a table.</summary>
    /// <returns>Whether a column was inserted.</returns>
    public bool InsertColumnRight()
        => CaretCell() is { } at && InsertTableColumn(at.tb, ColumnRightIndex(at.tb, at.r, at.c));

    /// <summary>Deletes the caret's column. Nothing happens when the caret is not in a table, or when the
    /// table has a single column.</summary>
    /// <returns>Whether the column was deleted.</returns>
    public bool DeleteColumn()
        => CaretCell() is { } at && DeleteTableColumn(at.tb, at.c);

    private void TableMergeSelected(TableBlock tb)
    {
        if (Document == null || SelectedCellRange(tb) is not { } g || !IsCleanRect(tb, g.r0, g.c0, g.r1, g.c1)) return;
        PushUndo(null);
        tb.MergeCells(g.r0, g.c0, g.r1, g.c1);
        UpdateParents(Document);
        SetCaretToCell(tb.Cells[g.r0][g.c0].Para);
        AfterStructuralEdit(tb);
    }

    private void TableUnmergeCell(TableBlock tb, int r, int c)
    {
        if (Document == null || r < 0 || c < 0) return;
        PushUndo(null);
        tb.UnmergeCell(r, c);
        UpdateParents(Document);
        SetCaretToCell(tb.Cells[r][c].Para);
        AfterStructuralEdit(tb);
    }

    // Removes a table (top-level or nested in a cell) and normalizes, dropping the caret on the first
    // top-level paragraph.
    private void DeleteTable(TableBlock tb)
    {
        if (Document == null) return;
        PushUndo(null);
        RemoveBlockAnywhere(tb);
        UpdateParents(Document);
        var first = FirstParagraph();
        if (first != null) SetCaretToCell(first);
        AfterStructuralEdit(tb);
    }

    private void SetCaretToCell(Paragraph p)
    {
        _caret = new TextPointer(p, 0);
        CollapseSelectionToCaret();
    }

    // Drops the cached row heights of tb AND every ancestor table (a nested/inline table's size change
    // re-measures the host rows too) — the targeted version of a wholesale _tableRowHeights.Clear().
    private void InvalidateTableMeasure(TableBlock tb)
    {
        _tableRowHeights.Remove(tb);
        for (object? cur = tb.Parent; cur != null; )
        {
            switch (cur)
            {
                case TableCell tc: cur = tc.Parent; break;
                case TableBlock anc: _tableRowHeights.Remove(anc); cur = anc.Parent; break;
                case InlineTable it: cur = it.Parent; break;
                case Paragraph host: cur = host.Parent; break;
                default: cur = null; break;
            }
        }
    }

    private void AfterStructuralEdit(TableBlock tb)
    {
        DropDetachedObjectSelection(); // a host call edits without clearing it first (DeleteTableRow)
        InvalidateTableMeasure(tb);
        _coalesceKey = null;
        RelayoutToViewport();
        RestartBlink();
        SyncIme();
        InvalidateCanvas();
        RaiseStatusChanged();
    }

    private bool RemoveBlockAnywhere(Block b)
    {
        if (Document == null) return false;
        if (Document.Blocks.Remove(b)) return true;
        return RemoveBlockFromCells(Document.Blocks, b);
    }

    // Searches the document's top level and then recurses through cells — of block tables AND of inline
    // tables, whose grid hangs off a paragraph's inlines. Descending into block tables only meant an
    // image, divider or nested table living in an inline table's cell was never found: Delete pushed an
    // undo checkpoint, dropped the selection, and left the block on screen.
    private static bool RemoveBlockFromCells(IEnumerable<Block> blocks, Block target)
    {
        foreach (var blk in blocks)
        {
            if (blk is TableBlock tb)
            {
                if (RemoveFromTable(tb, target)) return true;
            }
            else if (blk is Paragraph p)
            {
                foreach (var inl in p.Inlines)
                    if (inl is InlineTable it && RemoveFromTable(it.Table, target)) return true;
            }
        }
        return false;

        static bool RemoveFromTable(TableBlock tb, Block target)
        {
            foreach (var row in tb.Cells)
                foreach (var cell in row)
                {
                    if (cell.Blocks.Remove(target)) return true;
                    if (RemoveBlockFromCells(cell.Blocks, target)) return true;
                }
            return false;
        }
    }

    // The selected rectangular cell block defined by the two selection endpoints (span-aware), or null
    // unless both endpoints land in different cells of `tb`. (A one-cell block is CellBlockSelection's; the
    // callers of this — merge, copy cell, cell background — fall back to the caret's cell, the same cell.)
    private (int r0, int c0, int r1, int c1)? SelectedCellRange(TableBlock tb)
    {
        if (_selStart.Paragraph == null || _selEnd.Paragraph == null) return null;
        if (FindCell(_selStart.Paragraph) is not { } s || s.tb != tb) return null;
        if (FindCell(_selEnd.Paragraph) is not { } e || e.tb != tb) return null;
        if (s.r == e.r && s.c == e.c) return null;
        var (scs, srs) = tb.SpanOf(s.r, s.c);
        var (ecs, ers) = tb.SpanOf(e.r, e.c);
        int r0 = Math.Min(s.r, e.r), c0 = Math.Min(s.c, e.c);
        int r1 = Math.Max(s.r + srs - 1, e.r + ers - 1), c1 = Math.Max(s.c + scs - 1, e.c + ecs - 1);
        return (r0, c0, r1, c1);
    }

    // True when the box is a mergeable rectangle: more than one cell and no anchor inside it reaches
    // outside the box (no partial overlap with an existing merge).
    private static bool IsCleanRect(TableBlock tb, int r0, int c0, int r1, int c1)
    {
        if (r0 < 0 || c0 < 0 || r1 >= tb.Rows || c1 >= tb.Columns) return false;
        if (r0 == r1 && c0 == c1) return false;
        for (int r = r0; r <= r1; r++)
            for (int c = c0; c <= c1; c++)
            {
                var (ar, ac) = tb.AnchorOf(r, c);
                if (ar < r0 || ac < c0) return false;
                var (cs, rs) = tb.SpanOf(ar, ac);
                if (ar + rs - 1 > r1 || ac + cs - 1 > c1) return false;
            }
        return true;
    }

    // ---- block <-> inline table toggle (HWP "treat as character") ----------

    // Converts a top-level block table into an inline table embedded in an adjacent paragraph.
    internal void ConvertTableBlockToInline(TableBlock tb)
    {
        if (Document == null) return;
        int idx = Document.Blocks.IndexOf(tb);
        if (idx < 0) return;

        Paragraph? anchor = null;
        bool atEnd = true;
        if (idx > 0 && Document.Blocks[idx - 1] is Paragraph prev) anchor = prev;
        else
            for (int i = idx + 1; i < Document.Blocks.Count && anchor == null; i++)
                if (Document.Blocks[i] is Paragraph next) { anchor = next; atEnd = false; }
        if (anchor == null) return;

        PushUndo(null);
        var it = new InlineTable { Table = (TableBlock)tb.Clone() };
        Document.Blocks.Remove(tb);
        if (atEnd) anchor.Inlines.Add(it);
        else anchor.Inlines.Insert(0, it);
        if (ReferenceEquals(_selectedBlock, tb)) _selectedBlock = null;
        UpdateParents(Document);

        int off = 0;
        foreach (var inl in anchor.Inlines) { off += InlineLen(inl); if (ReferenceEquals(inl, it)) break; }
        _caret = new TextPointer(anchor, off);
        CollapseSelectionToCaret();
        AfterEdit();
    }

    // Promotes an inline table back to a block table, inserted right after its host paragraph.
    internal void ConvertInlineTableToBlock(Paragraph host, InlineTable it)
    {
        if (Document == null) return;
        // The host's own container, not Document.Blocks — an inline table inside a table cell used to
        // hit IndexOf == -1 and the command did nothing (a cell holding a nested table is rule #4).
        // Same fix, same reason, as ConvertInlineImageToBlock.
        if (BlockContainerOf(host) is not { } container) return;
        int idx = container.IndexOf(host);
        if (idx < 0) return;

        PushUndo(null);
        var tb = (TableBlock)it.Table.Clone();
        // The caret (and a whole-table selection) may be in the inline original's cells, which leave the document
        // with it — typing went nowhere (upstream round 34). It goes where the table was.
        bool caretInside = IsWithin(_caret.Paragraph, it.Table)
            || IsWithin(_selStart.Paragraph, it.Table) || IsWithin(_selEnd.Paragraph, it.Table);
        int off = RemoveInlineCharacter(host, it);
        if (caretInside) _caret = new TextPointer(host, off);
        container.Insert(idx + 1, tb);
        UpdateParents(Document);

        _selectedInline = null;
        _selectedInlineTable = null;
        _selectedBlock = tb;
        CollapseSelectionToCaret();
        _tableRowHeights.Remove(tb);
        RelayoutToViewport();
        RestartBlink();
        InvalidateCanvas();
        RaiseStatusChanged();
    }
}
