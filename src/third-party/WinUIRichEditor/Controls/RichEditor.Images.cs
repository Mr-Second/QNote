using System;
using System.Threading.Tasks;
using WinUIRichEditor.Documents;

namespace WinUIRichEditor.Controls;

// Phase 5: image insertion (block-level) + image context-menu commands (size presets, replace, save).
public partial class RichEditor
{
    /// <summary>Optional async provider of replacement image bytes (a file picker). When set, the image
    /// context menu offers "Replace Image…"; the host supplies bytes because picking needs the window handle.</summary>
    public Func<Task<byte[]?>>? ImageReplacePicker { get; set; }

    /// <summary>Optional handler that saves image bytes (+ MIME) to a host-chosen file. When set, the image
    /// context menu offers "Save As…".</summary>
    public Func<byte[], string?, Task>? ImageSaveHandler { get; set; }

    // Natural pixel size from the encoded header, or null if unavailable.
    internal static (double w, double h)? NaturalImageSize(byte[]? raw)
    {
        if (raw is not { Length: > 0 }) return null;
        var (nw, nh) = ImageInfo.GetPixelSize(raw);
        return nw > 0 && nh > 0 ? (nw, nh) : null;
    }

    // The drawn size of a block image: the declared Width/Height when set; otherwise the natural size
    // from the encoded header (sync, device-free), then the decoded bitmap, then a 200×200 fallback.
    // One dimension set keeps the natural aspect for the other. This is THE single source shared by
    // render, measurement (BlockHeight), hit-testing (BlockImageRects) and cell layout — they diverged
    // before (render used the decoded bitmap, measurement a fixed 200px), skewing every y below.
    internal static (double w, double h) BlockImageDims(ImageBlock img)
    {
        double w = img.Width > 0 ? img.Width : 0;   // NaN > 0 is false
        double h = img.Height > 0 ? img.Height : 0;
        if (w > 0 && h > 0) return (w, h);
        (double w, double h) nat;
        if (NaturalImageSize(img.RawBytes) is { } n) nat = n;
        else if (img.Image is { } bmp) nat = (bmp.Size.Width, bmp.Size.Height);
        else nat = (200, 200);
        if (w > 0) return (w, nat.h * (w / nat.w));
        if (h > 0) return (nat.w * (h / nat.h), h);
        return nat;
    }

    // "Original size": resets a block image to its natural pixel size (capped to the content width).
    private void ResetBlockImageNatural(ImageBlock img)
    {
        if (NaturalImageSize(img.RawBytes) is not { } nat) return;
        double w = nat.w, h = nat.h;
        double maxW = Math.Max(50, _layoutWidth - 40);
        if (w > maxW) { h *= maxW / w; w = maxW; }
        if (img.Width == w && img.Height == h) return; // already: no undo step that undoes nothing
        PushUndo(null);
        img.Width = w; img.Height = h;
        AfterEdit();
    }

    // "½/⅓/¼": scales a block image by a factor of its CURRENT displayed size (matches the original).
    private void ScaleBlockImage(ImageBlock img, double factor)
    {
        var nat = NaturalImageSize(img.RawBytes);
        double baseW = img.Width > 0 ? img.Width : nat?.w ?? 0;
        double baseH = img.Height > 0 ? img.Height : nat?.h ?? 0;
        if (baseW <= 0 || baseH <= 0) return;
        PushUndo(null);
        img.Width = Math.Max(8, baseW * factor);
        img.Height = Math.Max(8, baseH * factor);
        AfterEdit();
    }

    // "Original size": resets an inline image to its natural pixel size (capped to the inline max).
    private void ResetInlineImageNatural(InlineImage img)
    {
        if (NaturalImageSize(img.RawBytes) is not { } nat) return;
        double w = nat.w, h = nat.h;
        double maxW = Math.Max(40, Math.Min(_layoutWidth - 40, 240));
        if (w > maxW) { h *= maxW / w; w = maxW; }
        if (img.Width == w && img.Height == h) return; // see ResetBlockImageNatural
        PushUndo(null);
        img.Width = w; img.Height = h;
        _tableRowHeights.Clear();
        AfterEdit();
    }

    // "½/⅓/¼": scales an inline image by a factor of its CURRENT displayed size.
    private void ScaleInlineImage(InlineImage img, double factor)
    {
        var nat = NaturalImageSize(img.RawBytes);
        double baseW = img.Width > 0 ? img.Width : nat?.w ?? 0;
        double baseH = img.Height > 0 ? img.Height : nat?.h ?? 0;
        if (baseW <= 0 || baseH <= 0) return;
        PushUndo(null);
        img.Width = Math.Max(8, baseW * factor);
        img.Height = Math.Max(8, baseH * factor);
        _tableRowHeights.Clear();
        AfterEdit();
    }

    private async Task ReplaceImageBytesAsync(object imageElement)
    {
        if (ImageReplacePicker == null) return;
        byte[]? bytes;
        try { bytes = await ImageReplacePicker(); }
        catch (Exception ex) { RichEditorDiagnostics.Report(ex); return; }
        if (bytes is not { Length: > 0 }) return;
        PushUndo(null);
        var mime = ImageMime.Detect(bytes);
        // The cache is keyed by the RawBytes array; drop the OLD bytes' entry (an undo snapshot may
        // still share it — it re-decodes on demand if the user undoes back to it).
        byte[]? oldBytes;
        if (imageElement is ImageBlock ib) { oldBytes = ib.RawBytes; ib.SetImageData(bytes, mime); }
        else if (imageElement is InlineImage ii) { oldBytes = ii.RawBytes; ii.SetImageData(bytes, mime); }
        else return;
        if (oldBytes != null) _images.Invalidate(oldBytes);
        ClearLayoutCacheFor(imageElement);
        AfterEdit();
    }

    // Pictures print at up to this resolution (never above the source's own). 300 DPI is print quality; the
    // print paths hand over DIP-based sessions, whose own scale would otherwise ask for screen resolution.
    internal const double PrintImageDpi = 300;

    // The bitmap to draw an image element with, decoded for the device pixels `rect` covers on `ds`: its
    // transform (zoom, a print fit) times its DPI. Printing decodes synchronously at print resolution.
    private Microsoft.Graphics.Canvas.CanvasBitmap? ImageToDraw(Microsoft.Graphics.Canvas.CanvasDrawingSession ds,
        object element, byte[]? rawBytes, Microsoft.Graphics.Canvas.CanvasBitmap? already, Windows.Foundation.Rect rect)
    {
        var m = ds.Transform;
        double scale = Math.Max(Math.Sqrt(m.M11 * m.M11 + m.M12 * m.M12), Math.Sqrt(m.M21 * m.M21 + m.M22 * m.M22))
            * ds.Dpi / 96.0;
        if (_printMode) scale = Math.Max(scale, PrintImageDpi / 96.0);
        int w = (int)Math.Ceiling(Math.Max(1, rect.Width) * scale), h = (int)Math.Ceiling(Math.Max(1, rect.Height) * scale);
        return _printMode
            ? _images.GetForPrint(element, rawBytes, already, w, h)
            : _images.Get(_canvas, element, rawBytes, already, w, h, ControlRect(m, rect));
    }

    // `rect` (drawing-session coordinates) in the control's own DIPs — the space a CanvasVirtualControl
    // session starts in before the zoom and page transforms are applied — for ImageCache.TrimOffscreen.
    private static Windows.Foundation.Rect ControlRect(System.Numerics.Matrix3x2 m, Windows.Foundation.Rect rect)
    {
        var a = System.Numerics.Vector2.Transform(new((float)rect.Left, (float)rect.Top), m);
        var b = System.Numerics.Vector2.Transform(new((float)rect.Right, (float)rect.Bottom), m);
        return new Windows.Foundation.Rect(new Windows.Foundation.Point(a.X, a.Y), new Windows.Foundation.Point(b.X, b.Y));
    }

    // Decoded pictures still in the document but drawn nowhere near the viewport are kept up to this many pixel
    // bytes (ImageCache.TrimOffscreen); beyond it the least recently drawn are released and re-decode when they
    // come back into view. internal, not const: tests shrink it.
    internal long ImageOffscreenBytes = 32L * 1024 * 1024;

    // After every screen draw pass: release pictures far from the viewport beyond ImageOffscreenBytes (see
    // ImageCache.TrimOffscreen). The protected band is the viewport plus one viewport above and below, so a
    // short scroll back never meets a placeholder, and the scroll offset lagging the draw can't matter.
    private void TrimOffscreenImages(long keepBytes)
    {
        if (_images.IsEmpty) return;
        _images.TrimOffscreen(NearViewport(), keepBytes);
    }

    // The viewport in control DIPs (the canvas sits DocContentLeft below the top of the scroll content),
    // grown by one viewport on each side.
    private Windows.Foundation.Rect NearViewport()
    {
        double vw = Math.Max(1, _scroll.ViewportWidth), vh = Math.Max(1, _scroll.ViewportHeight);
        return new Windows.Foundation.Rect(_scroll.HorizontalOffset - _canvas.Margin.Left - vw,
            _scroll.VerticalOffset - _canvas.Margin.Top - vh, vw * 3, vh * 3);
    }

    // Every picture draw. The bitmap is decoded to COVER its rect with the source's aspect (ImageDecoder.CoverBox),
    // so a picture squashed out of its proportions still shrinks its short axis several times on the GPU. The
    // default bilinear sampling skips texels past 2:1 — lines at random spacing, "SHARP" drawn as "SHAKI'" in
    // the user's test file (2026-09-19). Anisotropic filters each axis by its own ratio; measured 0.6-1.1 ms a
    // draw against 0.3-0.7 for bilinear and up to 4.5 for HighQualityCubic, which looked the same.
    private static void DrawPicture(Microsoft.Graphics.Canvas.CanvasDrawingSession ds,
        Microsoft.Graphics.Canvas.CanvasBitmap bmp, Windows.Foundation.Rect rect)
        => ds.DrawImage(bmp, rect, bmp.Bounds, 1f, Microsoft.Graphics.Canvas.CanvasImageInterpolation.Anisotropic);

    // Decoded pictures an edit took out of the document stay cached up to this many pixel bytes, so undoing
    // the edit doesn't re-decode them (ImageCache.Prune). The undo history's own budget is 64 MB too.
    // internal, not const: tests shrink it instead of allocating 64 MB of bitmaps.
    internal long ImageRetainBytes = 64L * 1024 * 1024;

    // True while an undo/redo snapshot is being swapped in (ApplyHistoryState), which prunes like an edit.
    private bool _applyingHistory;

    // Releases the bitmaps of pictures an EDIT removed, called when an edit's TextChanged is flushed. Before
    // this, only a document swap pruned, so deleted pictures stayed decoded until the next load (see
    // ImageCache._retired). A document with no decoded picture pays nothing — not even the walk.
    private void SweepRemovedImages()
    {
        if (_images.IsEmpty) return;
        _images.Prune(CollectLiveImageKeys(), ImageRetainBytes);
    }

    // Every RawBytes array the current document references (blocks, inline images, table cells at any
    // nesting depth, inline tables) — the live-key set for ImageCache.Prune.
    private HashSet<object> CollectLiveImageKeys()
    {
        var live = new HashSet<object>();
        void WalkBlocks(System.Collections.Generic.IEnumerable<Block> blocks)
        {
            foreach (var b in blocks)
            {
                switch (b)
                {
                    case ImageBlock ib when ib.RawBytes != null: live.Add(ib.RawBytes); break;
                    case Paragraph p:
                        foreach (var inl in p.Inlines)
                        {
                            if (inl is InlineImage ii && ii.RawBytes != null) live.Add(ii.RawBytes);
                            else if (inl is InlineTable it)
                                foreach (var row in it.Table.Cells)
                                    foreach (var cell in row) WalkBlocks(cell.Blocks);
                        }
                        break;
                    case TableBlock tb:
                        foreach (var row in tb.Cells)
                            foreach (var cell in row) WalkBlocks(cell.Blocks);
                        break;
                }
            }
        }
        if (Document != null) WalkBlocks(Document.Blocks);
        return live;
    }

    // Alt-text editor dialog (image context menu). AltText doesn't affect layout/rendering, so no
    // relayout — just the undo checkpoint and a status flush (IsModified/TextChanged).
    private async Task EditImageAltTextAsync(ImageBlock? block, InlineImage? inline)
    {
        if (IsReadOnly || XamlRoot == null || (block == null && inline == null)) return;
        var box = new Microsoft.UI.Xaml.Controls.TextBox
        {
            Text = block?.AltText ?? inline?.AltText ?? "",
            Width = 360,
        };
        box.SelectAll();
        var dlg = new Microsoft.UI.Xaml.Controls.ContentDialog
        {
            Title = RichEditorLocalization.GetString("AltText").TrimEnd('…', '.'),
            Content = box,
            PrimaryButtonText = RichEditorLocalization.GetString("OK"),
            CloseButtonText = RichEditorLocalization.GetString("Cancel"),
            DefaultButton = Microsoft.UI.Xaml.Controls.ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        // Guarded for the same reason as EditHyperlinkAsync's dialog: this is called as
        // `_ = EditImageAltTextAsync(...)`, and ShowAsync throws if a dialog is already open.
        // (SaveImageBytesAsync, ten lines below, has always had this guard — the pair was half-done.)
        Microsoft.UI.Xaml.Controls.ContentDialogResult result;
        try { result = await dlg.ShowAsync(); }
        catch (Exception ex) { RichEditorDiagnostics.Report(ex); return; }
        if (result != Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary) return;
        string? alt = string.IsNullOrWhiteSpace(box.Text) ? null : box.Text.Trim();
        PushUndo(null);
        if (block != null) block.AltText = alt;
        else inline!.AltText = alt;
        RaiseStatusChanged();
    }

    private async Task SaveImageBytesAsync(byte[]? raw, string? mime)
    {
        if (ImageSaveHandler == null || raw is not { Length: > 0 }) return;
        // host save failed/cancelled
        try { await ImageSaveHandler(raw, mime); }
        catch (Exception ex) { RichEditorDiagnostics.Report(ex); }
    }

    // An inline image's host paragraph layout depends on the (unchanged-size) bitmap, but a replaced
    // bitmap can differ; clearing the layout cache for the affected paragraph forces a reshape.
    private void ClearLayoutCacheFor(object imageElement)
    {
        if (imageElement is InlineImage) EvictLayouts(); // disposes: a bare Clear() left the natives to the finalizer
    }

    /// <summary>Inserts an image (from its encoded bytes) as a block at the caret. The natural pixel size
    /// is read from the header and scaled to fit the content width; the GPU decode is deferred to render.</summary>
    public void InsertImageBlock(byte[] bytes, string? mimeType = null) => InsertImageBlock(bytes, mimeType, 0, 0);

    // Core insert with an optional display size (used by paste to restore a copied image's size; 0 = the
    // image's natural header size). The width is capped to the content width either way. Paste passes
    // pushUndo: false because it snapshots BEFORE deleting the replaced selection (one undo step).
    internal void InsertImageBlock(byte[] bytes, string? mimeType, double displayW, double displayH, bool pushUndo = true)
    {
        // AllowImages is checked HERE, in the core both public entry points reach (InsertImageBlock and the
        // InsertImageBytes alias). Only the inline twin used to check it; the paste path was covered solely
        // because its caller tests the flag first, so a host that turned images off still got them through
        // the API. Upstream gates InsertImageBytes the same way.
        if (Document == null || IsReadOnly || !AllowImages || bytes.Length == 0 || !CaretCanHostBlock) return;

        if (pushUndo) PushUndo(null);

        var (nw, nh) = ImageInfo.GetPixelSize(bytes);
        double w = displayW > 0 ? displayW : (nw > 0 ? nw : 200);
        double h = displayH > 0 ? displayH : (nh > 0 ? nh : 200);
        double maxW = Math.Max(50, _layoutWidth - 40);
        if (w > maxW) { h *= maxW / w; w = maxW; }

        var img = new ImageBlock { Width = w, Height = h };
        img.SetImageData(bytes, mimeType ?? ImageMime.Detect(bytes));

        // Splits the caret paragraph and splices the image between head and remainder (works for
        // top-level paragraphs and table-cell paragraphs alike).
        InsertBlockAtCaret(img);
        AfterEdit();
    }

    /// <summary>Inserts an image inline at the caret (flows within the paragraph text). The natural size
    /// is read from the header and capped to a modest inline size.</summary>
    public void InsertInlineImage(byte[] bytes, string? mimeType = null) => InsertInlineImage(bytes, mimeType, 0, 0);

    // Core insert with an optional display size (used by paste to restore a copied inline image's size;
    // 0 = the image's natural header size). The width is capped to the modest inline max either way.
    // Paste passes pushUndo: false (it snapshots before deleting the replaced selection).
    internal void InsertInlineImage(byte[] bytes, string? mimeType, double displayW, double displayH, bool pushUndo = true)
    {
        if (Document == null || IsReadOnly || !AllowImages || bytes.Length == 0) return;
        if (_caret.Paragraph is not { } p) return;

        if (pushUndo) PushUndo(null);

        var (nw, nh) = ImageInfo.GetPixelSize(bytes);
        double w = displayW > 0 ? displayW : (nw > 0 ? nw : 80);
        double h = displayH > 0 ? displayH : (nh > 0 ? nh : 80);
        double maxW = Math.Max(40, Math.Min(_layoutWidth - 40, 240)); // inline images stay modest
        if (w > maxW) { h *= maxW / w; w = maxW; }

        var img = new InlineImage { Width = w, Height = h, Parent = p };
        img.SetImageData(bytes, mimeType ?? ImageMime.Detect(bytes));

        int splitIdx = SplitInlinesAt(p, _caret.Offset);
        p.Inlines.Insert(splitIdx, img);
        _caret = new TextPointer(p, _caret.Offset + 1);
        CollapseSelectionToCaret();
        UpdateParents(Document);
        AfterEdit();
    }

    // ---- block <-> inline image toggle (HWP "treat as character") ---------

    // Converts a block image into an inline image embedded in an adjacent paragraph OF THE SAME
    // CONTAINER (capped to the modest inline size so it flows within the line).
    //
    // "Adjacent" and "same container" are the whole point: resolving the index against Document.Blocks
    // returned -1 for an image inside a table cell, so "글자처럼 취급" on a cell image did nothing at all
    // — and the image menu offers that toggle for cell images, which are selectable and resizable.
    // (ConvertTableBlockToInline keeps hard-coding Document.Blocks on purpose: its menu entry is gated
    // on `tb.Parent is FlowDocument`, so a nested table is never offered the toggle in the first place.)
    internal void ConvertImageBlockToInline(ImageBlock src)
    {
        if (Document == null || src.RawBytes is not { } bytes) return;
        if (BlockContainerOf(src) is not { } container) return;
        int idx = container.IndexOf(src);
        if (idx < 0) return;

        Paragraph? anchor = null;
        bool atEnd = true;
        if (idx > 0 && container[idx - 1] is Paragraph prev) anchor = prev;
        else
            for (int i = idx + 1; i < container.Count && anchor == null; i++)
                if (container[i] is Paragraph next) { anchor = next; atEnd = false; }
        if (anchor == null) return;

        PushUndo(null);
        double w = src.Width, h = src.Height;
        // Inside a cell the cap must be the CELL's content width — the document width would let a 240px
        // inline image spill out of a narrow cell box. ParagraphWrapWidth is the single source both the
        // draw and the caret walks already use for exactly this question.
        double avail = FindCell(anchor) != null ? ParagraphWrapWidth(anchor) : _layoutWidth - 40;
        double maxW = Math.Max(40, Math.Min(avail, 240));
        if (w > maxW) { h *= maxW / w; w = maxW; }
        var inl = new InlineImage { Width = w, Height = h };
        inl.SetImageData(bytes, src.MimeType ?? ImageMime.Detect(bytes));

        container.Remove(src);
        if (atEnd) anchor.Inlines.Add(inl); else anchor.Inlines.Insert(0, inl);
        if (ReferenceEquals(_selectedBlock, src)) _selectedBlock = null;
        UpdateParents(Document);

        int off = 0;
        foreach (var i in anchor.Inlines) { off += InlineLen(i); if (ReferenceEquals(i, inl)) break; }
        _caret = new TextPointer(anchor, off);
        CollapseSelectionToCaret();
        AfterEdit();
    }

    // Promotes an inline image back to a block image (scaled to the content width), inserted right after
    // its host paragraph. Mirrors ConvertInlineTableToBlock.
    internal void ConvertInlineImageToBlock(Paragraph host, InlineImage src)
    {
        if (Document == null || src.RawBytes is not { } bytes) return;
        // The host paragraph's own container, not Document.Blocks: inside a table cell IndexOf returned
        // -1 and the command silently did nothing, even though the right-click that offers it fires on
        // cell inline images too (DrawInlineObjects registers them from the shared paragraph walk) and
        // cells are legitimate block containers (rule #3/#4).
        if (BlockContainerOf(host) is not { } container) return;
        int idx = container.IndexOf(host);
        if (idx < 0) return;

        PushUndo(null);
        double w = src.Width, h = src.Height;
        double maxW = Math.Max(50, _layoutWidth - 40);
        if (w > maxW) { h *= maxW / w; w = maxW; }
        var blk = new ImageBlock { Width = w, Height = h };
        blk.SetImageData(bytes, src.MimeType ?? ImageMime.Detect(bytes));

        RemoveInlineCharacter(host, src);
        container.Insert(idx + 1, blk);
        UpdateParents(Document);

        _selectedInline = null;
        _selectedBlock = blk;
        CollapseSelectionToCaret();
        RelayoutToViewport();
        RestartBlink();
        InvalidateCanvas();
        RaiseStatusChanged();
    }
}
