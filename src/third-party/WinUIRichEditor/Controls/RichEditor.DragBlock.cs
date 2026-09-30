using System;
using Windows.Foundation;
using Windows.System;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Input;
using WinUIRichEditor.Documents;

namespace WinUIRichEditor.Controls;

// Drag & drop of a whole object within the editor: a table (grabbed by its left/top border band — where the
// SizeAll cursor already promised a move), a block image, an inline image or an inline table. The gesture is
// text drag's (RichEditor.DragText.cs): the press selects the object and arms; past the click slop a grey
// drop caret follows the pointer; the release moves the object there — or copies it when Ctrl is down at the
// drop. A press that never becomes a drag is only the selection it always was. One undo step either way.
public partial class RichEditor
{
    private object? _dragObject;       // ImageBlock / TableBlock / InlineImage / InlineTable armed by the press
    private bool _dragObjectActive;    // past the slop: the drop preview is live
    private Point _dragObjectStart;    // press point, doc space
    private Point _dragObjectLast;     // last pointer point, doc space — re-evaluated when Ctrl changes mid-drag

    // Either drag shows the drop caret (DrawDropPreview) and takes Ctrl as its copy modifier.
    private bool DropPreviewActive => _dragTextActive || _dragObjectActive;

    // A press that has just selected an object: arm its drag and take the pointer.
    private void ArmObjectDrag(object? obj, Point docPt, PointerStep s)
    {
        if (ArmObjectDragAt(obj, docPt)) s.Capture.Capture();
    }

    // The press minus its pointer capture. Editing only: a viewer's press selects the object for Copy.
    internal bool ArmObjectDragAt(object? obj, Point docPt)
    {
        if (IsReadOnly || Document == null || obj is not (ImageBlock or TableBlock or InlineImage or InlineTable)) return false;
        _dragObject = obj;
        _dragObjectActive = false;
        _dragObjectStart = _dragObjectLast = docPt;
        _dropPreview = null;
        return true;
    }

    // Pointer move while armed: past the slop the drag goes live and the drop caret follows the pointer. A
    // point the object may not go to (a move into its own cells) shows no caret and the no-drop cursor.
    internal void DragObjectMoved(Point docPt, bool ctrl)
    {
        if (_dragObject == null) return;
        _dragObjectLast = docPt;
        if (!_dragObjectActive)
        {
            if (Math.Abs(docPt.X - _dragObjectStart.X) + Math.Abs(docPt.Y - _dragObjectStart.Y) < MultiClickSlop) return;
            _dragObjectActive = true;
        }
        var tp = GetPositionFromPoint(docPt);
        _dropPreview = tp != null && CanDropObject(_dragObject, tp, copy: ctrl) && (!ctrl || MayCreate(_dragObject)) ? tp : null;
        SetCursorShape(_dropPreview != null ? InputSystemCursorShape.Arrow : InputSystemCursorShape.UniversalNo);
        InvalidateCanvas();
    }

    private void EndObjectDrag(PointerStep s)
    {
        FinishObjectDrag(copy: s.Ctrl);
        s.Capture.Release(); // after the drag is cleared, so CaptureLost finds nothing live
    }

    // The release minus its pointer capture. Ctrl is read at the DROP, as for text. Returns whether the
    // document changed; an unmoved press changes nothing (the object stays selected, as the press left it).
    internal bool FinishObjectDrag(bool copy)
    {
        var obj = _dragObject;
        bool wasDrag = _dragObjectActive;
        var drop = _dropPreview;
        CancelObjectDrag();
        if (!wasDrag || obj == null || drop == null) return false;
        return DropObject(obj, drop, copy);
    }

    // Ends an object drag WITHOUT dropping: a lost capture is not a drop, and after a document swap (load,
    // undo) the object belongs to the document that was replaced.
    private void CancelObjectDrag()
    {
        if (_dragObject == null) return;
        bool wasActive = _dragObjectActive;
        _dragObject = null;
        _dragObjectActive = false;
        _dropPreview = null;
        if (wasActive) { SetCursorShape(InputSystemCursorShape.IBeam); InvalidateCanvas(); }
    }

    // Ctrl pressed or let go mid-drag: the "+" copy mark follows it, and for an object drag so does whether
    // the point is a valid drop at all (a copy may go into its own cells, a move may not).
    private void OnDragModifierChanged()
    {
        // Driven by the KEY handlers (Ctrl pressed/released mid-drag), so the live keyboard state is the
        // right source here — unlike the pointer path, which carries the modifiers on its PointerStep.
        if (_dragObjectActive) DragObjectMoved(_dragObjectLast, Ctrl);
        else if (_dragTextActive) InvalidateCanvas();
    }

    private void OnEditorKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Control && DropPreviewActive) OnDragModifierChanged();
    }

    /// <summary>Whether <paramref name="obj"/> may be dropped at <paramref name="at"/>. A MOVE may not land
    /// inside itself — a table dropped into one of its own cells, at any depth, would have to become its own
    /// descendant. A COPY may: what lands there is a clone.</summary>
    internal static bool CanDropObject(object obj, TextPointer at, bool copy)
    {
        if (at.Paragraph is not { } p) return false;
        if (copy) return true;
        var table = obj switch { TableBlock tb => tb, InlineTable it => it.Table, _ => null };
        return table == null || !BlockContains(table, p);
    }

    // A copy CREATES a table or picture, which AllowTables / AllowImages forbid (as they do for the menu and for
    // paste); a move only relocates one the document already has (upstream round 34).
    private bool MayCreate(object obj) => obj switch
    {
        TableBlock or InlineTable => AllowTables,
        ImageBlock or InlineImage => AllowImages,
        _ => true,
    };

    // Moves (or copies) obj to `at`. Returns whether the document changed: a drop where the object already
    // is, or one CanDropObject refuses, is no edit at all — no undo step, not "modified".
    internal bool DropObject(object obj, TextPointer at, bool copy)
    {
        if (Document == null || at.Paragraph is not { } p || !CanDropObject(obj, at, copy)) return false;
        if (copy && !MayCreate(obj)) return false;
        return obj switch
        {
            ImageBlock or TableBlock => DropBlock((Block)obj, p, at.Offset, copy),
            InlineImage or InlineTable => DropInline((Inline)obj, p, at.Offset, copy),
            _ => false,
        };
    }

    // A block goes BEFORE the drop paragraph when dropped at its start, AFTER it when dropped at its end, and
    // splits it only in between. Always splitting (InsertBlockAtCaret's rule, right for an insert at the
    // caret) would leave an empty head paragraph at every start-of-line drop — dragging a table down and
    // back would grow the document by a blank line per trip.
    private bool DropBlock(Block blk, Paragraph p, int offset, bool copy)
    {
        var source = BlockContainerOf(blk);
        var target = BlockContainerOf(p);
        if (source == null || target == null || !source.Contains(blk) || !target.Contains(p)) return false;
        int len = GetParagraphLength(p);
        int off = Math.Clamp(offset, 0, len);
        if (!copy && ReferenceEquals(source, target))
        {
            int bi = source.IndexOf(blk), pi = target.IndexOf(p);
            if ((off == 0 && pi == bi + 1) || (off == len && pi == bi - 1)) return false; // already there
        }

        PushUndo(null);
        var moving = copy ? (Block)blk.Clone() : blk;
        if (!copy) source.Remove(blk);
        if (off > 0 && off < len)
        {
            _caret = new TextPointer(p, off);
            InsertBlockAtCaret(moving); // head | moving | tail
        }
        else
        {
            moving.Parent = p.Parent;
            int at = target.IndexOf(p);
            target.Insert(off == 0 ? at : at + 1, moving);
        }
        UpdateParents(Document!); // re-normalizes the container the block left, too

        // The dropped object stays selected, as it was when grabbed. The caret goes just after it:
        // NormalizeBlockList guarantees a paragraph follows every non-paragraph block.
        ClearObjectSelection();
        _selectedBlock = moving;
        if (BlockContainerOf(moving) is { } host && host.IndexOf(moving) is int mi and >= 0
            && mi + 1 < host.Count && host[mi + 1] is Paragraph next)
            _caret = new TextPointer(next, 0);
        CollapseSelectionToCaret();
        AfterEdit();
        return true;
    }

    // An inline object is one character (rule #2). Moved later within its own paragraph, the drop offset
    // shifts back by one once the object is taken out ahead of it.
    private bool DropInline(Inline obj, Paragraph p, int offset, bool copy)
    {
        if (obj.Parent is not Paragraph host || host.Inlines.IndexOf(obj) is not (int idx and >= 0)) return false;
        int from = 0;
        for (int i = 0; i < idx; i++) from += InlineLen(host.Inlines[i]);
        int off = Math.Clamp(offset, 0, GetParagraphLength(p));
        if (!copy && ReferenceEquals(host, p) && (off == from || off == from + 1)) return false; // already there

        PushUndo(null);
        var moving = copy ? (Inline)obj.Clone() : obj;
        if (!copy)
        {
            host.Inlines.RemoveAt(idx);
            if (host.Inlines.Count == 0) host.Inlines.Add(new Run { Text = "", Parent = host });
            if (ReferenceEquals(host, p) && off > from) off--;
        }
        int at = SplitInlinesAt(p, off);
        moving.Parent = p;
        p.Inlines.Insert(at, moving);
        TextRange.CoalesceRuns(p);
        if (!ReferenceEquals(host, p)) TextRange.CoalesceRuns(host);
        UpdateParents(Document!);

        ClearObjectSelection();
        if (moving is InlineImage img) _selectedInline = (p, img);
        else if (moving is InlineTable it) _selectedInlineTable = (p, it);
        int caretOff = 0;
        foreach (var inl in p.Inlines)
        {
            caretOff += InlineLen(inl);
            if (ReferenceEquals(inl, moving)) break;
        }
        _caret = new TextPointer(p, caretOff);
        CollapseSelectionToCaret();
        AfterEdit();
        return true;
    }
}
