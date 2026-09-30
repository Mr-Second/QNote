using System.Collections.Generic;

namespace WinUIRichEditor.Documents;

/// <summary>A grid of cells (each a <see cref="Paragraph"/>) supporting per-column widths, row heights,
/// and cell merging via colspan/rowspan. The grid is kept dense/rectangular; merged areas use an anchor
/// cell plus covered markers (see <see cref="ColSpans"/>/<see cref="RowSpans"/>).</summary>
public class TableBlock : Block
{
    /// <summary>Number of rows in the table.</summary>
    public int Rows { get; set; } = 2;
    /// <summary>Number of columns in the table.</summary>
    public int Columns { get; set; } = 2;
    /// <summary>Pixel widths of each column (parallel to column index).</summary>
    public List<double> ColumnWidths { get; set; } = new();
    /// <summary>User-specified minimum row heights in pixels. 0 = auto (content-driven).</summary>
    public List<double> RowHeights { get; set; } = new();
    /// <summary>The grid of cells ([row][column]). The grid is always dense/rectangular. Each
    /// <see cref="TableCell"/> holds a list of blocks (one paragraph through phases P1/P2).</summary>
    public List<List<TableCell>> Cells { get; set; } = new();

    /// <summary>Column-span values per cell ([row][column]).
    /// Anchor: ≥ 1. Covered by a merge: 0.</summary>
    public List<List<int>> ColSpans { get; set; } = new();
    /// <summary>Row-span values per cell ([row][column]).
    /// Anchor: ≥ 1. Covered by a merge: 0.</summary>
    public List<List<int>> RowSpans { get; set; } = new();

    /// <summary>Creates a 2×2 table.</summary>
    public TableBlock()
    {
        MarginTop = AutoTopMargin;
        InitializeCells(Rows, Columns);
    }

    /// <summary>Creates a <paramref name="rows"/>×<paramref name="cols"/> table.</summary>
    public TableBlock(int rows, int cols)
    {
        MarginTop = AutoTopMargin;
        Rows = rows;
        Columns = cols;
        InitializeCells(Rows, Columns);
    }

    private void InitializeCells(int rows, int cols)
    {
        for (int c = 0; c < cols; c++)
        {
            ColumnWidths.Add(100); // Default width
        }
        for (int r = 0; r < rows; r++)
        {
            var row = new List<TableCell>();
            var csRow = new List<int>();
            var rsRow = new List<int>();
            for (int c = 0; c < cols; c++)
            {
                row.Add(new TableCell { Parent = this });
                csRow.Add(1);
                rsRow.Add(1);
            }
            Cells.Add(row);
            ColSpans.Add(csRow);
            RowSpans.Add(rsRow);
        }
    }

    private TableCell NewCell() => new TableCell { Parent = this };

    // A table with its size declared but NO grid materialised — for the callers that fill every slot
    // themselves. `new TableBlock(0, 0)` runs InitializeCells over empty ranges, so this allocates the
    // four (empty) lists and nothing else.
    //
    // Clone and Extract used to ask for a fully built rows×cols grid and then discard all of it: each
    // slot arrives as a TableCell carrying a Paragraph carrying a Run, three objects per cell, none of
    // which survives the next few lines. Measured (Release, same-session A/B) on a table-heavy document
    // of 2000 cells: the discarded construction was 0.343ms and 1020KB of Clone's 1.486ms and 2606KB —
    // ~40% of both. Clone runs at every undo checkpoint (UndoManager.PushState), so that was ~1MB of
    // pure garbage per keystroke group. A prose document with one small table pays ~1%, which is why
    // this never surfaced in the general perf probes.
    private static TableBlock Skeleton(int rows, int cols)
    {
        var tb = new TableBlock(0, 0);
        tb.Rows = rows;
        tb.Columns = cols;
        return tb;
    }

    // ---- Span helpers ------------------------------------------------------

    private bool InBounds(int r, int c) => r >= 0 && r < Rows && c >= 0 && c < Columns;

    /// True when (r,c) is overlapped by a merged anchor and must be skipped in render/nav/hit-test.
    public bool IsCovered(int r, int c)
        => InBounds(r, c) && r < ColSpans.Count && c < ColSpans[r].Count && ColSpans[r][c] == 0;

    /// (colSpan, rowSpan) of an anchor; (1,1) for a plain cell. Covered cells report (0,0).
    public (int cs, int rs) SpanOf(int r, int c)
    {
        if (!InBounds(r, c) || r >= ColSpans.Count || c >= ColSpans[r].Count) return (1, 1);
        return (ColSpans[r][c], RowSpans[r][c]);
    }

    /// For any (r,c) — anchor or covered — returns the coordinates of the owning anchor.
    /// Scans up/left for the nearest anchor whose merged area contains (r,c).
    public (int ar, int ac) AnchorOf(int r, int c)
    {
        if (!IsCovered(r, c)) return (r, c);
        for (int ar = r; ar >= 0; ar--)
            for (int ac = c; ac >= 0; ac--)
            {
                var (cs, rs) = SpanOf(ar, ac);
                if (cs >= 1 && rs >= 1 && ar + rs - 1 >= r && ac + cs - 1 >= c)
                    return (ar, ac);
            }
        return (r, c); // shouldn't happen on a consistent grid
    }

    /// Row-major enumeration of logical (anchor) cells only — covered cells are skipped.
    /// Used by navigation, selection, and text extraction.
    public IEnumerable<(int r, int c, TableCell cell)> LogicalCells()
    {
        for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Columns; c++)
                if (!IsCovered(r, c))
                    yield return (r, c, Cells[r][c]);
    }

    // Stamps covered markers for an anchor's merged area; (anchorR,anchorC) stays the anchor.
    private void StampCovered(int anchorR, int anchorC, int cs, int rs)
    {
        for (int r = anchorR; r < anchorR + rs && r < Rows; r++)
            for (int c = anchorC; c < anchorC + cs && c < Columns; c++)
            {
                if (r == anchorR && c == anchorC) continue;
                ColSpans[r][c] = 0;
                RowSpans[r][c] = 0;
            }
    }

    // Defensive: clamp every anchor's span to the grid bounds. Cheap safety net after structural edits.
    private void ClampSpans()
    {
        for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Columns; c++)
            {
                if (ColSpans[r][c] >= 1 && RowSpans[r][c] >= 1)
                {
                    if (c + ColSpans[r][c] > Columns) ColSpans[r][c] = Columns - c;
                    if (r + RowSpans[r][c] > Rows) RowSpans[r][c] = Rows - r;
                }
            }
    }

    // ---- Structural edits --------------------------------------------------
    // Each keeps Rows/Columns, Cells, ColumnWidths, (sparse) RowHeights and the span grids consistent.
    // Merge handling: an anchor whose span *crosses* the insert/delete boundary grows/shrinks; cells in
    // the new row/column that fall inside a crossing anchor's area are marked covered.

    /// <summary>Inserts a new empty row before row index <paramref name="at"/>.</summary>
    public void InsertRow(int at)
    {
        at = System.Math.Clamp(at, 0, Rows);
        var row = new List<TableCell>();
        var csRow = new List<int>();
        var rsRow = new List<int>();
        for (int c = 0; c < Columns; c++) { row.Add(NewCell()); csRow.Add(1); rsRow.Add(1); }
        Cells.Insert(at, row);
        ColSpans.Insert(at, csRow);
        RowSpans.Insert(at, rsRow);
        if (at < RowHeights.Count) RowHeights.Insert(at, 0);
        Rows++;
        // Grow rowspan anchors that straddle the inserted row, and cover the new cells they reach.
        for (int r = 0; r < at; r++)
            for (int c = 0; c < Columns; c++)
            {
                var (cs, rs) = SpanOf(r, c);
                if (cs >= 1 && rs >= 1 && r + rs - 1 >= at)
                {
                    RowSpans[r][c] = rs + 1;
                    for (int cc = c; cc < c + cs && cc < Columns; cc++)
                    {
                        ColSpans[at][cc] = 0;
                        RowSpans[at][cc] = 0;
                    }
                }
            }
        ClampSpans();
    }

    /// <summary>Deletes row at index <paramref name="at"/>. No-op when the table has only one row.</summary>
    public void DeleteRow(int at)
    {
        if (Rows <= 1 || at < 0 || at >= Rows) return;
        // Promote anchors that live in this row but extend below: hand ownership to the next row.
        for (int c = 0; c < Columns; c++)
        {
            var (cs, rs) = SpanOf(at, c);
            if (cs >= 1 && rs >= 1 && rs > 1 && at + 1 < Rows)
            {
                Cells[at + 1][c] = Cells[at][c];      // keep the content
                Cells[at + 1][c].Parent = this;
                ColSpans[at + 1][c] = cs;
                RowSpans[at + 1][c] = rs - 1;
            }
        }
        // Shrink rowspan anchors above that cross this row.
        for (int r = 0; r < at; r++)
            for (int c = 0; c < Columns; c++)
            {
                var (cs, rs) = SpanOf(r, c);
                if (cs >= 1 && rs >= 1 && r + rs - 1 >= at) RowSpans[r][c] = rs - 1;
            }
        Cells.RemoveAt(at);
        ColSpans.RemoveAt(at);
        RowSpans.RemoveAt(at);
        if (at < RowHeights.Count) RowHeights.RemoveAt(at);
        Rows--;
        ClampSpans();
    }

    /// <summary>Inserts a new empty column before column index <paramref name="at"/>.</summary>
    public void InsertColumn(int at)
    {
        at = System.Math.Clamp(at, 0, Columns);
        for (int r = 0; r < Rows; r++)
        {
            Cells[r].Insert(at, NewCell());
            ColSpans[r].Insert(at, 1);
            RowSpans[r].Insert(at, 1);
        }
        // Pad up to `at` so the new width lands at the same index as the inserted cells; a clamp
        // alone would misplace it when ColumnWidths is shorter than the column count.
        while (ColumnWidths.Count < at) ColumnWidths.Add(100);
        ColumnWidths.Insert(at, 100);
        Columns++;
        // Grow colspan anchors that straddle the inserted column, and cover the new cells they reach.
        for (int c = 0; c < at; c++)
            for (int r = 0; r < Rows; r++)
            {
                var (cs, rs) = SpanOf(r, c);
                if (cs >= 1 && rs >= 1 && c + cs - 1 >= at)
                {
                    ColSpans[r][c] = cs + 1;
                    for (int rr = r; rr < r + rs && rr < Rows; rr++)
                    {
                        ColSpans[rr][at] = 0;
                        RowSpans[rr][at] = 0;
                    }
                }
            }
        ClampSpans();
    }

    /// <summary>Deletes column at index <paramref name="at"/>. No-op when the table has only one column.</summary>
    public void DeleteColumn(int at)
    {
        if (Columns <= 1 || at < 0 || at >= Columns) return;
        // Promote anchors that live in this column but extend right: hand ownership to the next column.
        for (int r = 0; r < Rows; r++)
        {
            var (cs, rs) = SpanOf(r, at);
            if (cs >= 1 && rs >= 1 && cs > 1 && at + 1 < Columns)
            {
                Cells[r][at + 1] = Cells[r][at];
                Cells[r][at + 1].Parent = this;
                ColSpans[r][at + 1] = cs - 1;
                RowSpans[r][at + 1] = rs;
            }
        }
        // Shrink colspan anchors to the left that cross this column.
        for (int c = 0; c < at; c++)
            for (int r = 0; r < Rows; r++)
            {
                var (cs, rs) = SpanOf(r, c);
                if (cs >= 1 && rs >= 1 && c + cs - 1 >= at) ColSpans[r][c] = cs - 1;
            }
        for (int r = 0; r < Rows; r++)
        {
            Cells[r].RemoveAt(at);
            ColSpans[r].RemoveAt(at);
            RowSpans[r].RemoveAt(at);
        }
        if (at < ColumnWidths.Count) ColumnWidths.RemoveAt(at);
        Columns--;
        ClampSpans();
    }

    /// Sets an anchor's span and stamps the covered cells it overlaps. Used by the HTML parser
    /// (placements are non-overlapping by construction). Clamps to grid bounds.
    public void SetSpan(int r, int c, int cs, int rs)
    {
        if (!InBounds(r, c)) return;
        cs = System.Math.Max(1, System.Math.Min(cs, Columns - c));
        rs = System.Math.Max(1, System.Math.Min(rs, Rows - r));
        ColSpans[r][c] = cs;
        RowSpans[r][c] = rs;
        StampCovered(r, c, cs, rs);
    }

    // ---- Merge / unmerge (driven by the editing UI) ------------------------

    /// Merges the rectangular block (r0..r1, c0..c1) into a single anchor at (r0,c0). Covered cells'
    /// content is preserved: paragraph inlines are appended to the anchor paragraph (space-separated),
    /// and non-paragraph blocks (images, nested tables, dividers) move into the anchor cell's block
    /// list instead of being discarded. No-op on a degenerate range.
    /// <para>A range that touches an existing merge is EXPANDED to contain that merge whole (repeatedly,
    /// until it straddles none) — the same rule a spreadsheet applies, and the only one that keeps the
    /// result a rectangle.</para>
    public void MergeCells(int r0, int c0, int r1, int c1)
    {
        if (!InBounds(r0, c0) || !InBounds(r1, c1)) return;
        if (r1 < r0 || c1 < c0) return;

        // Expand over every merge the range straddles. Without this the range could cut an existing
        // merge in half: stamping its anchor as covered leaves the cells THAT anchor owned pointing at
        // a covered cell, and shrinking the anchor at (r0,c0) leaves the cells it used to own flagged
        // covered with nothing covering them. Either way those cells are ORPHANS — LogicalCells() skips
        // them, so their content is unreachable to rendering, selection, every table command and every
        // formatter, and enough of them leave a table with no logical cells at all. A covered slot
        // reports (0,0) and is therefore skipped here: its anchor is elsewhere in the grid and pulls
        // the range over on its own iteration.
        bool grew = true;
        while (grew)
        {
            grew = false;
            for (int r = 0; r < Rows; r++)
                for (int c = 0; c < Columns; c++)
                {
                    var (cs, rs) = SpanOf(r, c);
                    if (cs <= 1 && rs <= 1) continue;          // plain cell or covered slot
                    if (cs < 1 || rs < 1) continue;
                    int endR = r + rs - 1, endC = c + cs - 1;
                    if (r > r1 || endR < r0 || c > c1 || endC < c0) continue; // disjoint
                    if (r < r0) { r0 = r; grew = true; }
                    if (c < c0) { c0 = c; grew = true; }
                    if (endR > r1) { r1 = endR; grew = true; }
                    if (endC > c1) { c1 = endC; grew = true; }
                }
        }
        if (r0 == r1 && c0 == c1) return; // a single plain cell: nothing to merge

        // Release the whole range to plain cells before re-stamping. Expansion guarantees every anchor
        // that reaches into the range lies entirely inside it, so this cannot orphan anything outside.
        for (int r = r0; r <= r1; r++)
            for (int c = c0; c <= c1; c++) { ColSpans[r][c] = 1; RowSpans[r][c] = 1; }

        var anchorCell = Cells[r0][c0];
        var anchorPara = anchorCell.Para;
        for (int r = r0; r <= r1; r++)
            for (int c = c0; c <= c1; c++)
            {
                if (r == r0 && c == c0) continue;
                var src = Cells[r][c];
                foreach (var blk in src.Blocks)
                {
                    if (blk is Paragraph sp)
                    {
                        bool hasContent = false;
                        foreach (var inl in sp.Inlines)
                            if (inl is not Run run || !string.IsNullOrEmpty(run.Text)) { hasContent = true; break; }
                        if (!hasContent) continue;
                        anchorPara.Inlines.Add(new Run { Text = " ", Parent = anchorPara });
                        foreach (var inl in sp.Inlines) { inl.Parent = anchorPara; anchorPara.Inlines.Add(inl); }
                        sp.Inlines.Clear();
                    }
                    else
                    {
                        blk.Parent = anchorCell;
                        anchorCell.Blocks.Add(blk);
                    }
                }
                src.Blocks.Clear();
                src.Blocks.Add(new Paragraph { Parent = src, Inlines = { new Run { Text = "" } } });
            }
        ColSpans[r0][c0] = c1 - c0 + 1;
        RowSpans[r0][c0] = r1 - r0 + 1;
        StampCovered(r0, c0, c1 - c0 + 1, r1 - r0 + 1);
    }

    /// Splits a merged anchor back into 1×1 cells (covered cells become empty anchors).
    public void UnmergeCell(int r, int c)
    {
        var (cs, rs) = SpanOf(r, c);
        if (cs <= 1 && rs <= 1) return;
        for (int rr = r; rr < r + rs && rr < Rows; rr++)
            for (int cc = c; cc < c + cs && cc < Columns; cc++)
            {
                ColSpans[rr][cc] = 1;
                RowSpans[rr][cc] = 1;
                if (!(rr == r && cc == c))
                    Cells[rr][cc] = new TableCell { Parent = this };
            }
    }

    /// <summary>Ensures <see cref="ColSpans"/>/<see cref="RowSpans"/> exactly match the <see cref="Cells"/>
    /// grid dimensions, filling gaps with 1 (plain cell), and that their values describe the grid: every merge
    /// inside it, no two merges overlapping, and every covered cell inside a merge. Safe to call after
    /// deserialization; a consistent table is left exactly as it was.</summary>
    public void EnsureSpanConsistency()
    {
        EnsureSpanShape();
        NormalizeSpanValues();
    }

    private void EnsureSpanShape()
    {
        while (ColSpans.Count < Cells.Count) ColSpans.Add(new List<int>());
        while (RowSpans.Count < Cells.Count) RowSpans.Add(new List<int>());
        if (ColSpans.Count > Cells.Count) ColSpans.RemoveRange(Cells.Count, ColSpans.Count - Cells.Count);
        if (RowSpans.Count > Cells.Count) RowSpans.RemoveRange(Cells.Count, RowSpans.Count - Cells.Count);
        for (int r = 0; r < Cells.Count; r++)
        {
            int cols = Cells[r].Count;
            while (ColSpans[r].Count < cols) ColSpans[r].Add(1);
            while (RowSpans[r].Count < cols) RowSpans[r].Add(1);
            if (ColSpans[r].Count > cols) ColSpans[r].RemoveRange(cols, ColSpans[r].Count - cols);
            if (RowSpans[r].Count > cols) RowSpans[r].RemoveRange(cols, RowSpans[r].Count - cols);
        }
    }

    // The span VALUES, made a consistent description of the grid. A file's spans were trusted as they came: a
    // negative span took the editor down on load (IndexOutOfRangeException), a covered slot no merge covered —
    // "0,0" everywhere, or a "0,1" pair — hid its cell's text from the editor and every export, and an
    // overflowing or overlapping merge drew over its neighbours (measured 2026-09-14). The rules, in order:
    //   * anything but an anchor (both spans ≥ 1) or a covered slot (both 0) is a plain cell;
    //   * an anchor is clamped to the grid;
    //   * merges are claimed in row-major order. One that would reach a slot already claimed, or another anchor,
    //     shrinks to 1×1 — so no anchor, and so no cell's text, ends up hidden under a merge. (A merge only ever
    //     claims covered slots, so an anchor's own slot is never claimed before its turn: a first draft also
    //     undid "the earlier merge over an anchor", and falsification showed that path unreachable.)
    //   * a covered slot no merge claimed becomes a plain cell.
    // One pass over the grid, so a hostile table cannot make it quadratic.
    private void NormalizeSpanValues()
    {
        int rows = Cells.Count;
        bool InGrid(int r, int c) => r < rows && c < Cells[r].Count;
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < Cells[r].Count; c++)
            {
                int cs = ColSpans[r][c], rs = RowSpans[r][c];
                if ((cs >= 1 && rs >= 1) || (cs == 0 && rs == 0)) continue;
                ColSpans[r][c] = 1;
                RowSpans[r][c] = 1;
            }

        var owner = new (int r, int c)?[rows][];
        for (int r = 0; r < rows; r++) owner[r] = new (int r, int c)?[Cells[r].Count];

        for (int r = 0; r < rows; r++)
            for (int c = 0; c < Cells[r].Count; c++)
            {
                int cs = ColSpans[r][c], rs = RowSpans[r][c];
                if (cs == 0) continue; // a covered slot: claimed by the merge over it, or an orphan below
                cs = System.Math.Min(cs, Cells[r].Count - c);
                rs = System.Math.Min(rs, rows - r);
                bool clash = false;
                for (int rr = r; rr < r + rs && !clash; rr++)
                    for (int cc = c; cc < c + cs && !clash; cc++)
                        if (!(rr == r && cc == c) && (!InGrid(rr, cc) || owner[rr][cc] != null || ColSpans[rr][cc] != 0))
                            clash = true;
                if (clash) { cs = 1; rs = 1; }
                ColSpans[r][c] = cs;
                RowSpans[r][c] = rs;
                for (int rr = r; rr < r + rs; rr++)
                    for (int cc = c; cc < c + cs; cc++)
                        owner[rr][cc] = (r, c);
            }

        for (int r = 0; r < rows; r++)
            for (int c = 0; c < Cells[r].Count; c++)
                if (ColSpans[r][c] == 0 && owner[r][c] == null)
                {
                    ColSpans[r][c] = 1;
                    RowSpans[r][c] = 1;
                }
    }

    /// <summary>Extracts the rectangle [<paramref name="r0"/>..<paramref name="r1"/>]×[<paramref name="c0"/>..
    /// <paramref name="c1"/>] into a standalone table, preserving column widths, row heights, cell content,
    /// and any merge whose anchor sits inside the rectangle (clamped to the sub-grid). A merge cut by the
    /// rectangle boundary (its anchor lies outside) degrades to plain 1×1 cells so the result stays a
    /// consistent grid. Coordinates are clamped to the grid.
    /// <para>An empty grid (no rows or no columns) extracts to an empty table: clamping into a grid with
    /// no cells has no meaningful answer, and <c>Math.Clamp(x, 0, -1)</c> throws.</para></summary>
    public TableBlock Extract(int r0, int c0, int r1, int c1)
    {
        if (Rows <= 0 || Columns <= 0) return new TableBlock(0, 0);
        EnsureSpanConsistency();
        r0 = System.Math.Clamp(r0, 0, Rows - 1); r1 = System.Math.Clamp(r1, 0, Rows - 1);
        c0 = System.Math.Clamp(c0, 0, Columns - 1); c1 = System.Math.Clamp(c1, 0, Columns - 1);
        if (r1 < r0) (r0, r1) = (r1, r0);
        if (c1 < c0) (c0, c1) = (c1, c0);
        int rows = r1 - r0 + 1, cols = c1 - c0 + 1;
        var sub = Skeleton(rows, cols);

        for (int c = c0; c <= c1; c++) sub.ColumnWidths.Add(c < ColumnWidths.Count ? ColumnWidths[c] : 100);
        for (int r = r0; r <= r1; r++) sub.RowHeights.Add(r < RowHeights.Count ? RowHeights[r] : 0);

        // Clone every cell (anchor or covered) into place as a plain 1×1 cell first. Rows are BUILT here
        // rather than assigned into a pre-made grid — see Skeleton for why there is no grid to assign into.
        for (int r = r0; r <= r1; r++)
        {
            var row = new List<TableCell>(cols);
            var colSpans = new List<int>(cols);
            var rowSpans = new List<int>(cols);
            for (int c = c0; c <= c1; c++)
            {
                var cell = Cells[r][c].Clone() as TableCell ?? new TableCell();
                cell.Parent = sub;
                row.Add(cell);
                colSpans.Add(1);
                rowSpans.Add(1);
            }
            sub.Cells.Add(row);
            sub.ColSpans.Add(colSpans);
            sub.RowSpans.Add(rowSpans);
        }

        // Re-apply merges for anchors inside the rectangle, clamped to the sub-grid (SetSpan stamps the
        // covered cells). Covered cells whose anchor lies outside stay independent 1×1 cells.
        for (int r = r0; r <= r1; r++)
            for (int c = c0; c <= c1; c++)
            {
                var (cs, rs) = SpanOf(r, c);
                if (cs >= 1 && rs >= 1 && (cs > 1 || rs > 1))
                    sub.SetSpan(r - r0, c - c0, System.Math.Min(cs, cols - (c - c0)), System.Math.Min(rs, rows - (r - r0)));
            }
        return sub;
    }

    /// <inheritdoc/>
    public override TextElement Clone()
    {
        EnsureSpanConsistency();
        // Skeleton, not `new TableBlock(Rows, Columns)`: every slot that constructor builds was cleared
        // away on the next four lines. See Skeleton for the measurement.
        var tb = Skeleton(Rows, Columns);
        tb.Indent = Indent;
        tb.MarginTop = MarginTop;
        tb.MarginBottom = MarginBottom;
        foreach (var w in ColumnWidths) tb.ColumnWidths.Add(w);
        foreach (var h in RowHeights) tb.RowHeights.Add(h);

        for (int r = 0; r < Rows; r++)
        {
            var row = new List<TableCell>(Columns);
            for (int c = 0; c < Columns; c++)
            {
                var cClone = Cells[r][c].Clone() as TableCell ?? new TableCell();
                cClone.Parent = tb;
                row.Add(cClone);
            }
            tb.Cells.Add(row);
            tb.ColSpans.Add(new List<int>(ColSpans[r]));
            tb.RowSpans.Add(new List<int>(RowSpans[r]));
        }
        return tb;
    }
}
