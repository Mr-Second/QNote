using System;
using Windows.Foundation;
using Windows.UI.Text.Core;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using WinUIRichEditor.Documents;

namespace WinUIRichEditor.Controls;

// Phase 4: CJK/Korean IME via CoreTextEditContext. The edit buffer the IME sees is the CARET'S
// PARAGRAPH plain text; composition text is applied to that paragraph by TextUpdating and the composing
// range is drawn underlined (FormatUpdating). CoreTextServicesManager.GetForCurrentView() works in
// WinUI 3 desktop on current Windows (verified at runtime), so no hidden-TextBox workaround is needed.
public partial class RichEditor
{
    private CoreTextEditContext? _editContext;
    private bool _imeEnabled;
    private bool _inTextUpdating;     // re-entrancy guard: don't notify the IME while applying its own update
    private bool _composing;
    private (int start, int length)? _composRange; // composition underline range, in caret-paragraph coords
    private int _imeBufferLen;
    private string? _imeBufferText; // last text handed to the IME — catches same-length replacements
    private Paragraph? _imeBufferPara;
    // Offset shift applied to incoming IME ranges after a cross-paragraph selection was deleted at
    // composition start (the IME saw a collapsed caret at the OLD offset; the deletion moved the caret).
    private int _imeRangeDelta;

    private void SetupIme()
    {
        try
        {
            var mgr = CoreTextServicesManager.GetForCurrentView();
            _editContext = mgr.CreateEditContext();
            _editContext.InputPaneDisplayPolicy = CoreTextInputPaneDisplayPolicy.Automatic;
            _editContext.InputScope = CoreTextInputScope.Text;

            _editContext.TextRequested += OnImeTextRequested;
            _editContext.SelectionRequested += OnImeSelectionRequested;
            _editContext.TextUpdating += OnImeTextUpdating;
            _editContext.SelectionUpdating += OnImeSelectionUpdating;
            _editContext.FormatUpdating += OnImeFormatUpdating;
            _editContext.LayoutRequested += OnImeLayoutRequested;
            _editContext.CompositionStarted += (_, _) => { _composing = true; };
            _editContext.CompositionCompleted += (_, _) => { _composing = false; _composRange = null; _imeRangeDelta = 0; _coalesceKey = null; _pendingCaretStyles = null; InvalidateCanvas(); };
            _editContext.FocusRemoved += (_, _) => { _composing = false; _composRange = null; _imeRangeDelta = 0; };
            _imeEnabled = true;
        }
        catch (Exception ex)
        {
            RichEditorDiagnostics.Report(ex);
            _imeEnabled = false; // fall back to CharacterReceived-only input
        }
    }

    private void ImeNotifyFocusEnter()
    {
        if (!_imeEnabled || _editContext == null) return;
        _editContext.NotifyFocusEnter();
        _imeBufferPara = _caret.Paragraph;
        _imeBufferLen = GetParagraphLength(_caret.Paragraph);
        _imeBufferText = CaretParagraphText();
        _imeRangeDelta = 0;
    }

    private void ImeNotifyFocusLeave()
    {
        if (!_imeEnabled || _editContext == null) return;
        _composing = false; _composRange = null;
        _editContext.NotifyFocusLeave();
    }

    // Keeps the IME's view of text/selection in sync after we edit or move the caret. The buffer is the
    // caret paragraph; a paragraph switch or length change is a full-buffer replacement.
    private void SyncIme()
    {
        if (!_imeEnabled || _editContext == null || _inTextUpdating) return;
        int newLen = GetParagraphLength(_caret.Paragraph);
        // Compare content too, not just paragraph identity + length — a same-length replacement
        // (find/replace, formatting-driven text swap) would otherwise leave the IME's buffer stale.
        string newText = CaretParagraphText();
        bool bufferChanged = !ReferenceEquals(_imeBufferPara, _caret.Paragraph)
            || newLen != _imeBufferLen || newText != _imeBufferText;
        if (bufferChanged)
        {
            var modified = new CoreTextRange { StartCaretPosition = 0, EndCaretPosition = _imeBufferLen };
            _editContext.NotifyTextChanged(modified, newLen, CaretSelectionRange());
            _imeBufferLen = newLen;
            _imeBufferPara = _caret.Paragraph;
            _imeRangeDelta = 0; // the IME now sees the real buffer; no remapping needed
        }
        else
        {
            _editContext.NotifySelectionChanged(CaretSelectionRange());
        }
        _imeBufferText = newText;
    }

    private CoreTextRange CaretSelectionRange()
    {
        int a = _caret.Offset, b = _caret.Offset;
        if (HasSelection && ReferenceEquals(_selStart.Paragraph, _caret.Paragraph) && ReferenceEquals(_selEnd.Paragraph, _caret.Paragraph))
        {
            a = Math.Min(_selStart.Offset, _selEnd.Offset);
            b = Math.Max(_selStart.Offset, _selEnd.Offset);
        }
        return new CoreTextRange { StartCaretPosition = a, EndCaretPosition = b };
    }

    private string CaretParagraphText() => _caret.Paragraph != null ? BuildPlain(_caret.Paragraph) : "";

    // ---- edit-context events ----------------------------------------------
    private void OnImeTextRequested(CoreTextEditContext sender, CoreTextTextRequestedEventArgs args)
    {
        string text = CaretParagraphText();
        var r = args.Request.Range;
        int s = Math.Clamp(r.StartCaretPosition, 0, text.Length);
        int e = Math.Clamp(r.EndCaretPosition, s, text.Length);
        args.Request.Text = text.Substring(s, e - s);
    }

    private void OnImeSelectionRequested(CoreTextEditContext sender, CoreTextSelectionRequestedEventArgs args)
        => args.Request.Selection = CaretSelectionRange();

    // The offset arithmetic of a composition update, split out of OnImeTextUpdating because that handler
    // needs a CoreTextTextUpdatingEventArgs — a WinRT type with no public constructor — while this is
    // where the mistakes that reach users live: composed text landing at the wrong offset, or a range
    // that runs past the paragraph it belongs to.
    //
    // `delta` is the shift applied after a cross-paragraph selection was deleted at composition start:
    // the IME was told about a collapsed caret at the OLD offset (CaretSelectionRange cannot express a
    // multi-paragraph selection), so every range it reports afterwards is off by how far the deletion
    // moved the caret.
    /// The span of the caret paragraph a composition update replaces. Clamped into the paragraph, and
    /// the end is never before the start — an inverted or out-of-range span would delete the wrong text.
    internal static (int start, int end) MapImeRange(int reportedStart, int reportedEnd, int delta, int paragraphLength)
    {
        int s = Math.Clamp(reportedStart + delta, 0, paragraphLength);
        int e = Math.Clamp(reportedEnd + delta, s, paragraphLength);
        return (s, e);
    }

    /// Where the caret lands after a composition update, in the same coordinates.
    internal static int MapImeCaret(int reportedCaret, int delta, int paragraphLength)
        => Math.Clamp(reportedCaret + delta, 0, paragraphLength);

    private void OnImeTextUpdating(CoreTextEditContext sender, CoreTextTextUpdatingEventArgs args)
    {
        if (Document == null || IsReadOnly || _caret.Paragraph == null) { args.Result = CoreTextTextUpdatingResult.Failed; return; }
        _inTextUpdating = true;
        try
        {
            var p = _caret.Paragraph;

            // A selection spanning paragraphs was reported to the IME as a collapsed caret
            // (CaretSelectionRange), so the IME's range can't cover it. Delete it here — mirroring the
            // CharacterReceived path, where InsertText replaces the selection — and remap the rest of
            // the composition's ranges by how far the deletion moved the caret.
            if (HasSelection && !(ReferenceEquals(_selStart.Paragraph, p) && ReferenceEquals(_selEnd.Paragraph, p)))
            {
                PushUndo("ime"); // one undo group with the composition below (same coalesce key)
                int reported = _caret.Offset;
                DeleteSelection();
                p = _caret.Paragraph;
                if (p == null) { args.Result = CoreTextTextUpdatingResult.Failed; return; }
                _imeRangeDelta = _caret.Offset - reported;
            }

            int len = GetParagraphLength(p);
            var r = args.Range;
            var (s, e) = MapImeRange(r.StartCaretPosition, r.EndCaretPosition, _imeRangeDelta, len);
            string newText = args.Text ?? "";

            // Snapshot before the edit so a composition is undoable. The "ime" coalesce key collapses the
            // many TextUpdating calls of one composition session into a single undo group; CompositionCompleted
            // clears the key so the next composition (or edit) starts a fresh group.
            PushUndo("ime");

            if (e > s) DeleteLocalText(p, s, e - s);
            if (newText.Length > 0)
            {
                TryInsertTextCore(p, newText, s);
                // A format toggled at the empty caret (pending style) must reach IME text too — the
                // CharacterReceived path applies it in InsertText, but composition text arrives here.
                // While composing, keep the styles armed (each update replaces the composed range and
                // re-applies them); CompositionCompleted clears them.
                ApplyPendingStyles(p, s, newText.Length, clear: !_composing);
            }

            int caret = MapImeCaret(args.NewSelection.EndCaretPosition, _imeRangeDelta, GetParagraphLength(p));
            _caret = new TextPointer(p, caret);
            CollapseSelectionToCaret();
            _imeBufferLen = GetParagraphLength(p);
            _imeBufferPara = p;
            _imeBufferText = BuildPlain(p);

            // Composition is an edit like any other, so finish with the SAME post-edit work every other
            // path runs. Previously this did only RelayoutToViewport + RestartBlink, so composing Hangul
            // (a) never dropped the ancestor tables' cached row heights — RelayoutToViewport does not
            // touch _tableRowHeights — leaving a cell's row too short for the text just typed into it,
            // (b) never scrolled the caret into view, and (c) never flushed TextChanged/SelectionChanged
            // (they waited for the next unrelated input). English input, which goes through
            // CharacterReceived -> InsertText -> AfterEdit, did all three.
            // SyncIme inside AfterEdit no-ops here: _inTextUpdating is set for this whole handler.
            AfterEdit();
            args.Result = CoreTextTextUpdatingResult.Succeeded;
        }
        catch (Exception ex)
        {
            RichEditorDiagnostics.Report(ex);
            args.Result = CoreTextTextUpdatingResult.Failed;
        }
        finally { _inTextUpdating = false; }
    }

    private void OnImeSelectionUpdating(CoreTextEditContext sender, CoreTextSelectionUpdatingEventArgs args)
    {
        var p = _caret.Paragraph;
        if (p == null) { args.Result = CoreTextSelectionUpdatingResult.Failed; return; }
        int len = GetParagraphLength(p);
        // Same remapping as OnImeTextUpdating: while _imeRangeDelta is armed the IME is still speaking
        // the PRE-deletion offsets it was told about, so its ranges need the shift too.
        int a = Math.Clamp(args.Selection.StartCaretPosition + _imeRangeDelta, 0, len);
        int b = Math.Clamp(args.Selection.EndCaretPosition + _imeRangeDelta, 0, len);
        _selStart = new TextPointer(p, a);
        _selEnd = new TextPointer(p, b);
        _caret = new TextPointer(p, b);
        InvalidateCanvas();
        args.Result = CoreTextSelectionUpdatingResult.Succeeded;
    }

    private void OnImeFormatUpdating(CoreTextEditContext sender, CoreTextFormatUpdatingEventArgs args)
    {
        var r = args.Range;
        // The underline range arrives in the IME's coordinate space, which OnImeTextUpdating shifts by
        // _imeRangeDelta after deleting a cross-paragraph selection at composition start. Without the
        // same shift the composition underline was drawn `delta` characters away from the text it marks.
        _composRange = (r.StartCaretPosition + _imeRangeDelta,
                        Math.Max(0, r.EndCaretPosition - r.StartCaretPosition));
        InvalidateCanvas();
    }

    private void OnImeLayoutRequested(CoreTextEditContext sender, CoreTextLayoutRequestedEventArgs args)
    {
        var rect = CaretScreenRect();
        args.Request.LayoutBounds.TextBounds = rect;
        args.Request.LayoutBounds.ControlBounds = rect;
    }

    // The caret rectangle in physical screen pixels, as CoreTextEditContext.LayoutRequested expects, so
    // the IME candidate window sits right under the caret. Uses the shared doc→screen mapping below
    // (also consumed by the automation peer's bounding rectangles).
    private Rect CaretScreenRect()
    {
        try
        {
            if (CaretToDocPoint(_caret) is not { } dp) return default;
            double scale = _canvas.XamlRoot?.RasterizationScale ?? 1.0;
            var top = DocPointToScreen(new Point(dp.X, dp.Y));
            return new Rect(top.X, top.Y, 2 * scale, dp.Height * EffectiveZoom * scale);
        }
        catch (Exception ex) { RichEditorDiagnostics.Report(ex); return default; }
    }

    // ---- shared doc-space <-> physical-screen mapping -----------------------
    // DocToView folds in zoom + the paged page stack; the scroll offset places it in the viewport;
    // TransformToVisual(null) + rasterization scale reach physical window coordinates; ClientToScreen of
    // the active top-level window adds the on-screen client origin (valid while this window is
    // focused/active — for background automation queries the origin degrades to client-relative).
    private Point DocPointToScreen(Point d)
    {
        var cv = DocToView(d);
        double ex = cv.X - _scroll.HorizontalOffset;
        double ey = cv.Y - _scroll.VerticalOffset;
        var tl = _canvas.TransformToVisual(null).TransformPoint(new Point(ex, ey));
        double scale = _canvas.XamlRoot?.RasterizationScale ?? 1.0;
        var (ox, oy) = ClientOriginOnScreen();
        return new Point(ox + tl.X * scale, oy + tl.Y * scale);
    }

    // Inverse of DocPointToScreen up to the view (pre-ViewToDoc) coordinate space.
    private Point? ScreenPointToView(Point screen)
    {
        double scale = _canvas.XamlRoot?.RasterizationScale ?? 1.0;
        var (ox, oy) = ClientOriginOnScreen();
        var winPt = new Point((screen.X - ox) / scale, (screen.Y - oy) / scale);
        var inv = _canvas.TransformToVisual(null).Inverse;
        if (inv == null) return null;
        var local = inv.TransformPoint(winPt);
        return new Point(local.X + _scroll.HorizontalOffset, local.Y + _scroll.VerticalOffset);
    }

    internal Rect DocRectToScreen(Rect r)
    {
        var tl = DocPointToScreen(new Point(r.X, r.Y));
        var br = DocPointToScreen(new Point(r.Right, r.Bottom));
        return new Rect(Math.Min(tl.X, br.X), Math.Min(tl.Y, br.Y), Math.Abs(br.X - tl.X), Math.Abs(br.Y - tl.Y));
    }

    private (double ox, double oy) ClientOriginOnScreen()
    {
        var hwnd = GetActiveWindow();
        if (hwnd != IntPtr.Zero)
        {
            var origin = new POINT { X = 0, Y = 0 };
            if (ClientToScreen(hwnd, ref origin)) return (origin.X, origin.Y);
        }
        return (0, 0);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetActiveWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    // ---- composition underline (called from the render walk) --------------
    private void DrawCompositionUnderline(CanvasDrawingSession ds, Paragraph p, CanvasTextLayout layout, double px, double oy)
    {
        if (_printMode) return;
        if (_composRange is not { } cr || cr.length <= 0) return;
        if (!IsCaretInParagraph(p)) return;
        int len = GetParagraphLength(p);
        int s = Math.Clamp(cr.start, 0, len);
        int e = Math.Clamp(cr.start + cr.length, s, len);
        if (e <= s) return;
        try
        {
            var regions = layout.GetCharacterRegions(s, e - s);
            foreach (var r in regions)
            {
                var lb = r.LayoutBounds;
                float uy = (float)(oy + lb.Bottom - 1);
                ds.DrawLine((float)(px + lb.X), uy, (float)(px + lb.X + lb.Width), uy, CaretColor, 1.5f);
            }
        }
        catch (Exception ex) { RichEditorDiagnostics.Report(ex); }
    }
}
