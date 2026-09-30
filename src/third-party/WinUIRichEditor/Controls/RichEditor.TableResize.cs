using System;
using System.Collections.Generic;
using Windows.Foundation;
using Microsoft.UI.Xaml.Input;
using Microsoft.Graphics.Canvas;
using WinUIRichEditor.Documents;

namespace WinUIRichEditor.Controls;

// Phase 5: table column-width resize. Column boundary rects are recorded each render pass (top-level
// tables) and hit-tested on press; dragging an internal edge redistributes the two adjacent columns,
// the outer-right edge grows/shrinks the table. Mirrors the Avalonia original's column resize.
public partial class RichEditor
{
    // Keyed by table identity and replaced wholesale whenever the table draws; kept until the next
    // relayout (ClearRecordedGeometry). See the inline-object rects in RichEditor.BlockSelection.cs for
    // the scheme rationale (draw-time recording instead of a per-frame document-wide geometry pass).
    private readonly Dictionary<TableBlock, List<(int col, Rect rect)>> _columnBoundaries = new();
    private readonly Dictionary<TableBlock, List<(int row, double height, Rect rect)>> _rowBoundaries = new();
    private readonly Dictionary<TableBlock, Rect> _tableRects = new();
    // Tables nested in a cell — at any depth, an inline table's cells included — drawn by DrawNestedTable.
    // Their border selects them like a top-level table's (OnTableSelectBorder). A list, recorded before a
    // table's cells are drawn, so walking it backwards finds the innermost table first.
    private readonly List<(TableBlock tb, Rect rect)> _cellTableRects = new();
    // Doc-space origin of EVERY drawn table — top-level, nested-in-cell and inline. _tableRects only
    // covers top-level tables and _inlineTableRects only inline ones, so nested tables previously had no
    // recorded geometry at all; caret code that needs to locate one (entering it with ↑/↓, stepping out
    // of it) had nothing to work from.
    private readonly Dictionary<TableBlock, Point> _tableOrigins = new();

    // Drops every recorded doc-space rect (inline objects, table outers, resize boundaries). Called from
    // RelayoutToViewport — the funnel for anything that can move content (edit, format, resize, zoom,
    // width change). Scrolling keeps them (doc space); the next draw re-records whatever is visible.
    private void ClearRecordedGeometry()
    {
        _inlineImageRects.Clear();
        _inlineTableRects.Clear();
        _cellImageRects.Clear();
        _columnBoundaries.Clear();
        _rowBoundaries.Clear();
        _tableRects.Clear();
        _cellTableRects.Clear();
        _tableOrigins.Clear();
    }

    private const double TableBorderGrab = 5;

    private bool _resizingColumn;
    private TableBlock? _resizingColTable;
    private int _resizingColIndex;
    private bool _resizingLastCol;
    private double _colResizeStartX, _initColW, _initNextColW;

    private bool _resizingRow;
    private TableBlock? _resizingRowTable;
    private int _resizingRowIndex;
    private double _rowResizeStartY, _initRowH;

    private const double ColBorderGrab = 4;

    // A resize drag takes its undo snapshot on the FIRST actual move, not on press. Pressing a table
    // border or an image handle and releasing without dragging changes nothing, so it must not clone the
    // whole document into the undo stack, consume an undo step, or flip IsModified — which drives the
    // host's unsaved-changes prompt (the same "no empty undo step for a no-op" rule the key handlers
    // follow). The snapshot still precedes the first mutation because each Resize* pushes before it writes.
    private bool _dragUndoPending;

    private void PushDragUndoOnce()
    {
        if (!_dragUndoPending) return;
        _dragUndoPending = false;
        PushUndo(null);
    }

    // Records each column's right-edge and each row's bottom-edge grab band. Called from DrawNestedTable
    // so it covers EVERY table — top-level, nested-in-cell, and inline — not just the top level.
    private void RecordTableResizeBoundaries(TableBlock tb, double top, in TableLayout tl)
    {
        _tableOrigins[tb] = new Point(tl.ColX.Length > 0 ? tl.ColX[0] : 0, top);
        var cols = new List<(int col, Rect rect)>(tb.Columns);
        for (int c = 0; c < tb.Columns && c + 1 < tl.ColX.Length; c++)
        {
            double x = tl.ColX[c + 1];
            cols.Add((c, new Rect(x - ColBorderGrab, top, 2 * ColBorderGrab, tl.TotalHeight)));
        }
        _columnBoundaries[tb] = cols;

        double left = tl.ColX[0];
        var rows = new List<(int row, double height, Rect rect)>(tb.Rows);
        for (int r = 0; r < tb.Rows && r + 1 < tl.RowY.Length; r++)
        {
            double y = tl.RowY[r + 1];
            double h = tl.RowY[r + 1] - tl.RowY[r];
            rows.Add((r, h, new Rect(left, y - ColBorderGrab, tl.TableWidth, 2 * ColBorderGrab)));
        }
        _rowBoundaries[tb] = rows;
    }

    // The outer rect of a TOP-LEVEL table, for left/top-border block selection (only top-level tables
    // are block-selectable; nested/inline tables are reached/edited through their cells).
    private void RecordTopLevelTableRect(TableBlock tb, double top, in TableLayout tl)
    {
        if (_printMode) return; // print renders must not touch the screen hit-test geometry
        _tableRects[tb] = new Rect(tl.ColX[0], top, tl.TableWidth, tl.TotalHeight);
    }

    private static void EnsureColumnWidths(TableBlock tb)
    {
        while (tb.ColumnWidths.Count < tb.Columns) tb.ColumnWidths.Add(100);
    }

    private static void EnsureRowHeights(TableBlock tb)
    {
        while (tb.RowHeights.Count < tb.Rows) tb.RowHeights.Add(0);
    }

    // Starts a column resize if the press lands on a column boundary. Returns true when it consumed it.
    // The pointer capture is the only part that needs the event; the rest is BeginColumnResizeAt, which
    // tests drive with a document point.
    private bool BeginColumnResizeAt(Point pt)
    {
        if (IsReadOnly) return false;
        foreach (var (tb, list) in _columnBoundaries)
        foreach (var (col, rect) in list)
            if (rect.Contains(pt))
            {
                // Nothing is written on press — not even the missing widths LayoutTable already draws as 100.
                // Padding here changed the document with no undo step and no IsModified (measured 2026-09-15:
                // ColumnWidths 1 → 3 on a click); ResizeColumn pads after its undo checkpoint instead.
                _dragUndoPending = true; // pushed on the first move (see PushDragUndoOnce)
                _resizingColumn = true;
                _resizingColTable = tb;
                _resizingColIndex = col;
                _resizingLastCol = col >= tb.Columns - 1;
                _colResizeStartX = pt.X;
                _initColW = col < tb.ColumnWidths.Count ? tb.ColumnWidths[col] : 100;
                _initNextColW = (col + 1 < tb.ColumnWidths.Count) ? tb.ColumnWidths[col + 1] : 100;
                return true;
            }
        return false;
    }

    // Live column resize during a drag.
    private void ResizeColumn(Point pt)
    {
        if (_resizingColTable is not { } tb) return;
        PushDragUndoOnce(); // snapshot before the first actual write
        const double minW = 20;
        double diff = pt.X - _colResizeStartX;
        EnsureColumnWidths(tb);
        if (_resizingLastCol)
        {
            // Outer-right edge: grow/shrink this column, changing the table's total width.
            double w = Math.Max(minW, _initColW + diff);
            // A table inside a cell — nested, or inline in a cell's paragraph — is bound to that cell's content
            // box: growing its last column past it drew the table through the neighbouring cells (measured
            // 2026-09-15: a nested table in a 190px cell dragged to 480, an inline one to 420). Shrinking stays
            // free, and the cap never falls below the width the drag started from, so a table that already
            // overflows (a file says so) does not snap narrower the moment it is grabbed.
            if (EnclosingContentWidth(tb) is { } room)
            {
                double others = 0;
                for (int k = 0; k < tb.Columns; k++) if (k != _resizingColIndex) others += tb.ColumnWidths[k];
                w = Math.Min(w, Math.Max(_initColW, room - others));
            }
            tb.ColumnWidths[_resizingColIndex] = w;
        }
        else
        {
            // Internal edge: redistribute between the two adjacent columns, total fixed.
            diff = ClampColumnDelta(diff, _initColW, _initNextColW, minW);
            tb.ColumnWidths[_resizingColIndex] = _initColW + diff;
            tb.ColumnWidths[_resizingColIndex + 1] = _initNextColW - diff;
        }
        // Targeted invalidation: the resized table's rows reflow, and so do its ancestor tables' host
        // rows (nested/inline). Other tables are untouched — clearing ALL row caches made every pointer
        // move during a drag re-measure every table in the document.
        InvalidateTableMeasure(tb);
        RelayoutToViewport();
    }

    // How far an internal column boundary may move, given the two adjacent columns' start widths.
    // Normally each side keeps `minW`, which also means a sub-minimum column is snapped UP to the floor.
    //
    // The floor is unsatisfiable when the two together are narrower than 2*minW, and then the naive
    // bounds INVERT (min > max) — which Math.Clamp answers with ArgumentException, i.e. the drag crashed
    // the app instead of degrading. Sub-minimum widths are not exotic: HTML import keeps any positive
    // <td width>, RTF keeps anything >= 16px, `.flow`/JSON keep whatever the file says, and
    // InsertTable's own floor for a NESTED table is 15. Fall back to "keep the total, keep both sides
    // non-negative" there.
    internal static double ClampColumnDelta(double diff, double initColW, double initNextColW, double minW)
    {
        double minDiff = -(initColW - minW);
        double maxDiff = initNextColW - minW;
        if (minDiff > maxDiff) { minDiff = -initColW; maxDiff = initNextColW; }
        return Math.Clamp(diff, minDiff, maxDiff);
    }

    // The width a table inside a cell has to fit in: the cell's content box for a nested table, the host
    // paragraph's wrap width for an inline table in a cell's paragraph. Null anywhere else — a top-level table
    // may grow past the margin, as in upstream and Word. (Upstream caps the nested case the same way,
    // EnclosingCellInnerWidth.)
    private double? EnclosingContentWidth(TableBlock tb)
    {
        switch (tb.Parent)
        {
            case TableCell cell when cell.Parent is TableBlock outer:
                for (int r = 0; r < outer.Rows; r++)
                    for (int c = 0; c < outer.Columns; c++)
                        if (ReferenceEquals(outer.Cells[r][c], cell))
                        {
                            var (cs, _) = outer.SpanOf(r, c);
                            double w = 0;
                            for (int k = c; k < c + cs && k < outer.Columns; k++)
                                w += k < outer.ColumnWidths.Count ? outer.ColumnWidths[k] : 100;
                            return Math.Max(10, w - 2 * CellPad);
                        }
                return null;
            case InlineTable { Parent: Paragraph host } when host.Parent is TableCell:
                return ParagraphWrapWidth(host);
            default:
                return null;
        }
    }

    // Finish BEFORE releasing: the release raises PointerCaptureLost, whose handler must find nothing live.
    private bool EndColumnResize(PointerStep s)
    {
        if (!_resizingColumn) return false;
        FinishColumnResize();
        s.Capture.Release();
        return true;
    }

    private void FinishColumnResize()
    {
        _resizingColumn = false;
        _resizingColTable = null;
        _dragUndoPending = false; // released without dragging: nothing was pushed, nothing to keep armed
        RaiseStatusChanged();
    }

    private bool OverColumnBoundary(Point pt)
    {
        if (IsReadOnly) return false;
        foreach (var list in _columnBoundaries.Values)
            foreach (var (_, rect) in list)
                if (rect.Contains(pt)) return true;
        return false;
    }

    // Starts a row resize if the press lands on a row boundary. Returns true when it consumed it.
    private bool BeginRowResizeAt(Point pt)
    {
        if (IsReadOnly) return false;
        foreach (var (tb, list) in _rowBoundaries)
        foreach (var (row, height, rect) in list)
            if (rect.Contains(pt))
            {
                // No padding on press (see BeginColumnResizeAt); ResizeRow pads after its checkpoint.
                _dragUndoPending = true; // pushed on the first move (see PushDragUndoOnce)
                _resizingRow = true;
                _resizingRowTable = tb;
                _resizingRowIndex = row;
                _rowResizeStartY = pt.Y;
                _initRowH = height; // current rendered height (content- or user-driven)
                return true;
            }
        return false;
    }

    // Live row resize during a drag. RowHeights only grows a row past its content; shrinking is bounded
    // by the cell content height (LayoutTable takes the max), so this can't clip text.
    private void ResizeRow(Point pt)
    {
        if (_resizingRowTable is not { } tb) return;
        PushDragUndoOnce(); // snapshot before the first actual write
        EnsureRowHeights(tb);
        double diff = pt.Y - _rowResizeStartY;
        tb.RowHeights[_resizingRowIndex] = Math.Max(20, _initRowH + diff);
        InvalidateTableMeasure(tb); // this table + ancestor hosts only (see ResizeColumn)
        RelayoutToViewport();
    }

    private bool EndRowResize(PointerStep s)
    {
        if (!_resizingRow) return false;
        FinishRowResize(); // before the release (see EndColumnResize)
        s.Capture.Release();
        return true;
    }

    private void FinishRowResize()
    {
        _resizingRow = false;
        _resizingRowTable = null;
        _dragUndoPending = false;
        RaiseStatusChanged();
    }

    private bool OverRowBoundary(Point pt)
    {
        if (IsReadOnly) return false;
        foreach (var list in _rowBoundaries.Values)
            foreach (var (_, _, rect) in list)
                if (rect.Contains(pt)) return true;
        return false;
    }

    // True when the point is on a table's outer LEFT or TOP border band (its right/bottom edges are the
    // last column/row resize boundaries, so only left/top select the table as a block) — a top-level table,
    // or one nested in a cell (2026-09-13: those had no border at all). Innermost first: a table in a cell
    // wins over the table around it, whose band it overlaps by a cell's padding.
    private bool OnTableSelectBorder(Point pt, out TableBlock? table)
    {
        for (int i = _cellTableRects.Count - 1; i >= 0; i--)
            if (OnTableBand(_cellTableRects[i].rect, pt)) { table = _cellTableRects[i].tb; return true; }
        foreach (var (tb, o) in _tableRects)
            if (OnTableBand(o, pt)) { table = tb; return true; }
        table = null;
        return false;
    }

    private static bool OnTableBand(Rect o, Point pt)
    {
        bool onLeft = Math.Abs(pt.X - o.Left) <= TableBorderGrab && pt.Y >= o.Top - TableBorderGrab && pt.Y <= o.Bottom + TableBorderGrab;
        bool onTop = Math.Abs(pt.Y - o.Top) <= TableBorderGrab && pt.X >= o.Left - TableBorderGrab && pt.X <= o.Right + TableBorderGrab;
        return onLeft || onTop;
    }

    // Selects a whole table when its left/top border is clicked (Delete then removes it). A viewer too, since
    // 2026-09-13: it was edit-only because the chrome looked like a dead end there (Delete is gated), but a
    // viewer's selected table is what Copy takes, and a live check asked for a sign that the table — not the
    // text in it — is the target. The border band is narrow; a click inside the table still places the caret.
    private bool TrySelectTableBlock(Point pt)
    {
        if (!OnTableSelectBorder(pt, out var tb) || tb == null) return false;
        _selectedInline = null;
        _selectedBlock = tb;
        _isSelecting = false;
        CollapseSelectionToCaret();
        RestartBlink();
        InvalidateCanvas();
        RaiseStatusChanged();
        return true;
    }

    // Draws the selection chrome (border) around a selected table.
    private void DrawTableSelectionChrome(CanvasDrawingSession ds, TableBlock tb, double startX, double top, in TableLayout tl)
    {
        if (_printMode || !ReferenceEquals(_selectedBlock, tb)) return;
        var r = new Rect(startX - 1.5, top - 1.5, tl.TableWidth + 3, tl.TotalHeight + 3);
        // A top-level table's border lies outside the page's content clip where the table meets it (left margin 0):
        // drawn after the walk, under the paper's clip (see FlushOutsideChrome). A table in a cell stays inside it.
        if (tb.Parent is FlowDocument) _outsideChrome.Add(d => d.DrawRectangle(r, BlockSelBorder, 2.5f));
        else ds.DrawRectangle(r, BlockSelBorder, 2.5f);
    }
}
