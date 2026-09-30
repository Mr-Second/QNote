using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using WinUIRichEditor.Documents;
using WinUIRichEditor.Formatters;

using Microsoft.UI.Xaml;

namespace WinUIRichEditor.Controls;

// Phase 5: clipboard. Copy/cut put plain text + HTML (CF_HTML) on the system clipboard and keep an
// internal rich snapshot for high-fidelity in-app paste. Paste priority: internal rich → external
// HTML → plain text (mirrors the Avalonia original).
public partial class RichEditor
{
    private FlowDocument? _internalClipboardDoc;
    private string? _internalClipboardText;

    // Windows lets one process open the clipboard at a time, and whatever watches it (clipboard history, a
    // clipboard manager, a remote-desktop client) opens it right after each change. A call landing in that
    // window fails with CLIPBRD_E_CANT_OPEN, and every call site below swallows it and moves on. Measured
    // 2026-09-24 on a dev machine: 6% of pastes lost the HTML read (a web page pasted as plain text, or
    // nothing when the text read failed too) and 6% of SetContent calls threw (the copy silently lost).
    // The window is milliseconds, so the calls retry that one error briefly; any other error is not retried.
    private const int ClipboardBusy = unchecked((int)0x800401D0); // CLIPBRD_E_CANT_OPEN
    private const int ClipboardAttempts = 10;
    private const int ClipboardRetryMs = 20;

    // The wait must be an await, never a sleep - for the synchronous SetContent/GetContent too. The watcher
    // that holds the clipboard is often reading it: it asks THIS process to render the formats we offered,
    // and that request is served on our UI thread. A Thread.Sleep retry blocks that thread, so the watcher
    // keeps the clipboard until it gives up and every retry fails (measured: 5 of 300 copies still lost with
    // 10 × 20 ms of sleeping; 0 with the same budget awaited).
    private static Task RetryWhileClipboardBusyAsync(Action call)
        => RetryWhileClipboardBusyAsync(() => { call(); return Task.FromResult(0); });

    private static async Task<T> RetryWhileClipboardBusyAsync<T>(Func<Task<T>> read)
    {
        for (int attempt = 1; ; attempt++)
        {
            try { return await read(); }
            catch (Exception ex) when (ex.HResult == ClipboardBusy && attempt < ClipboardAttempts)
            {
                await Task.Delay(ClipboardRetryMs);
            }
        }
    }

    /// <summary>Copies the current selection to the clipboard (plain text + HTML). Also copies a
    /// block-selected object (table / image / inline table), which has no text selection.</summary>
    public Task CopyAsync()
    {
        // Block-selected object: copy the whole object (there is no text selection for these).
        if (_selectedBlock is { } blk)
        {
            if (blk is ImageBlock ib)
                return CopyImageToClipboardAsync(ib.RawBytes, ib.Image, false, ib.Width, ib.Height);
            return CopyBlockToClipboard(blk); // table, divider, …
        }
        if (_selectedInline is { } sinl)
            return CopyImageToClipboardAsync(sinl.img.RawBytes, sinl.img.Image, true, sinl.img.Width, sinl.img.Height);
        if (_selectedInlineTable is { } sit)
            return CopyTableToClipboard(sit.it.Table);

        // A rectangular table-cell block selection: copy just the selected cells as a sub-table (so an
        // in-app paste, and the HTML/RTF handed to Word/HWP, reproduce only the highlighted rectangle —
        // not the whole table the linear BuildSelectionDocument would clone). All of an INLINE table's cells
        // (a staged Ctrl+A) are that inline table, and copy as it (CopyTableToClipboard).
        if (CellBlockSelection() is { } cb)
            return cb.tb.Parent is InlineTable && cb.r0 == 0 && cb.c0 == 0
                   && cb.r1 == cb.tb.Rows - 1 && cb.c1 == cb.tb.Columns - 1
                ? CopyTableToClipboard(cb.tb)
                : CopyBlockToClipboard(cb.tb.Extract(cb.r0, cb.c0, cb.r1, cb.c1));

        if (!HasSelection) return Task.CompletedTask;
        var range = new TextRange(_selStart, _selEnd);
        return SetClipboardFromSelection(BuildSelectionDocument(range), range.GetText());
    }

    // Puts a selection sub-document on the system clipboard as plain text + HTML (CF_HTML) + RTF, and keeps
    // the internal rich snapshot for a loss-free in-app paste. RTF matters for tables: HWP (and Word)
    // import table structure far more reliably from RTF than from HTML — without it, a copied table pastes
    // into HWP as plain text. The internal/HTML/RTF read paths all understand these.
    private async Task SetClipboardFromSelection(FlowDocument selDoc, string plain)
    {
        // Plain text is built with LF between paragraphs; LF-only shows as a single line in many Windows
        // consumers (Notepad, native text boxes), so normalize to the platform newline (CRLF) before it
        // reaches the system clipboard. Store the SAME normalized form internally so the paste round-trip
        // guard (system GetTextAsync == _internalClipboardText) still holds after the OS hands text back
        // as CRLF — otherwise the high-fidelity in-app rich paste is silently missed.
        plain = plain.ReplaceLineEndings();

        var dp = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        if (!string.IsNullOrEmpty(plain)) dp.SetText(plain);

        // One flavour at a time: each is handed to the package (which keeps its own copy) and let go before
        // the next is built. With a picture in the selection both carry its bytes as text — base64 in the
        // HTML twice over (the fragment and CF_HTML), hex in the RTF — and building all of them before
        // handing any over held 93 MB of strings at once for one 10 MB picture (CopyAllocationProbeTests).
        string? html = BuildSelectionHtml(selDoc);
        if (!string.IsNullOrEmpty(html))
            try { dp.SetHtmlFormat(HtmlFormatHelper.CreateHtmlFormat(html)); }
            catch (Exception ex) { RichEditorDiagnostics.Report(ex); }
        html = null;

        string? rtf = null;
        // RTF is best-effort
        try { rtf = RtfDocumentFormatter.Write(selDoc); }
        catch (Exception ex) { RichEditorDiagnostics.Report(ex); }
        if (!string.IsNullOrEmpty(rtf))
            try { dp.SetRtf(rtf); }
            catch (Exception ex) { RichEditorDiagnostics.Report(ex); }
        rtf = null;

        _internalClipboardDoc = selDoc.Clone();
        _internalClipboardText = plain;
        try { await RetryWhileClipboardBusyAsync(() => Clipboard.SetContent(dp)); }
        catch (Exception ex) { RichEditorDiagnostics.Report(ex); }
    }

    /// <summary>Cuts the current selection (copy + delete). Also cuts a block-selected object.</summary>
    public async Task CutAsync()
    {
        if (IsReadOnly) return;
        // Delete BEFORE awaiting the copy. What is copied is captured synchronously when CopyAsync starts, but
        // the clipboard write may wait for a busy clipboard (RetryWhileClipboardBusyAsync), and during that wait
        // the UI thread handles input - a keystroke could move the selection, and the cut would delete that.
        if (HasBlockSelection)
        {
            var copyObject = CopyAsync();
            DeleteSelectedObject(); // pushes its own undo checkpoint + AfterEdit
            await copyObject;
            return;
        }
        if (!HasSelection) return;
        var copy = CopyAsync();
        PushUndo(null);
        DeleteSelection();
        AfterEdit();
        await copy;
    }

    // Copies a whole block object selected as an object (table, divider, …). Builds a one-block selection
    // document so the internal-rich and HTML clipboards reproduce the structure on paste (an in-app paste
    // re-inserts the real block; Word/HWP/Excel receive the <table> HTML and TSV text).
    private Task CopyBlockToClipboard(Block block)
    {
        var selDoc = new FlowDocument();
        selDoc.Blocks.Add((Block)block.Clone());
        return SetClipboardFromSelection(selDoc, BlockPlainText(block));
    }

    // Copies a table as what it is. An inline table ("treat as character") goes out as a one-paragraph fragment
    // holding it, which paste puts at the caret as an inline table (InsertDocumentAtCaret → InsertInlinesAtCaret);
    // it came back a block table, splitting the paragraph it was pasted into (user decision, 2026-09-13). The
    // HTML and RTF built from that fragment carry the inline marker, so a paste of them into this editor stays
    // inline too. Any other table copies as a block.
    private Task CopyTableToClipboard(TableBlock tb)
    {
        if (tb.Parent is not InlineTable it) return CopyBlockToClipboard(tb);
        var line = new Paragraph();
        line.Inlines.Add((Inline)it.Clone());
        var selDoc = new FlowDocument();
        selDoc.Blocks.Add(line);
        return SetClipboardFromSelection(selDoc, BlockPlainText(tb));
    }

    // Plain-text projection of a single block: a table becomes TSV (tab between cells, newline between
    // rows) so the system clipboard text is meaningful for Excel/Notepad and the internal round-trip guard
    // (clipText == _internalClipboardText) matches; other blocks reuse PlainTextOf.
    private static string BlockPlainText(Block block)
    {
        if (block is TableBlock tb)
        {
            var sb = new System.Text.StringBuilder();
            for (int r = 0; r < tb.Rows; r++)
            {
                for (int c = 0; c < tb.Columns; c++)
                {
                    if (c > 0) sb.Append('\t');
                    sb.Append(CellPlainText(tb.Cells[r][c]));
                }
                if (r < tb.Rows - 1) sb.Append('\n');
            }
            return sb.ToString();
        }
        var fd = new FlowDocument();
        fd.Blocks.Add(block);
        return PlainTextOf(fd);
    }

    private static string CellPlainText(TableCell cell)
    {
        var sb = new System.Text.StringBuilder();
        bool first = true;
        foreach (var b in cell.Blocks)
            if (b is Paragraph p)
            {
                if (!first) sb.Append(' ');
                first = false;
                foreach (var inl in p.Inlines) if (inl is Run r && r.Text != null) sb.Append(r.Text);
            }
        return sb.ToString();
    }

    /// <summary>Pastes clipboard content at the caret (internal rich → RTF → HTML → image → TSV → plain
    /// text). When <paramref name="plainOnly"/> is <see langword="true"/> (Ctrl+Shift+V) every rich format
    /// is skipped and only the plain text is inserted.</summary>
    public async Task PasteAsync(bool plainOnly = false)
    {
        if (IsReadOnly || Document == null || _caret.Paragraph == null) return;
        DataPackageView view;
        try { view = await RetryWhileClipboardBusyAsync(() => Task.FromResult(Clipboard.GetContent())); }
        catch (Exception ex) { RichEditorDiagnostics.Report(ex); return; }

        string? clipText = null;
        if (view.Contains(StandardDataFormats.Text))
        {
            try { clipText = await RetryWhileClipboardBusyAsync(() => view.GetTextAsync().AsTask()); }
            catch (Exception ex) { RichEditorDiagnostics.Report(ex); clipText = null; }
        }

        // 1) Internal rich snapshot — only when the system clipboard text still matches what we copied.
        if (!plainOnly && AllowRichPaste && _internalClipboardDoc != null && clipText != null && clipText == _internalClipboardText)
        {
            var snapshot = AdaptToCapabilities(_internalClipboardDoc.Clone(), out bool emptied);
            if (emptied) return; // everything it held is disallowed here: nothing to paste
            PushUndo(null);
            if (HasSelection) DeleteSelection();
            InsertDocumentAtCaret(snapshot);
            AfterEdit();
            return;
        }

        // 2) External RTF (Word/HWP): tried before CF_HTML because RTF embeds image bytes, whereas
        //    Word's CF_HTML only references temp files that may already be gone.
        if (!plainOnly && AllowRichPaste && view.Contains(StandardDataFormats.Rtf))
        {
            try
            {
                string rtf = await RetryWhileClipboardBusyAsync(() => view.GetRtfAsync().AsTask());
                if (!string.IsNullOrEmpty(rtf) && RtfDocumentFormatter.LooksLikeRtf(rtf))
                {
                    var parsedRtf = RtfDocumentFormatter.Parse(rtf);
                    bool empty = parsedRtf.Blocks.Count == 0
                        || (parsedRtf.Blocks.Count == 1 && parsedRtf.Blocks[0] is Paragraph ep && ep.Inlines.Count == 0);
                    if (!empty)
                    {
                        FlowDocument chosen = parsedRtf;
                        // HWP/Word often embed pictures as WMF/EMF, which the RTF parser can't decode.
                        // When the RTF clearly carried pictures we lost, prefer the HTML flavor if IT
                        // yields images (data: URIs or clipboard temp files); otherwise keep the RTF
                        // result — its table fidelity is better.
                        if (!HasAnyImage(parsedRtf) && rtf.Contains(@"\pict") && view.Contains(StandardDataFormats.Html))
                        {
                            try
                            {
                                string cfhtml0 = await RetryWhileClipboardBusyAsync(() => view.GetHtmlFormatAsync().AsTask());
                                string frag0 = HtmlFormatHelper.GetStaticFragment(cfhtml0);
                                if (!string.IsNullOrWhiteSpace(frag0))
                                {
                                    var hd = await HtmlDocumentFormatter.ParseHtmlAsync(frag0, AllowLocalFileImagesOnPaste, AllowRemoteImagesOnPaste);
                                    if (HasAnyImage(hd)) chosen = hd;
                                }
                            }
                            // keep the RTF result
                            catch (Exception ex) { RichEditorDiagnostics.Report(ex); }
                        }
                        chosen = AdaptToCapabilities(chosen, out bool emptiedRtf);
                        if (emptiedRtf) return;
                        PushUndo(null);
                        if (HasSelection) DeleteSelection();
                        InsertDocumentAtCaret(chosen);
                        AfterEdit();
                        return;
                    }
                }
            }
            // fall through to HTML/plain
            catch (Exception ex) { RichEditorDiagnostics.Report(ex); }
        }

        // 3) External HTML.
        if (!plainOnly && AllowRichPaste && view.Contains(StandardDataFormats.Html))
        {
            try
            {
                string cfhtml = await RetryWhileClipboardBusyAsync(() => view.GetHtmlFormatAsync().AsTask());
                string fragment = HtmlFormatHelper.GetStaticFragment(cfhtml);
                if (!string.IsNullOrWhiteSpace(fragment))
                {
                    // Async: remote (http) images download off the UI thread so a slow network can't freeze
                    // the paste (the model build itself still runs on the UI thread inside ParseHtmlAsync).
                    var pd = AdaptToCapabilities(
                        await HtmlDocumentFormatter.ParseHtmlAsync(fragment, AllowLocalFileImagesOnPaste, AllowRemoteImagesOnPaste),
                        out bool emptiedHtml);
                    if (emptiedHtml) return;
                    PushUndo(null);
                    if (HasSelection) DeleteSelection();
                    InsertDocumentAtCaret(pd);
                    AfterEdit();
                    return;
                }
            }
            // fall through to image/TSV/plain text
            catch (Exception ex) { RichEditorDiagnostics.Report(ex); }
        }

        // 4) Bitmap image (e.g. a screenshot or copied picture). An image copied in-app carries our own
        //    original bytes (no PNG re-encode) plus an "inline;w;h" meta restoring inline-ness and size.
        if (!plainOnly && AllowRichPaste && AllowImages
            && (view.Contains(StandardDataFormats.Bitmap) || view.Contains(ImageBytesFormat)))
        {
            try
            {
                var meta = await TryGetImageMetaAsync(view);
                var bytes = await TryGetOwnImageBytesAsync(view) ?? await ReadClipboardBitmapBytesAsync(view);
                if (bytes is { Length: > 0 })
                {
                    // Snapshot BEFORE deleting the replaced selection, so one undo restores both the
                    // removed image and the text it replaced (the insert must not push its own step).
                    PushUndo(null);
                    if (HasSelection) DeleteSelection();
                    if (meta is { Inline: true } im)
                        InsertInlineImage(bytes, null, im.W, im.H, pushUndo: false);
                    else
                        InsertImageBlock(bytes, null, meta?.W ?? 0, meta?.H ?? 0, pushUndo: false);
                    return;
                }
            }
            // fall through to TSV/plain text
            catch (Exception ex) { RichEditorDiagnostics.Report(ex); }
        }

        // 5) Tab-separated text (e.g. Excel/HWP cells copied without HTML) -> rebuild as a table.
        if (!plainOnly && AllowRichPaste && AllowTables && CaretCanHostBlock && !string.IsNullOrEmpty(clipText) && LooksTabular(clipText!))
        {
            PushUndo(null);
            if (HasSelection) DeleteSelection();
            InsertTableFromTsv(clipText!);
            AfterEdit();
            return;
        }

        // 6) Plain text. Multi-line text splits into PARAGRAPHS (Word/HWP behavior — Enter = new
        //    paragraph in this model); single-line text splices inline.
        if (clipText != null) InsertPlainTextBlock(clipText);
    }

    // Inserts plain text at the caret, mapping newlines to paragraph breaks. Falls back to the inline
    // InsertText for single-line text (keeps its formatting-inheritance and undo-coalescing behavior).
    private void InsertPlainTextBlock(string text)
    {
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (!text.Contains('\n')) { InsertText(text); return; }
        if (Document == null || IsReadOnly || _caret.Paragraph == null) return;
        PushUndo(null);
        if (HasSelection) DeleteSelection();
        var pd = new FlowDocument();
        foreach (var line in text.Split('\n'))
            pd.Blocks.Add(new Paragraph { Inlines = { new Run { Text = line } } });
        InsertDocumentAtCaret(pd); // in-cell carets fall back to soft-'\n' plain text inside (see there)
        AfterEdit();
    }

    // Application-private clipboard formats: the image's original encoded bytes (base64, so an in-editor
    // copy/paste round-trips without re-encoding to PNG) plus a small meta string "inline;width;height" (so
    // an inline image pastes back as inline at its displayed size). A decoded bitmap is written alongside
    // them for other applications.
    private const string ImageBytesFormat = "WinUIRichEditor.Image";
    private const string ImageMetaFormat = "WinUIRichEditor.ImageMeta";

    /// <summary>Copies an image to the system clipboard. Writes a decoded bitmap (for other apps) plus the
    /// original encoded bytes and an "inline;w;h" meta string (for a loss-free in-app round-trip that
    /// restores inline-ness and the displayed size). Encodes the decoded bitmap to PNG when no original
    /// bytes are available.</summary>
    internal async Task CopyImageToClipboardAsync(byte[]? raw, Microsoft.Graphics.Canvas.CanvasBitmap? bmp,
        bool inline = false, double width = 0, double height = 0)
    {
        byte[]? bytes = raw ?? ImageEncoder.ToPngBytes(bmp);
        if (bytes is not { Length: > 0 }) return;

        // The internal rich snapshot is now stale; clear it so it can't hijack the next paste.
        _internalClipboardDoc = null;
        _internalClipboardText = null;

        try
        {
            var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            using (var w = new Windows.Storage.Streams.DataWriter(stream))
            {
                w.WriteBytes(bytes);
                await w.StoreAsync();
                await w.FlushAsync();
                w.DetachStream();
            }
            stream.Seek(0);
            var reference = Windows.Storage.Streams.RandomAccessStreamReference.CreateFromStream(stream);
            var dp = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            dp.SetBitmap(reference);
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            dp.SetData(ImageMetaFormat, $"{(inline ? 1 : 0)};{width.ToString(inv)};{height.ToString(inv)}");
            // Original bytes (base64) only when we have them — avoids the PNG re-encode on in-app paste.
            if (raw is { Length: > 0 }) dp.SetData(ImageBytesFormat, Convert.ToBase64String(raw));
            await RetryWhileClipboardBusyAsync(() => Clipboard.SetContent(dp));
        }
        catch (Exception ex) { RichEditorDiagnostics.Report(ex); }
    }

    // The application-private original bytes (base64) when present, decoded back to the raw encoded image.
    private static async Task<byte[]?> TryGetOwnImageBytesAsync(DataPackageView view)
    {
        if (!view.Contains(ImageBytesFormat)) return null;
        try
        {
            if (await RetryWhileClipboardBusyAsync(() => view.GetDataAsync(ImageBytesFormat).AsTask()) is string b64 && b64.Length > 0)
                return Convert.FromBase64String(b64);
        }
        catch (Exception ex) { RichEditorDiagnostics.Report(ex); }
        return null;
    }

    // The application-private "inline;w;h" meta string, parsed. Null when absent or malformed.
    private static async Task<(bool Inline, double W, double H)?> TryGetImageMetaAsync(DataPackageView view)
    {
        if (!view.Contains(ImageMetaFormat)) return null;
        string? meta;
        try { meta = await RetryWhileClipboardBusyAsync(() => view.GetDataAsync(ImageMetaFormat).AsTask()) as string; }
        catch (Exception ex) { RichEditorDiagnostics.Report(ex); return null; }
        if (string.IsNullOrEmpty(meta)) return null;
        var parts = meta.Split(';');
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (parts.Length != 3
            || !int.TryParse(parts[0], out int inl)
            || !double.TryParse(parts[1], System.Globalization.NumberStyles.Float, inv, out double w)
            || !double.TryParse(parts[2], System.Globalization.NumberStyles.Float, inv, out double h))
            return null;
        return (inl == 1, w, h);
    }

    private static async Task<byte[]?> ReadClipboardBitmapBytesAsync(DataPackageView view)
    {
        var bmpRef = await RetryWhileClipboardBusyAsync(() => view.GetBitmapAsync().AsTask());
        using var stream = await bmpRef.OpenReadAsync();
        uint size = (uint)stream.Size;
        if (size == 0) return null;
        var bytes = new byte[size];
        using var reader = new Windows.Storage.Streams.DataReader(stream);
        await reader.LoadAsync(size);
        reader.ReadBytes(bytes);
        return bytes;
    }

    // Heuristic for "this plain text is a copied spreadsheet grid". Every non-empty line must contain a
    // tab, and at least one line must have 2+ non-empty cells (so tab-indented prose isn't mistaken for
    // a table).
    internal static bool LooksTabular(string text)
    {
        bool anyMultiCell = false;
        foreach (var line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (line.Length == 0) continue;
            if (!line.Contains('\t')) return false;
            int nonEmpty = 0;
            foreach (var f in line.Split('\t')) if (!string.IsNullOrWhiteSpace(f)) nonEmpty++;
            if (nonEmpty >= 2) anyMultiCell = true;
        }
        return anyMultiCell;
    }

    private void InsertTableFromTsv(string text)
    {
        if (Document == null) return;
        var lines = new List<string>(text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'));
        while (lines.Count > 1 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);

        int cols = 1;
        foreach (var l in lines) cols = Math.Max(cols, l.Split('\t').Length);

        var tb = new TableBlock(lines.Count, cols);
        for (int r = 0; r < lines.Count; r++)
        {
            var parts = lines[r].Split('\t');
            for (int c = 0; c < cols; c++)
                ((Run)tb.Cells[r][c].Para.Inlines[0]).Text = c < parts.Length ? parts[c] : "";
        }
        InsertBlockAtCaret(tb);
    }

    // Inserts a block (table/image/divider) at the caret, splitting the caret paragraph so the block
    // lands between the head and the remainder. Works in any block container — top-level Document.Blocks
    // or a table cell's block list (rule #3: containers generalize) — so tables/dividers nest in cells.
    private void InsertBlockAtCaret(Block block)
    {
        if (Document == null || _caret.Paragraph is not { } p) return;
        IList<Block>? container = p.Parent switch
        {
            FlowDocument d => d.Blocks,
            TableCell tc => tc.Blocks,
            _ => null
        };
        if (container == null) return;
        int idx = container.IndexOf(p);
        if (idx < 0) return;

        // At the paragraph's start the block goes BEFORE it, at its end AFTER it; only in between does the paragraph
        // split. Always splitting left an empty paragraph ahead of (or behind) the block (upstream round 34 — the
        // rule DropBlock already had). An empty paragraph takes the block after it, as before.
        int len = GetParagraphLength(p);
        int off = Math.Clamp(_caret.Offset, 0, len);
        if (len > 0 && (off == 0 || off == len))
        {
            block.Parent = p.Parent;
            container.Insert(off == 0 ? idx : idx + 1, block);
            UpdateParents(Document); // NormalizeBlocks puts a paragraph after a block that ends the list
            int bi = container.IndexOf(block);
            var landing = bi + 1 < container.Count && container[bi + 1] is Paragraph next ? next : p;
            _caret = new TextPointer(landing, 0);
            CollapseSelectionToCaret();
            return;
        }

        int splitIdx = SplitInlinesAt(p, _caret.Offset);
        // The tail is the same paragraph's continuation, so it keeps the FULL paragraph format
        // (the old two-field copy dropped list/heading/spacing/quote across a block insert).
        var tail = p.CloneFormat();
        while (p.Inlines.Count > splitIdx)
        {
            var inl = p.Inlines[splitIdx];
            p.Inlines.RemoveAt(splitIdx);
            inl.Parent = tail;
            tail.Inlines.Add(inl);
        }
        if (tail.Inlines.Count == 0) tail.Inlines.Add(new Run { Text = "" });
        if (p.Inlines.Count == 0) p.Inlines.Add(new Run { Text = "" });
        block.Parent = p.Parent;
        tail.Parent = p.Parent;
        container.Insert(idx + 1, block);
        container.Insert(idx + 2, tail);

        _caret = new TextPointer(tail, 0);
        CollapseSelectionToCaret();
        UpdateParents(Document);
    }

    // True when the caret paragraph lives in a container that can receive a sibling block. Checked
    // BEFORE PushUndo so an impossible insert doesn't leave an empty undo step.
    private bool CaretCanHostBlock
        => _caret.Paragraph is { } p && (p.Parent is FlowDocument or TableCell);

    // file:// images in pasted HTML are blocked by default (don't read arbitrary local files on paste).
    // The HTML parser still allows paths under %TEMP% even when this is false — Word/HWP CF_HTML
    // reference the images they just copied via clipboard temp files (see HtmlDocumentFormatter.LoadImage).
    private bool AllowLocalFileImagesOnPaste => false;

    // Whether a parsed document contains any image (block or inline, at any table nesting depth).
    private static bool HasAnyImage(FlowDocument d)
    {
        static bool InBlocks(System.Collections.Generic.IEnumerable<Block> blocks)
        {
            foreach (var b in blocks)
            {
                switch (b)
                {
                    case ImageBlock: return true;
                    case Paragraph p:
                        foreach (var inl in p.Inlines)
                        {
                            if (inl is InlineImage) return true;
                            if (inl is InlineTable it)
                                foreach (var row in it.Table.Cells)
                                    foreach (var cell in row)
                                        if (InBlocks(cell.Blocks)) return true;
                        }
                        break;
                    case TableBlock tb:
                        foreach (var row in tb.Cells)
                            foreach (var cell in row)
                                if (InBlocks(cell.Blocks)) return true;
                        break;
                }
            }
            return false;
        }
        return InBlocks(d.Blocks);
    }

    /// <summary>Identifies the <see cref="AllowRemoteImagesOnPaste"/> dependency property.</summary>
    public static readonly DependencyProperty AllowRemoteImagesOnPasteProperty = DependencyProperty.Register(
        nameof(AllowRemoteImagesOnPaste), typeof(bool), typeof(RichEditor), new PropertyMetadata(true));

    /// <summary>Whether parsing HTML may download remote (http/https) images. Default true; set false
    /// for privacy-sensitive hosts — markup can reference tracking pixels, and loading it would
    /// otherwise issue network requests. Downloads are capped at 20MB per image and ~5s per parse.
    /// <para>Despite the name this governs every HTML entry point, not only paste:
    /// <see cref="LoadHtmlAsync"/> (the one other path that actually reaches the network) plus
    /// <see cref="LoadHtml"/>/<see cref="InsertHtml"/>, which are threaded for consistency even though
    /// the synchronous parser never performs network I/O.</para></summary>
    public bool AllowRemoteImagesOnPaste
    {
        get => (bool)GetValue(AllowRemoteImagesOnPasteProperty);
        set => SetValue(AllowRemoteImagesOnPasteProperty, value);
    }

    // A trimmed FlowDocument for the current selection that preserves each paragraph's
    // list/heading/alignment/indent/background AND inline images, and keeps whole tables / block images /
    // dividers the selection spans — so internal copy/paste (and copied-out HTML) round-trips structure,
    // not just runs. Mirrors the Avalonia original's CaptureBlockStructure + CloneParagraphRange. Falls back
    // to the inline-only builder for intra-cell selections and anything whose top-level block we can't
    // resolve (e.g. inside an inline table).
    private FlowDocument BuildSelectionDocument(TextRange range)
    {
        var doc = new FlowDocument();
        var sp = range.Start.Paragraph;
        var ep = range.End.Paragraph;
        if (Document == null || sp == null || ep == null) return BuildInlineSelectionDocument(range);

        var startTop = FindTopLevelBlock(sp);
        var endTop = FindTopLevelBlock(ep);
        int si = startTop != null ? Document.Blocks.IndexOf(startTop) : -1;
        int ei = endTop != null ? Document.Blocks.IndexOf(endTop) : -1;
        if (si < 0 || ei < 0 || si > ei) return BuildInlineSelectionDocument(range);

        // Single top-level block.
        if (si == ei)
        {
            var b = Document.Blocks[si];
            if (b is Paragraph)
            {
                int to = ReferenceEquals(sp, ep) ? range.End.Offset : GetParagraphLength(sp);
                doc.Blocks.Add(CloneParagraphRange(sp, range.Start.Offset, to));
                return doc;
            }
            // A selection confined to a single table cell is intra-cell text, not a table copy.
            if (b is TableBlock && FindCell(sp) is { } sc && FindCell(ep) is { } ec
                && ReferenceEquals(sc.tb, ec.tb) && sc.r == ec.r && sc.c == ec.c)
                return BuildInlineSelectionDocument(range);
            // Whole non-paragraph block (table across cells, block image, divider).
            if (b.Clone() is Block whole) doc.Blocks.Add(whole);
            return doc;
        }

        // Spans multiple top-level blocks: trim the first/last paragraphs to the selection, clone the rest
        // (other paragraphs and any tables/images/dividers) whole.
        for (int k = si; k <= ei; k++)
        {
            var b = Document.Blocks[k];
            if (b is Paragraph para)
            {
                int from = k == si ? range.Start.Offset : 0;
                int to = k == ei ? range.End.Offset : GetParagraphLength(para);
                doc.Blocks.Add(CloneParagraphRange(para, from, to));
            }
            else if (b.Clone() is Block whole) doc.Blocks.Add(whole);
        }
        return doc;
    }

    // Wraps the selection's exported HTML so consumers inherit the editor's base font/size. ToHtml omits
    // the 10pt body default and an empty font-family, which would otherwise fall back to the consumer's own
    // defaults (Word/HWP → Calibri 11pt) and look like the font/size was lost; runs with an explicit
    // font/size still emit overriding spans. Double-quoted attribute + single-quoted family (valid for
    // multi-word / CJK names), matching ToHtml. Null when there is nothing to emit.
    private string? BuildSelectionHtml(FlowDocument doc)
    {
        // Written into one pre-sized builder: formatting the HTML to a string and then interpolating it into
        // the <div> copied a picture's whole base64 payload once more (see HtmlDocumentFormatter.EstimateCapacity).
        string family = (DefaultFontFamily ?? "").Replace("'", "").Replace("\"", "");
        string open = $"<div style=\"font-family:'{family}';font-size:10pt\">";
        var sb = new System.Text.StringBuilder(HtmlDocumentFormatter.EstimateCapacity(doc) + open.Length + 6);
        sb.Append(open);
        HtmlDocumentFormatter.AppendHtml(sb, doc);
        if (sb.Length == open.Length) return null; // nothing to put in it
        return sb.Append("</div>").ToString();
    }

    // Inline-only fallback: builds a FlowDocument from a selection's rich inlines, splitting paragraphs at
    // the "\n" run markers GetRichInlines inserts between paragraphs. Used for intra-cell text and any
    // selection whose block structure we can't resolve (paragraph properties don't matter there — the
    // destination paragraph keeps its own).
    private FlowDocument BuildInlineSelectionDocument(TextRange range)
    {
        var doc = new FlowDocument();
        var p = new Paragraph();
        foreach (var inl in range.GetRichInlines())
        {
            if (inl is Run r && r.Text == "\n") { doc.Blocks.Add(p); p = new Paragraph(); }
            else p.Inlines.Add(inl);
        }
        doc.Blocks.Add(p);
        return doc;
    }

    // The top-level Document.Blocks entry that (directly or via nested table cells / inline tables) contains
    // the given paragraph. Null when it isn't reachable from Document.Blocks.
    private Block? FindTopLevelBlock(Paragraph p)
    {
        if (Document == null) return null;
        foreach (var b in Document.Blocks)
            if (BlockContains(b, p)) return b;
        return null;
    }

    private static bool BlockContains(Block b, Paragraph p)
    {
        if (ReferenceEquals(b, p)) return true;
        if (b is TableBlock tb)
        {
            for (int r = 0; r < tb.Rows; r++)
                for (int c = 0; c < tb.Columns; c++)
                    foreach (var cb in tb.Cells[r][c].Blocks)
                        if (BlockContains(cb, p)) return true;
        }
        else if (b is Paragraph para)
        {
            foreach (var inl in para.Inlines)
                if (inl is InlineTable it)
                {
                    var t = it.Table;
                    for (int r = 0; r < t.Rows; r++)
                        for (int c = 0; c < t.Columns; c++)
                            foreach (var cb in t.Cells[r][c].Blocks)
                                if (BlockContains(cb, p)) return true;
                }
        }
        return false;
    }

    // Clones one paragraph's formatting plus the inlines (text runs trimmed to [from,to); atomic inline
    // objects whose extent falls inside the range) — so HTML export and in-app paste keep bullets/numbers,
    // headings, alignment, indent, background, and pasted-back inline pictures.
    private static Paragraph CloneParagraphRange(Paragraph p, int from, int to)
    {
        var np = new Paragraph
        {
            MarginTop = p.MarginTop, MarginBottom = p.MarginBottom, MarginRight = p.MarginRight,
            TextAlignment = p.TextAlignment, LineHeight = p.LineHeight, LineSpacing = p.LineSpacing,
            ListType = p.ListType, ListMarker = p.ListMarker, HeadingLevel = p.HeadingLevel,
            Background = p.Background, Indent = p.Indent, IsQuote = p.IsQuote, ListLevel = p.ListLevel,
        };
        int idx = 0;
        foreach (var inl in p.Inlines)
        {
            int len = InlineLen(inl);
            int segStart = idx, segEnd = idx + len;
            idx = segEnd;
            if (len == 0 || segEnd <= from || segStart >= to) continue;
            if (inl is Run r && r.Text != null)
            {
                int a = Math.Max(from, segStart) - segStart;
                int b = Math.Min(to, segEnd) - segStart;
                if (b > a) { var c = (Run)r.Clone(); c.Text = r.Text.Substring(a, b - a); c.Parent = np; np.Inlines.Add(c); }
            }
            else if (inl is not Run && segStart >= from && segEnd <= to)
            {
                var c = (Inline)inl.Clone(); c.Parent = np; np.Inlines.Add(c);
            }
        }
        if (np.Inlines.Count == 0) np.Inlines.Add(new Run { Text = "" });
        return np;
    }

    // Inserts a document's content at the caret. Single-paragraph content is spliced inline (keeps
    // formatting, no new paragraph); multi-block content splits the caret paragraph and inserts blocks.
    // TAKES OWNERSHIP of pd's blocks — they are wired into the live document directly. Every caller
    // hands over a throwaway document (a fresh RTF/HTML/plain-text parse, or an explicit Clone for the
    // retained internal-clipboard snapshot), so the full deep clone this used to do per paste was pure
    // duplicate work — on top of the parse itself, big pastes were cloned once more for nothing.
    private void InsertDocumentAtCaret(FlowDocument pd)
    {
        if (Document == null || _caret.Paragraph == null) return;
        var p = _caret.Paragraph;
        var pasted = pd.Blocks;
        if (pasted.Count == 0) return;

        if (pasted.Count == 1 && pasted[0] is Paragraph onlyP)
        {
            InsertInlinesAtCaret(onlyP.Inlines);
            UpdateParents(Document);
            return;
        }

        // Container generalization (rule #3): Document.Blocks for a top-level caret, the cell's block
        // list for a caret in a table cell. A cell used to fall back to PLAIN TEXT here, which flattened
        // every pasted paragraph — bullets/numbering, headings, alignment and character formatting were
        // all dropped when pasting multi-paragraph content into a cell. Cells are recursive containers
        // (rule #4) and hold exactly these blocks, so the same splice works unchanged.
        // The plain-text path stays for a caret whose parent chain isn't wired to either container.
        // NOT InsertText there: every caller already pushed the undo snapshot (and deleted the
        // selection), so InsertText's own PushUndo(null) would split one paste into two undo steps.
        if (BlockContainerOf(p) is not { } container || p.Parent is not { } owner)
        {
            InsertPlainNoUndo(PlainTextOf(pd));
            return;
        }

        int idx = container.IndexOf(p);
        if (idx < 0) { InsertPlainNoUndo(PlainTextOf(pd)); return; }

        // Split the caret paragraph: head stays (p), tail becomes a new paragraph inserted right after.
        // The tail is p's own continuation — carry the full paragraph format (see InsertBlockAtCaret).
        int splitIdx = SplitInlinesAt(p, _caret.Offset);
        var tail = p.CloneFormat();
        while (p.Inlines.Count > splitIdx)
        {
            var inl = p.Inlines[splitIdx];
            p.Inlines.RemoveAt(splitIdx);
            inl.Parent = tail;
            tail.Inlines.Add(inl);
        }
        if (tail.Inlines.Count == 0) tail.Inlines.Add(new Run { Text = "" });
        if (p.Inlines.Count == 0) p.Inlines.Add(new Run { Text = "" });
        tail.Parent = owner;
        container.Insert(idx + 1, tail);

        // Splice pasted blocks between head (p) and tail: the first pasted paragraph merges into head,
        // the rest are inserted as blocks before tail. Caret lands at the start of the tail.
        int insertAt = idx + 1; // tail is here; new blocks go before it
        for (int i = 0; i < pasted.Count; i++)
        {
            var b = pasted[i];
            if (i == 0 && b is Paragraph fp)
            {
                // The first pasted paragraph merges into the caret paragraph (p). If p was EMPTY (a
                // blank line / fresh cell), adopt the pasted paragraph's own format — list, heading,
                // alignment, indent, spacing — so pasting a bullet into an empty cell stays a bullet.
                // When p already has content, keep p's format (the paste flows into existing text).
                if (GetParagraphLength(p) == 0)
                {
                    p.CopyFormatFrom(fp);
                    p.Inlines.Clear();
                }
                foreach (var inl in fp.Inlines.ToList()) { inl.Parent = p; p.Inlines.Add(inl); }
                if (p.Inlines.Count == 0) p.Inlines.Add(new Run { Text = "" });
                TextRange.CoalesceRuns(p);
            }
            else
            {
                b.Parent = owner;
                container.Insert(insertAt++, b);
            }
        }
        _caret = new TextPointer(tail, 0);
        CollapseSelectionToCaret();
        UpdateParents(Document);
    }

    // Inserts plain text at the caret WITHOUT pushing an undo step — for fallback paths whose caller
    // already snapshotted (paste, InsertHtml). Newlines stay soft breaks; the caller runs AfterEdit.
    private void InsertPlainNoUndo(string text)
    {
        if (_caret.Paragraph is not { } p) return;
        if (text.Contains('\r')) text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        int at = _caret.Offset;
        TryInsertTextCore(p, text, at);
        _caret = new TextPointer(p, at + text.Length);
        CollapseSelectionToCaret();
    }

    // Splices a list of inlines into the caret paragraph (single-paragraph paste). Caret advances past
    // them. The inlines are MOVED, not cloned — the only caller is InsertDocumentAtCaret, which owns
    // its source document (see there); cloning here deep-copied any inline table a second time.
    private void InsertInlinesAtCaret(IList<Inline> inlines)
    {
        var p = _caret.Paragraph!;
        int splitIdx = SplitInlinesAt(p, _caret.Offset);
        int addedLen = 0, n = 0;
        foreach (var inl in inlines)
        {
            // Skip a lone trailing empty run that paragraphs carry as a placeholder.
            if (inl is Run r && string.IsNullOrEmpty(r.Text) && inlines.Count > 1) continue;
            inl.Parent = p;
            p.Inlines.Insert(splitIdx + n, inl);
            n++;
            addedLen += InlineLen(inl);
        }
        _caret = new TextPointer(p, _caret.Offset + addedLen);
        TextRange.CoalesceRuns(p);
        CollapseSelectionToCaret();
    }

    private static string PlainTextOf(FlowDocument pd)
    {
        var sb = new System.Text.StringBuilder();
        bool first = true;
        foreach (var b in pd.Blocks)
            if (b is Paragraph p)
            {
                if (!first) sb.Append('\n');
                first = false;
                foreach (var inl in p.Inlines) if (inl is Run r && r.Text != null) sb.Append(r.Text);
            }
        return sb.ToString();
    }

    // ---- capability flags on INCOMING content -------------------------------------------------------
    // Foreign content — every rich paste flavour, and InsertHtml — is adapted to what this editor allows
    // before it is spliced in: with AllowImages off its pictures are dropped, with AllowTables off its
    // tables are unwrapped into their cells' content (the text survives, only the grid goes). Both flags
    // promise that ("image insertion and image paste", "table insertion"), yet the rich paste paths
    // checked only AllowRichPaste, so a host that turned images or tables off still received them from
    // any web page or Word document pasted in. (Decided 2026-09-12. Upstream drops inline images from
    // in-app paste only.)
    // NOT for content already in the document: a text drag moves it (DragText also splices through
    // InsertDocumentAtCaret, and adapting there would delete a picture the user merely moved), and
    // opening a document (Load*) is not an insert.
    // `emptied`: the flags removed everything the content had — the caller then inserts nothing, so the
    // paste leaves no empty undo step and no modified flag behind.
    private FlowDocument AdaptToCapabilities(FlowDocument pd, out bool emptied)
    {
        emptied = false;
        if (AllowImages && AllowTables) return pd;
        bool had = !IsEmptyContent(pd);
        var blocks = AdaptBlocks(pd.Blocks);
        pd.Blocks.Clear();
        foreach (var b in blocks) { b.Parent = pd; pd.Blocks.Add(b); }
        emptied = had && IsEmptyContent(pd);
        return pd;
    }

    private List<Block> AdaptBlocks(IEnumerable<Block> source)
    {
        var result = new List<Block>();
        foreach (var b in source)
        {
            switch (b)
            {
                case ImageBlock when !AllowImages:
                    break;
                case TableBlock tb when !AllowTables:
                    // Unwrapped in reading order, one cell after another; an empty cell adds no blank line.
                    foreach (var (_, _, cell) in tb.LogicalCells())
                        foreach (var inner in AdaptBlocks(cell.Blocks))
                            if (!(inner is Paragraph ip && IsBlank(ip))) result.Add(inner);
                    break;
                case TableBlock tb:
                    AdaptCells(tb);
                    result.Add(tb);
                    break;
                case Paragraph p:
                    AdaptInlines(p);
                    result.Add(p);
                    break;
                default:
                    result.Add(b);
                    break;
            }
        }
        return result;
    }

    // A table that stays keeps its grid; what its cells hold is adapted, and each cell keeps its
    // never-paragraph-less invariant.
    private void AdaptCells(TableBlock tb)
    {
        foreach (var (_, _, cell) in tb.LogicalCells())
        {
            var blocks = AdaptBlocks(cell.Blocks);
            if (!blocks.Any(x => x is Paragraph)) blocks.Add(new Paragraph { Inlines = { new Run { Text = "" } } });
            cell.Blocks = blocks;
        }
    }

    private void AdaptInlines(Paragraph p)
    {
        for (int i = p.Inlines.Count - 1; i >= 0; i--)
        {
            switch (p.Inlines[i])
            {
                case InlineImage when !AllowImages:
                    p.Inlines.RemoveAt(i);
                    break;
                case InlineTable it when !AllowTables:
                    // An inline table becomes its cells' text, in place, a space between cells.
                    p.Inlines.RemoveAt(i);
                    int at = i;
                    bool first = true;
                    foreach (var (_, _, cell) in it.Table.LogicalCells())
                        foreach (var inner in AdaptBlocks(cell.Blocks))
                        {
                            if (inner is not Paragraph ip || IsBlank(ip)) continue;
                            if (!first) p.Inlines.Insert(at++, new Run { Text = " " });
                            first = false;
                            foreach (var inl in ip.Inlines.ToList()) p.Inlines.Insert(at++, inl);
                        }
                    break;
                case InlineTable it:
                    AdaptCells(it.Table);
                    break;
            }
        }
        if (p.Inlines.Count == 0) p.Inlines.Add(new Run { Text = "" });
    }

    private static bool IsBlank(Paragraph p) => p.Inlines.All(i => i is Run r && string.IsNullOrEmpty(r.Text));
    private static bool IsEmptyContent(FlowDocument pd) => pd.Blocks.All(b => b is Paragraph p && IsBlank(p));
}
