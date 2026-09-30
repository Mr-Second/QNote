using System;
using Windows.Foundation;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Input;
using WinUIRichEditor.Documents;

namespace WinUIRichEditor.Controls;

// Drag & drop of the selected text within the editor (Word convention): pressing inside the selection
// arms a drag instead of starting a new selection; moving past the click slop shows a grey drop-preview
// caret; releasing moves the content there (Ctrl held = copy). A press that never becomes a drag falls
// back to the ordinary click (collapse the caret at the press point) on release.
public partial class RichEditor
{
    private bool _dragTextArmed;        // pressed inside the selection; may become a drag or stay a click
    private bool _dragTextActive;       // past the slop: drop preview showing, release performs the drop
    private Point _dragTextStart;       // press point, doc space
    private TextPointer? _dropPreview;  // current drop position while dragging (drawn by DrawDropPreview)
    private bool _dragTextPressCtrl;    // Ctrl was down at the press — an undragged release is then a Ctrl+click

    // What a press on text does once no object or table chrome has taken it.
    internal enum TextPress { ArmDrag, CtrlClick, Click }

    /// <summary>The order of the text-press branches. A single press strictly inside the selection arms a drag
    /// BEFORE the Ctrl+click branch sees it: Ctrl is the drag's copy modifier (Word convention), and the Ctrl+click
    /// branch collapses the selection — tested first, it turned "hold Ctrl, drag the selection" into a new
    /// selection, so a copy-drag could only be had by pressing Ctrl mid-drag. A Ctrl+click on a link inside the
    /// selection still opens it, from the release (EndTextDrag). Multi-clicks never arm (a fast triple-click
    /// still selects the paragraph), and Shift extends the selection instead.</summary>
    internal static TextPress ChooseTextPress(bool ctrl, bool shift, bool repeat, bool insideDraggableSelection)
    {
        if (!shift && !repeat && insideDraggableSelection) return TextPress.ArmDrag;
        if (ctrl && !shift) return TextPress.CtrlClick;
        return TextPress.Click;
    }

    // Whether tp sits STRICTLY inside the current selection (boundary clicks place the caret instead,
    // matching Word). Cell-block selections are excluded — their "content" is a grid rectangle, not a
    // linear range this move understands.
    private bool CanArmTextDragAt(TextPointer tp)
    {
        if (IsReadOnly || !HasSelection || CellBlockSelection() != null) return false;
        TextPointer s = _selStart, e2 = _selEnd;
        if (ComparePositions(s, e2) > 0) (s, e2) = (e2, s);
        return ComparePositions(s, tp) < 0 && ComparePositions(tp, e2) < 0;
    }

    // Arms the drag; the caller has checked CanArmTextDragAt (via ChooseTextPress).
    private void ArmTextDrag(Point docPt, PointerStep s)
    {
        _dragTextArmed = true;
        _dragTextActive = false;
        _dragTextStart = docPt;
        _dragTextPressCtrl = s.Ctrl;
        _dropPreview = null;
        s.Capture.Capture();
    }

    // Ends an armed text drag WITHOUT dropping — a lost capture is not a release (the same rule as
    // CancelObjectDrag). Left armed, the drag outlived the button: the next plain hover kept moving the drop
    // caret, and the next CLICK ran EndTextDrag with the stale preview and MOVED the selection somewhere the
    // user never dropped it (measured 2026-09-20 through the pointer pipeline). Ending is not dropping.
    private void CancelTextDrag()
    {
        if (!_dragTextArmed) return;
        bool wasActive = _dragTextActive;
        _dragTextArmed = false;
        _dragTextActive = false;
        _dropPreview = null;
        if (wasActive) { SetCursorShape(InputSystemCursorShape.IBeam); InvalidateCanvas(); }
    }

    // Pointer move while armed: past the slop the drag activates and the drop preview follows the pointer.
    private void DragTextMoved(Point docPt)
    {
        if (!_dragTextActive)
        {
            if (Math.Abs(docPt.X - _dragTextStart.X) + Math.Abs(docPt.Y - _dragTextStart.Y) < MultiClickSlop) return;
            _dragTextActive = true;
            SetCursorShape(InputSystemCursorShape.Arrow);
        }
        _dropPreview = GetPositionFromPoint(docPt);
        InvalidateCanvas();
    }

    // Pointer release: a real drag performs the move/copy; an unmoved press is the deferred plain click.
    private void EndTextDrag(PointerStep s)
    {
        bool wasDrag = _dragTextActive;
        var drop = _dropPreview;
        _dragTextArmed = false;
        _dragTextActive = false;
        _dropPreview = null;
        s.Capture.Release();
        SetCursorShape(InputSystemCursorShape.IBeam);

        if (!wasDrag)
        {
            // The press deferred the usual click handling (so the selection survived a potential drag);
            // do it now: caret to the click point, selection collapsed.
            var pt = ViewToDoc(s.ViewPos);
            if (GetPositionFromPoint(pt) is { Paragraph: not null } tp)
            {
                _caret = tp;
                _desiredCaretX = pt.X;
                _coalesceKey = null;
                _pendingCaretStyles = null;
                CollapseSelectionToCaret();
                RestartBlink();
                SyncIme();
                RaiseStatusChanged();
            }
            InvalidateCanvas();
            // A Ctrl+click on a link inside the selection armed this drag instead of reaching the press's
            // Ctrl+click branch (ChooseTextPress), so the link opens here. Ctrl as it was at the PRESS — a click
            // is decided by its press, and the release may come after Ctrl has already been let go.
            if (_dragTextPressCtrl && LinkRunAtPoint(pt)?.NavigateUri is { Length: > 0 } link)
                _ = OpenUriAsync(link);
            return;
        }

        if (drop?.Paragraph == null) { InvalidateCanvas(); return; }
        PerformTextDrop(drop, copy: s.Ctrl);
    }

    // Moves (or, with copy, duplicates) the selected content to `drop`. The move deletes the selection
    // first; a zero-length MARKER run planted at the drop point survives that deletion (empty runs are
    // neither removed by DeleteInParagraph nor merged by CoalesceRuns while their format is unique, and
    // paragraph merges move inlines rather than discard them), so the drop position is recovered after
    // the offsets shift. One undo step restores both halves.
    private void PerformTextDrop(TextPointer drop, bool copy)
    {
        if (Document == null || !HasSelection) { InvalidateCanvas(); return; }
        TextPointer s = _selStart, e = _selEnd;
        if (ComparePositions(s, e) > 0) (s, e) = (e, s);

        // Dropping into the dragged range is a no-op (Word behavior).
        if (ComparePositions(s, drop) <= 0 && ComparePositions(drop, e) <= 0) { InvalidateCanvas(); return; }

        var content = BuildSelectionDocument(new TextRange(s, e)); // cloned; independent of the edits below
        PushUndo(null);

        if (copy)
        {
            _caret = new TextPointer(drop.Paragraph, drop.Offset);
            CollapseSelectionToCaret();
            InsertDocumentAtCaret(content);
            AfterEdit();
            return;
        }

        var markerPara = drop.Paragraph!;
        var marker = new Run { Text = "", NavigateUri = "winuirich:drop-marker" }; // unique format → never coalesced
        int mIdx = SplitInlinesAt(markerPara, Math.Clamp(drop.Offset, 0, GetParagraphLength(markerPara)));
        marker.Parent = markerPara;
        markerPara.Inlines.Insert(mIdx, marker);

        new TextRange(_selStart, _selEnd).Delete();
        UpdateParents(Document);

        // Relocate the marker (its paragraph may have been merged into the selection-start paragraph).
        Paragraph? host = null;
        int hostOff = 0;
        foreach (var p in AllParagraphs())
        {
            int off = 0;
            foreach (var inl in p.Inlines)
            {
                if (ReferenceEquals(inl, marker)) { host = p; hostOff = off; break; }
                off += InlineLen(inl);
            }
            if (host != null) break;
        }
        if (host == null) { AfterEdit(); return; } // defensive: content deleted; undo restores everything
        host.Inlines.Remove(marker);
        if (host.Inlines.Count == 0) host.Inlines.Add(new Run { Text = "", Parent = host });

        _caret = new TextPointer(host, hostOff);
        CollapseSelectionToCaret();
        InsertDocumentAtCaret(content);
        AfterEdit();
    }
}
