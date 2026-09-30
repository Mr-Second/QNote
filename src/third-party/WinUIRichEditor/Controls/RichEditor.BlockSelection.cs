using System;
using System.Collections.Generic;
using Windows.Foundation;
using Windows.UI;
using Microsoft.UI;
using Microsoft.UI.Xaml.Input;
using Microsoft.Graphics.Canvas;
using WinUIRichEditor.Documents;

namespace WinUIRichEditor.Controls;

// Phase 5: block-image selection chrome (border + resize handles), drag-resize (corner aspect-locked,
// right/bottom edge one axis — see ResizeGrip), and delete. A top-level block image is selected by clicking it; the selection is cleared by
// any other press or key. Mirrors the Avalonia original's _selectedBlock / image-resize behavior.
public partial class RichEditor
{
    private Block? _selectedBlock;          // currently selected block (top-level ImageBlock or TableBlock)
    private (Paragraph p, InlineImage img)? _selectedInline; // currently selected inline image
    private (Paragraph host, InlineTable it)? _selectedInlineTable; // currently selected inline table
    private ImageBlock? _resizingImage;     // block image being drag-resized, if any
    private InlineImage? _resizingInline;   // inline image being drag-resized, if any
    private double _imageAspect;            // width/height captured at resize start (aspect lock)
    private double _resizeStartX;           // pointer x at resize start
    private double _resizeStartW;           // image width at resize start
    private double _resizeStartY;           // pointer y at resize start
    private double _resizeStartH;           // image height at resize start
    private ResizeGrip _resizeGrip;         // which handle the resize was started from

    /// <summary>A selected picture's handles: the corner keeps its proportions (as it always did), the
    /// middle of the right edge changes only the width and the middle of the bottom edge only the height —
    /// Word's arrangement, and the only way to change a picture's proportions without a file.</summary>
    internal enum ResizeGrip { None, Corner, Right, Bottom }

    // Inline-object rects in document space, recorded whenever an object draws (keyed by object
    // identity, so partial-region redraws just overwrite) and kept until the next relayout drops them
    // (ClearRecordedGeometry — positions may have moved). Doc-space rects survive scrolling. This
    // replaces the old rebuild-document-wide-every-draw scheme, which forced the render walk to lay out
    // every atomic-inline paragraph and table per frame. Interaction needs visibility, so an object is
    // always drawn (and thus recorded) before it can be clicked.
    private readonly Dictionary<InlineImage, (Paragraph p, Rect rect)> _inlineImageRects = new();
    private readonly Dictionary<InlineTable, (Paragraph host, Rect rect)> _inlineTableRects = new();
    // Rendered rects of block images inside table CELLS. Top-level block images come from the block
    // layout map (BlockImageRects); a cell image is not in that map at all, so without this registry it
    // could not be clicked, selected, resized or deleted — at any nesting depth. The recorded rect is the
    // size the image was DRAWN at: a cell scales a picture down to fit its width (CellImageSize), so the
    // declared Width is normally several times larger (insertion caps to the document width, not the
    // cell's). That is also why the drag below is measured against this rect and not against Width —
    // seeding it from the declared size puts every realistic drag inside the range that still clamps to
    // the same drawn width, so the handle looks dead.
    private readonly Dictionary<ImageBlock, Rect> _cellImageRects = new();

    private const double ResizeHandleSize = 11;
    private const double ResizeGrab = 14;   // grab radius around the corner

    private static readonly Color BlockSelBorder = Color.FromArgb(255, 0, 120, 215);
    private static readonly Color BlockHandleFill = Color.FromArgb(255, 0, 120, 215);

    /// <summary>True when a block/inline image or an inline table is selected as an object.</summary>
    public bool HasBlockSelection => _selectedBlock != null || _selectedInline != null || _selectedInlineTable != null;

    private void ClearObjectSelection() { _selectedBlock = null; _selectedInline = null; _selectedInlineTable = null; }

    // Lets go of a selected object that an edit took out of the document. Pointer, key and menu paths clear the
    // selection before they edit, but a host call does not: DeleteTableRow around a selected nested table or
    // picture left it selected, and Delete then removed the inline picture from the detached row and parked the
    // caret there — outside the document (2026-09-24). Checked by walking DOWN from the document: a detached
    // node still names its old parent, and a deleted row's cells still name the table, so a parent chain would
    // call it inside (see TableIsInDocument).
    private void DropDetachedObjectSelection()
    {
        if (!HasBlockSelection) return;
        if (Document == null) { ClearObjectSelection(); return; }
        HashSet<Block>? inDoc = null;
        bool InDoc(Block b) => (inDoc ??= new HashSet<Block>(BlockWalk.DocumentOrder(Document.Blocks),
                                                              ReferenceEqualityComparer.Instance)).Contains(b);
        if (_selectedBlock is { } blk && !InDoc(blk)) _selectedBlock = null;
        if (_selectedInline is { } si && !(InDoc(si.p) && si.p.Inlines.Contains(si.img))) _selectedInline = null;
        if (_selectedInlineTable is { } st && !(InDoc(st.host) && st.host.Inlines.Contains(st.it))) _selectedInlineTable = null;
    }

    // Records an inline table's rect for hit-testing and draws its selection chrome if selected.
    private void TrackInlineTable(CanvasDrawingSession ds, Paragraph host, InlineTable it, Rect rect)
    {
        if (_printMode) return; // print renders must not touch the screen hit-test geometry
        _inlineTableRects[it] = (host, rect);
        if (_selectedInlineTable is { } s && ReferenceEquals(s.it, it))
            ds.DrawRectangle(new Rect(rect.X - 1.5, rect.Y - 1.5, rect.Width + 3, rect.Height + 3), BlockSelBorder, 2.5f);
    }

    // Selects a whole inline table when its left/top border is clicked (a click inside descends into a
    // cell instead, via HitInlineTable). The grab band reuses the block-table border width.
    // A viewer too: there the selection is what Copy (Ctrl+C, the right-click menu) takes — the whole table.
    private bool TrySelectInlineTable(Point pt)
    {
        foreach (var (it, v) in _inlineTableRects)
            if (OnEdgeBorder(v.rect, pt))
            {
                ClearObjectSelection();
                _selectedInlineTable = (v.host, it);
                _isSelecting = false;
                CollapseSelectionToCaret();
                RestartBlink();
                InvalidateCanvas();
                RaiseStatusChanged();
                return true;
            }
        return false;
    }

    private bool OverInlineTableBorder(Point pt)
    {
        foreach (var v in _inlineTableRects.Values)
            if (OnEdgeBorder(v.rect, pt)) return true;
        return false;
    }

    // True when pt is on a rect's outer LEFT or TOP border band (shared by inline-table selection).
    private static bool OnEdgeBorder(Rect o, Point pt)
    {
        const double grab = 5;
        bool onLeft = Math.Abs(pt.X - o.Left) <= grab && pt.Y >= o.Top - grab && pt.Y <= o.Bottom + grab;
        bool onTop = Math.Abs(pt.Y - o.Top) <= grab && pt.X >= o.Left - grab && pt.X <= o.Right + grab;
        return onLeft || onTop;
    }

    // Removes the selected inline table from its host paragraph, dropping the caret where it sat.
    private void DeleteSelectedInlineTable()
    {
        if (Document == null || _selectedInlineTable is not { } sel) return;
        PushUndo(null);
        var (host, it) = sel;
        _selectedInlineTable = null;
        int idx = host.Inlines.IndexOf(it);
        if (idx >= 0)
        {
            int off = 0;
            for (int i = 0; i < idx; i++) off += InlineLen(host.Inlines[i]);
            host.Inlines.RemoveAt(idx);
            if (host.Inlines.Count == 0) host.Inlines.Add(new Run { Text = "" });
            _caret = new TextPointer(host, Math.Min(off, GetParagraphLength(host)));
            CollapseSelectionToCaret();
        }
        UpdateParents(Document);
        AfterEdit();
    }

    // Begins a block-image interaction at the press point: a drag on the selected image's corner handle
    // starts a resize; a click inside any block image selects it. Returns true when it consumed the press.
    // What a pointer press on the canvas lands on. Split out of TryBeginImageInteraction because that
    // method needs a PointerRoutedEventArgs — a WinRT type with no public constructor — while the ORDER
    // of these tests is a contract that has already cost a defect once.
    internal enum PointerTarget { None, SelectedBlockImageHandle, SelectedInlineImageHandle, CellImage, InlineImage, BlockImage }

    /// <summary>Priority order for a pointer press, and the reasons it is this order:
    /// <list type="bullet">
    /// <item>EVERY resize-handle test runs before EVERY selection click. A handle's grab band extends
    /// OUTSIDE its own rect, so it can land inside a neighbouring object; if selection went first the
    /// neighbour would swallow the grab and the handle would be unusable.</item>
    /// <item>Handles only exist while editing, so read-only skips straight to selection.</item>
    /// <item>A cell-hosted image is tested before a top-level one: it is drawn inside a table that also
    /// covers the point, so the innermost target has to win.</item>
    /// <item>An inline image beats a block image — it sits within text, where the block hit-test is
    /// coarser.</item>
    /// </list></summary>
    internal static PointerTarget ChoosePointerTarget(
        bool isReadOnly,
        bool onSelectedBlockImageHandle, bool onSelectedInlineImageHandle,
        bool inCellImage, bool inInlineImage, bool inBlockImage)
    {
        if (!isReadOnly && onSelectedBlockImageHandle) return PointerTarget.SelectedBlockImageHandle;
        if (!isReadOnly && onSelectedInlineImageHandle) return PointerTarget.SelectedInlineImageHandle;
        if (inCellImage) return PointerTarget.CellImage;
        if (inInlineImage) return PointerTarget.InlineImage;
        if (inBlockImage) return PointerTarget.BlockImage;
        return PointerTarget.None;
    }

    private bool TryBeginImageInteraction(Point pt, PointerStep s)
    {
        // Hit-test everything first, then let ChoosePointerTarget decide. The drag is seeded from the
        // rect the handle was DRAWN at, for both registries — see _cellImageRects for why.
        var handle = SelectedGripAt(pt);

        ImageBlock? cellImage = null;
        foreach (var (img, rect) in _cellImageRects)
            if (rect.Contains(pt)) { cellImage = img; break; }

        (Paragraph p, InlineImage img)? inlineImage = null;
        foreach (var (img, v) in _inlineImageRects)
            if (v.rect.Contains(pt)) { inlineImage = (v.p, img); break; }

        ImageBlock? blockImage = null;
        foreach (var (img, rect) in BlockImageRects())
            if (rect.Contains(pt)) { blockImage = img; break; }

        void SelectObject(ImageBlock? block, (Paragraph p, InlineImage img)? inline)
        {
            _selectedBlock = block;
            _selectedInline = inline;
            _isSelecting = false;
            CollapseSelectionToCaret();
            RestartBlink();
            InvalidateCanvas();
            RaiseStatusChanged();
        }

        switch (ChoosePointerTarget(IsReadOnly,
                    handle.grip != ResizeGrip.None && !handle.inline, handle.grip != ResizeGrip.None && handle.inline,
                    cellImage != null, inlineImage != null, blockImage != null))
        {
            case PointerTarget.SelectedBlockImageHandle:
            case PointerTarget.SelectedInlineImageHandle:
                BeginImageResize(handle.grip, handle.rect, handle.inline, pt);
                s.Capture.Capture();
                return true;
            // A press on the picture itself selects it and arms dragging it (RichEditor.DragBlock.cs).
            case PointerTarget.CellImage:  SelectObject(cellImage, null); ArmObjectDrag(cellImage, pt, s); return true;
            case PointerTarget.InlineImage: SelectObject(null, inlineImage); ArmObjectDrag(inlineImage!.Value.img, pt, s); return true;
            case PointerTarget.BlockImage: SelectObject(blockImage, null); ArmObjectDrag(blockImage, pt, s); return true;
            default: return false;
        }
    }

    /// <summary>Starts resizing the selected picture from the handle under <paramref name="pt"/>, as a press
    /// there does — without the pointer capture, so a test can drive it. False when no handle is there.</summary>
    internal bool BeginImageResizeAt(Point pt)
    {
        if (IsReadOnly) return false;
        var (grip, rect, inline) = SelectedGripAt(pt);
        if (grip == ResizeGrip.None) return false;
        BeginImageResize(grip, rect, inline, pt);
        return true;
    }

    // Everything is seeded from the rect the picture was DRAWN at (see _cellImageRects): a cell draws a
    // picture scaled down to its width, and a drag has to move the edge that is under the pointer.
    private void BeginImageResize(ResizeGrip grip, Rect rect, bool inline, Point pt)
    {
        _dragUndoPending = true; // pushed on the first move (see PushDragUndoOnce)
        _resizeGrip = grip;
        if (inline)
        {
            var img = _selectedInline!.Value.img;
            _resizingInline = img;
            _resizeStartW = img.Width > 0 ? img.Width : rect.Width;
            _resizeStartH = img.Height > 0 ? img.Height : rect.Height;
        }
        else
        {
            _resizingImage = (ImageBlock)_selectedBlock!;
            _resizeStartW = rect.Width;
            _resizeStartH = rect.Height;
        }
        _imageAspect = _resizeStartH > 0 ? _resizeStartW / _resizeStartH : 1;
        _resizeStartX = pt.X;
        _resizeStartY = pt.Y;
    }

    // A picture this tall is well past any page; the cap only stops a runaway drag.
    private const double MaxImageHeight = 10000;

    // Live image resize during a pointer drag. Returns true while a resize is active. The corner keeps the
    // proportions the picture had when the drag began; an edge handle changes one side and keeps the other
    // at its size as drawn.
    private bool TryResizeImage(Point pt)
    {
        if (_resizingImage == null && _resizingInline == null) return false;
        PushDragUndoOnce(); // snapshot before the first actual write
        double min = _resizingImage != null ? 24 : 16;
        double w = _resizeStartW, h = _resizeStartH;
        if (_resizeGrip != ResizeGrip.Bottom)
            w = Math.Clamp(_resizeStartW + pt.X - _resizeStartX, min, Math.Max(min, _layoutWidth - 40));
        if (_resizeGrip == ResizeGrip.Bottom)
            h = Math.Clamp(_resizeStartH + pt.Y - _resizeStartY, min, MaxImageHeight);
        else if (_resizeGrip == ResizeGrip.Corner && _imageAspect > 0)
            h = w / _imageAspect;

        if (_resizingImage != null)
        {
            _resizingImage.Width = w;
            _resizingImage.Height = h;
            // A block image in a cell sizes that cell, and so the row. Evict the enclosing table chain
            // the way the column/row drags do, or the row only grows after the NEXT edit.
            if (_resizingImage.Parent is TableCell rc && rc.Parent is TableBlock rtb) InvalidateTableMeasure(rtb);
        }
        else
        {
            _resizingInline!.Width = w;
            _resizingInline.Height = h;
            _tableRowHeights.Clear(); // an inline image may live in a table cell — re-measure rows
        }
        RelayoutToViewport();
        return true;
    }

    private bool EndImageResize(PointerStep s)
    {
        if (_resizingImage == null && _resizingInline == null) return false;
        FinishImageResize(); // before the release (see EndColumnResize)
        s.Capture.Release();
        return true;
    }

    private void FinishImageResize()
    {
        _resizingImage = null;
        _resizingInline = null;
        _dragUndoPending = false; // released without dragging: nothing was pushed
        RaiseStatusChanged();
    }

    /// <summary>The handle of a picture drawn at <paramref name="rect"/> under <paramref name="pt"/>. The corner
    /// is tested first: on a small picture the three grab areas overlap, and the corner is the one that
    /// existed before the edge handles did.</summary>
    internal static ResizeGrip GripAt(Rect rect, Point pt)
    {
        bool nearRight = Math.Abs(pt.X - rect.Right) <= ResizeGrab, nearBottom = Math.Abs(pt.Y - rect.Bottom) <= ResizeGrab;
        if (nearRight && nearBottom) return ResizeGrip.Corner;
        if (nearRight && Math.Abs(pt.Y - (rect.Top + rect.Height / 2)) <= ResizeGrab) return ResizeGrip.Right;
        if (nearBottom && Math.Abs(pt.X - (rect.Left + rect.Width / 2)) <= ResizeGrab) return ResizeGrip.Bottom;
        return ResizeGrip.None;
    }

    private static Microsoft.UI.Input.InputSystemCursorShape GripCursor(ResizeGrip grip) => grip switch
    {
        ResizeGrip.Right => Microsoft.UI.Input.InputSystemCursorShape.SizeWestEast,
        ResizeGrip.Bottom => Microsoft.UI.Input.InputSystemCursorShape.SizeNorthSouth,
        _ => Microsoft.UI.Input.InputSystemCursorShape.SizeNorthwestSoutheast,
    };

    // The handle under the point on the SELECTED picture (block, cell or inline), with the rect it was drawn at.
    private (ResizeGrip grip, Rect rect, bool inline) SelectedGripAt(Point pt)
    {
        if (_selectedBlock is ImageBlock selB)
            foreach (var rect in BlockImageHandleRects(selB)) // top-level map + cell registry
                if (GripAt(rect, pt) is var g and not ResizeGrip.None) return (g, rect, false);
        if (_selectedInline is { } selI && _inlineImageRects.TryGetValue(selI.img, out var ir)
            && GripAt(ir.rect, pt) is var gi and not ResizeGrip.None) return (gi, ir.rect, true);
        return (ResizeGrip.None, default, false);
    }

    // Deletes whichever image object is selected (inline takes priority).
    private void DeleteSelectedObject()
    {
        if (_selectedInlineTable != null) DeleteSelectedInlineTable();
        else if (_selectedInline != null) DeleteSelectedInline();
        else if (_selectedBlock != null) DeleteSelectedBlock();
    }

    // Removes the selected inline image from its host paragraph, dropping the caret where it sat.
    private void DeleteSelectedInline()
    {
        if (Document == null || _selectedInline is not { } sel) return;
        PushUndo(null);
        var (p, img) = sel;
        _selectedInline = null;
        int idx = p.Inlines.IndexOf(img);
        if (idx >= 0)
        {
            int off = 0;
            for (int i = 0; i < idx; i++) off += InlineLen(p.Inlines[i]);
            p.Inlines.RemoveAt(idx);
            if (p.Inlines.Count == 0) p.Inlines.Add(new Run { Text = "" });
            _caret = new TextPointer(p, Math.Min(off, GetParagraphLength(p)));
            CollapseSelectionToCaret();
        }
        UpdateParents(Document);
        AfterEdit();
    }

    // Deletes the selected block image (top-level), placing the caret on the nearest paragraph.
    private void DeleteSelectedBlock()
    {
        if (Document == null || _selectedBlock is not { } blk) return;
        PushUndo(null);
        _selectedBlock = null;
        int idx = Document.Blocks.IndexOf(blk);
        if (idx < 0)
        {
            // A block inside a table cell — an image, or (since its border selects it, 2026-09-13) a table
            // nested there. The caret moves to that cell: it may have been INSIDE the removed table, and would
            // otherwise point at a paragraph no longer in the document.
            var cellBlocks = BlockContainerOf(blk);
            RemoveBlockAnywhere(blk);
            UpdateParents(Document);
            var landing = (cellBlocks != null ? ParagraphsInBlocks(cellBlocks).FirstOrDefault() : null) ?? FirstParagraph();
            if (landing != null) { _caret = new TextPointer(landing, 0); CollapseSelectionToCaret(); }
            AfterEdit();
            return;
        }
        Document.Blocks.RemoveAt(idx);
        UpdateParents(Document); // re-normalizes so a paragraph borders the gap

        var blocks = Document.Blocks;
        Paragraph? target = null;
        for (int i = Math.Min(idx, blocks.Count - 1); i >= 0 && i < blocks.Count; i++)
            if (blocks[i] is Paragraph pp) { target = pp; break; }
        target ??= FirstParagraph();
        if (target != null) { _caret = new TextPointer(target, 0); CollapseSelectionToCaret(); }
        AfterEdit();
    }

    // The drawn rect(s) a given block image's resize handle can sit on: the block layout map for a
    // top-level image, the cell registry for one inside a table cell (which is not in that map).
    private IEnumerable<Rect> BlockImageHandleRects(ImageBlock target)
    {
        if (_cellImageRects.TryGetValue(target, out var cellRect)) yield return cellRect;
        foreach (var (img, rect) in BlockImageRects())
            if (ReferenceEquals(img, target)) yield return rect;
    }

    // Block image rects in document space, straight off the block layout map (no advance walk).
    private IEnumerable<(ImageBlock img, Rect rect)> BlockImageRects()
    {
        if (Document == null) yield break;
        foreach (var (block, y, h, _) in EnsureBlockLayout(_layoutWidth))
            if (block is ImageBlock img)
                yield return (img, new Rect(10 + img.Indent, y, BlockImageDims(img).w, h));
    }

    // Draws the selection border + bottom-right resize handle over a selected block image.
    private void DrawBlockImageChrome(CanvasDrawingSession ds, ImageBlock img, Rect rect)
    {
        if (_printMode || !ReferenceEquals(_selectedBlock, img)) return;
        // Queued, not drawn: the border lies outside the picture and the walk runs under the page's content clip
        // (see DrawPagedDocument).
        _outsideChrome.Add(d => DrawSelectionChrome(d, rect));
    }

    // Selection chrome that lies OUTSIDE its object — a top-level picture's or table's border — queued by the walk
    // in its coordinates and drawn after it: in page view the walk runs under the page's content clip, and an
    // object on the content box's edge lost that side of its border (a table at left margin 0: all of its left
    // line — reported from the demo, 2026-09-24; a picture opening a page: its top line, upstream PR #52).
    private readonly System.Collections.Generic.List<Action<CanvasDrawingSession>> _outsideChrome = new();

    private void FlushOutsideChrome(CanvasDrawingSession ds)
    {
        foreach (var draw in _outsideChrome) draw(ds);
        _outsideChrome.Clear();
    }

    // Records a cell-hosted block image's DRAWN rect for hit-testing and draws its selection chrome if
    // selected. Called from the cell block walk, which is the only place that knows the scaled size.
    private void TrackCellImage(CanvasDrawingSession ds, ImageBlock img, Rect rect)
    {
        if (_printMode) return; // print renders must not touch the screen hit-test geometry
        _cellImageRects[img] = rect;
        if (ReferenceEquals(_selectedBlock, img)) DrawSelectionChrome(ds, rect);
    }

    // Records an inline image's rect for hit-testing and draws its selection chrome if selected.
    // Called from DrawInlineObjects for each inline image (top-level and table-cell paragraphs).
    private void TrackInlineImage(CanvasDrawingSession ds, Paragraph p, InlineImage img, Rect rect)
    {
        if (_printMode) return; // print renders must not touch the screen hit-test geometry
        _inlineImageRects[img] = (p, rect);
        if (_selectedInline is { } s && ReferenceEquals(s.img, img)) DrawSelectionChrome(ds, rect);
    }

    private void DrawSelectionChrome(CanvasDrawingSession ds, Rect rect)
    {
        // Half a pen OUTSIDE the picture: a pen is centred on the rect it strokes, so a border drawn on the
        // picture's own rect painted over its outermost 1.25 px (upstream PR #52 — "the picture is cut by a
        // pixel or two", seen at a high zoom). The handles still sit on the picture's edges, as Word and HWP draw them.
        const float pen = 2.5f;
        ds.DrawRectangle(new Rect(rect.X - pen / 2, rect.Y - pen / 2, rect.Width + pen, rect.Height + pen), BlockSelBorder, pen);
        if (IsReadOnly) return; // a viewer selects pictures but cannot resize them
        // The handles GripAt finds: corner, middle of the right edge, middle of the bottom edge.
        foreach (var (cx, cy) in new[] { (rect.Right, rect.Bottom), (rect.Right, rect.Top + rect.Height / 2), (rect.Left + rect.Width / 2, rect.Bottom) })
        {
            var h = new Rect(cx - ResizeHandleSize / 2, cy - ResizeHandleSize / 2, ResizeHandleSize, ResizeHandleSize);
            ds.FillRectangle(h, BlockHandleFill);
            ds.DrawRectangle(h, Colors.White, 1.5f);
        }
    }
}
