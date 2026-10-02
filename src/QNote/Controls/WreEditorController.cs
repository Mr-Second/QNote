using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using QNote.Markdown;
using QNote.Models;
using QNote.Services;
using QNote.Text;
using QNote.WreMarkdown;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
using WinUIRichEditor.Controls;
using WinUIRichEditor.Documents;

namespace QNote.Controls;

/// <summary>
/// View-side wrapper around the vendored WinUIRichEditor <see cref="RichEditor"/>
/// (NOT a view-model — it holds the control, so it lives in code-behind wiring per
/// mvvm-guidelines). Mirrors the surface the old <c>RichTextEditorController</c>
/// exposed to <c>NotesPage</c>: content in/out (<see cref="SetMarkdownAsync"/> /
/// <see cref="GetMarkdown"/>), dirty-check, char count, image insertion and
/// open-original. The implementations are WRE API calls now — the RTF/TOM engine
/// (msftedit) is gone.
///
/// Image model (schema v6): storage is Markdown with
/// <c>![alt](qnote-img:&lt;sha256&gt;)</c> references; the editor document only ever
/// holds the compressed display copies. Load resolves references from
/// <c>note_images.display_bytes</c> through <see cref="MarkdownDocumentFormatter.ImageBytesProvider"/>;
/// save re-derives a content address by hashing each inline image's bytes. The
/// formatter's hash is over the bytes it was handed (the DISPLAY copy), while the
/// stored reference is the ORIGINAL image's sha — so this controller keeps the
/// display-bytes-hash → original-sha map and rewrites the emitted document's
/// references back to the original addresses (the same content-addressing trick
/// the RTF-era controller used, now applied to the bridge output).
/// </summary>
public sealed class WreEditorController
{
    private readonly RichEditor _editor;
    private readonly ILogger<WreEditorController>? _log;
    private readonly IImageService? _images;
    private readonly INoteService? _notes;

    /// <summary>Bumped on every <see cref="SetMarkdownAsync"/>; stale loads abort at their await.</summary>
    private int _loadGeneration;

    /// <summary>True while the document is being swapped in — the wholesale <c>Document</c>
    /// assignment raises <c>TextChanged</c> synchronously, which must not read as an edit.</summary>
    private bool _loading;

    /// <summary>Markdown emitted right after a load — the no-op baseline for <see cref="IsDirty"/>.</summary>
    private string _baseline = string.Empty;

    /// <summary>
    /// SHA256(display bytes) → original sha256, filled at load from
    /// <c>note_images.display_bytes</c> and extended at insert. The save path rewrites
    /// each formatter-derived reference (a display-bytes hash) back to its original
    /// address through this map; unknown addresses are left as-is (a foreign image the
    /// host never linked to a row).
    /// </summary>
    private readonly Dictionary<string, string> _displayBytesSha = new(StringComparer.Ordinal);

    public WreEditorController(
        RichEditor editor,
        ILogger<WreEditorController>? log = null,
        IImageService? images = null,
        INoteService? notes = null)
    {
        _editor = editor;
        _log = log;
        _images = images;
        _notes = notes;

        _editor.TextChanged += (_, _) => { if (!_loading) ContentChanged?.Invoke(); };
        _editor.SelectionChanged += (_, _) => SelectionChanged?.Invoke();

        // WRE has no host hook for image double-click / open-original (S2 investigation:
        // no public event, and the built-in image context menu owns Save/Replace/AltText).
        // The one host-facing seam is the Save-As handler, wired below to the same
        // temp-copy workaround the packaged-app viewer path needs.
        _editor.ImageSaveHandler = SaveImageBytesAsync;
    }

    /// <summary>Raised on user edits (suppressed during programmatic load via <see cref="SetMarkdownAsync"/>).</summary>
    public event Action? ContentChanged;

    /// <summary>Raised when the selection (and thus its formatting) may have changed.</summary>
    public event Action? SelectionChanged;

    /// <summary>Raised when an import failed and the view should tell the user (localized message).</summary>
    public event Action<string>? ImportFailed;

    /// <summary>
    /// Set by the view: the id of the note currently open, so an import can link the
    /// original to that note immediately (metadata is only known at import time).
    /// Returns <c>null</c> when no note is open.
    /// </summary>
    public Func<long?>? CurrentNoteIdProvider { get; set; }

    // ---------- Markdown in/out (schema v6) ----------

    /// <summary>
    /// Loads the note's Markdown: <see cref="MarkdownParser"/> → neutral document →
    /// <see cref="MarkdownDocumentFormatter.ToFlowDocument"/> (resolving
    /// <c>qnote-img:</c> references to the note's display copies) → the editor's
    /// <see cref="RichEditor.Document"/>. The baseline is captured right after the
    /// swap (see <see cref="IsDirty"/>).
    /// </summary>
    public async Task SetMarkdownAsync(string? markdown)
    {
        var md = markdown ?? string.Empty;
        _displayBytesSha.Clear();

        // Fast note switching: two loads can interleave at the image-row await;
        // the stale one must not overwrite the newer document.
        var generation = ++_loadGeneration;

        Dictionary<string, byte[]> displayBytes = new(StringComparer.Ordinal);
        if (_notes is not null && CurrentNoteIdProvider?.Invoke() is { } noteId &&
            md.Contains(MarkdownParser.ImageSchemePrefix, StringComparison.Ordinal))
        {
            IReadOnlyList<NoteImage> rows;
            try
            {
                rows = await _notes.GetNoteImagesWithDisplayAsync(noteId);
            }
            catch (Exception ex)
            {
                // Degrade: render the text without images rather than failing the load.
                _log?.LogWarning(ex, "Loading display copies for note {NoteId} failed", noteId);
                rows = [];
            }

            if (generation != _loadGeneration)
                return;

            foreach (var row in rows)
            {
                if (row.DisplayBytes is { } bytes)
                {
                    _displayBytesSha[Sha256Hex(bytes)] = row.Sha256;
                    displayBytes[row.Sha256] = bytes;
                }
            }
        }

        var content = MarkdownParser.Parse(md);
        var document = MarkdownDocumentFormatter.ToFlowDocument(
            content, sha => displayBytes.TryGetValue(sha, out var bytes) ? bytes : null);

        // Assigning Document fires TextChanged synchronously (wholesale swap). Suppress
        // that so a fresh load never reads as an edit; the baseline is captured right
        // after the swap so IsDirty() is false until real input. GetMarkdown() is
        // idempotent through a load.
        _loading = true;
        try
        {
            _editor.Document = document;
            _baseline = GetMarkdown();
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>
    /// Saves the document as Markdown: <see cref="MarkdownDocumentFormatter.ToDocumentContent"/>
    /// → (reference remap) → <see cref="MarkdownEmitter"/>. Each inline image is
    /// content-addressed by hashing its bytes; a display copy's hash is rewritten to the
    /// original sha it resolves to (see <see cref="_displayBytesSha"/>), so the
    /// emitted references keep matching <c>note_images</c>.
    /// </summary>
    public string GetMarkdown()
    {
        if (_editor.Document is not { } document)
            return string.Empty;
        return GetMarkdown(document);
    }

    private string GetMarkdown(FlowDocument document)
    {
        var content = MarkdownDocumentFormatter.ToDocumentContent(document);
        return MarkdownEmitter.Emit(RemapImageReferences(content));
    }

    /// <summary>
    /// Rewrites every <see cref="DocumentImage"/>'s content address from the display
    /// copy's hash back to the original sha it resolves to. Addresses with no mapping
    /// (a picture the host never linked to a row) pass through unchanged. Table cells
    /// are walked too — an image inside a table must remap like any other or the
    /// note_images sync would orphan it.
    /// </summary>
    private DocumentContent RemapImageReferences(DocumentContent content)
    {
        if (_displayBytesSha.Count == 0)
            return content;

        DocumentInline Remap(DocumentInline inline) =>
            inline is DocumentImage image && _displayBytesSha.TryGetValue(image.Sha256, out var original)
                ? image with { Sha256 = original }
                : inline;

        IReadOnlyList<DocumentInline> RemapCell(
            IReadOnlyList<DocumentInline> cell) => cell.Select(Remap).ToList();

        var blocks = new List<DocumentBlock>(content.Blocks.Count);
        foreach (var block in content.Blocks)
        {
            IReadOnlyList<IReadOnlyList<IReadOnlyList<DocumentInline>>>? cells = null;
            if (block.TableCells is { } tableCells)
                cells = tableCells
                    .Select(row => (IReadOnlyList<IReadOnlyList<DocumentInline>>)row.Select(RemapCell).ToList())
                    .ToList();

            blocks.Add(block with
            {
                Inlines = block.Inlines.Select(Remap).ToList(),
                TableCells = cells,
            });
        }
        return new DocumentContent(blocks);
    }

    /// <summary>Lowercase hex SHA256 of the given bytes (identity key for image matching).</summary>
    private static string Sha256Hex(byte[] bytes)
    {
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    // ---------- Plain text / status ----------

    /// <summary>Plain text of the document (paragraphs joined by the platform newline).</summary>
    public string GetPlainText() => _editor.GetPlainText();

    public bool IsEmpty => string.IsNullOrWhiteSpace(_editor.GetPlainText());

    /// <summary>
    /// True when the emitted Markdown differs from the post-load baseline. The compare
    /// is content-level (not a control-normalization artifact): the bridge output is
    /// canonical, so a format-only edit (bold/list) is visible even though the plain
    /// text is identical.
    /// </summary>
    public bool IsDirty => GetMarkdown() != _baseline;

    /// <summary>Marks the current state as the saved baseline (call after a successful persist).</summary>
    public void MarkClean() => _baseline = GetMarkdown();

    public void Focus() => _editor.Focus(FocusState.Programmatic);

    /// <summary>Editor content width available to an image (shared by import paths).</summary>
    private double AvailableImageWidth
    {
        get
        {
            var width = _editor.ActualWidth;
            return width > 0 ? width : 480;
        }
    }

    // ---------- Image insertion ----------

    /// <summary>
    /// Inserts an image from a file at the current selection: the original is stored
    /// content-addressed, the compressed copy is inlined, and the resulting image is
    /// linked to the current note. Returns true when the image was inserted.
    /// </summary>
    public async Task<bool> InsertImageFromFileAsync(string filePath)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(filePath);
            var extension = file.FileType;
            if (!WicImageNormalizer.IsSupported(extension))
            {
                ImportFailed?.Invoke($"不支持的图片格式：{extension}");
                return false;
            }

            var buffer = await FileIO.ReadBufferAsync(file);
            var bytes = System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.ToArray(buffer);
            return await InsertImageBytesAsync(bytes, extension);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Inserting image from {Path} failed", filePath);
            ImportFailed?.Invoke("插入图片失败，请查看日志");
            return false;
        }
    }

    /// <summary>Inserts every supported image file from a drop.</summary>
    public async Task InsertImageFilesAsync(IEnumerable<string> filePaths)
    {
        foreach (var path in filePaths)
            await InsertImageFromFileAsync(path);
    }

    private async Task<bool> InsertImageBytesAsync(byte[] bytes, string extension)
    {
        var (imported, error) = await TryImportAndLinkAsync(bytes, extension, CurrentNoteIdProvider?.Invoke());
        if (imported is null)
        {
            ImportFailed?.Invoke(error ?? "插入图片失败，请查看日志");
            return false;
        }

        _editor.Focus(FocusState.Programmatic);
        _editor.InsertInlineImage(imported.DisplayBytes, BlipToMime(imported.DisplayBlip));
        ContentChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// The import core shared by the insert path and save-time adoption: normalize a
    /// compressed display copy, store the original content-addressed (dedup), link the
    /// row to the given note, and record the display-bytes remap entry. No editor
    /// mutation and no user-facing failure event — the insert path surfaces the message
    /// via <see cref="ImportFailed"/>, adoption just logs and skips. Returns
    /// (imported, null) or (null, message) on failure.
    /// </summary>
    private async Task<(ImportedImage? Image, string? Error)> TryImportAndLinkAsync(
        byte[] bytes, string extension, long? noteId)
    {
        if (_images is null)
            return (null, "图片服务不可用，无法插入图片");

        NormalizedImage normalized;
        try
        {
            normalized = await WicImageNormalizer.NormalizeAsync(bytes, extension);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Image decode failed ({Ext})", extension);
            return (null, "无法解码该图片（可能是不支持的格式）");
        }

        ImportedImage imported;
        try
        {
            imported = await _images.ImportAsync(
                bytes, normalized.Ext, normalized.DisplayBytes, normalized.Blip,
                normalized.PixelWidth, normalized.PixelHeight);
        }
        catch (Exception ex)
        {
            _log?.LogError(ex, "Storing original image failed");
            return (null, "保存图片失败，请查看日志");
        }

        // Link the original to the note now — import-time is the only moment
        // the byte size / dimensions are known without a WIC round-trip on reload.
        if (_notes is not null && noteId is { } id)
        {
            try
            {
                await _notes.AddNoteImagesAsync(id, [ToNoteImage(id, imported)]);
            }
            catch (Exception ex)
            {
                _log?.LogWarning(ex, "Linking image {Sha} to note {NoteId} failed", imported.Sha256, id);
            }
        }

        // The display-bytes identity lets the save path map this pict back to the
        // original sha (the same entry a later load rebuilds from note_images).
        _displayBytesSha[Sha256Hex(imported.DisplayBytes)] = imported.Sha256;

        return (imported, null);
    }

    private static NoteImage ToNoteImage(long noteId, ImportedImage image) => new()
    {
        NoteId = noteId,
        Sha256 = image.Sha256,
        Ext = image.Ext,
        ByteSize = image.ByteSize,
        Width = image.PixelWidth,
        Height = image.PixelHeight,
        DisplayBytes = image.DisplayBytes,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static string BlipToMime(string blip) =>
        string.Equals(blip, RtfImagePayload.JpegBlip, StringComparison.Ordinal) ? "image/jpeg" : "image/png";

    // ---------- Save-time image adoption (cross-note paste / external bitmap) ----------

    /// <summary>
    /// Save-time adoption of unlinked inline images (task 10-03). A paste can put
    /// bytes into the document that no <c>note_images</c> row of the open note backs:
    /// an in-app copy from ANOTHER note carries that note's display copy (its hash
    /// resolves to no original here), an external bitmap was never imported at all.
    /// The emitted Markdown would carry a dead <c>qnote-img:</c> reference — the next
    /// reload degrades it to alt text and the sync then prunes it. This walks the
    /// current document and links every such image to the open note: a display hash
    /// that some row anywhere already carries clones that row (zero extra disk — the
    /// original file is shared, content-addressed); anything else goes through the
    /// full import pipeline. Only rows and <see cref="_displayBytesSha"/> entries are
    /// added — the document itself is not touched (the image is already visible).
    /// </summary>
    public Task AdoptUnlinkedImagesAsync() => AdoptUnlinkedImagesAsync(_editor.Document);

    /// <summary>
    /// Save-time entry point: adopt unlinked images, then capture the flush snapshot
    /// (Markdown + dirty flag) from the SAME document instance the adoption walked.
    /// Snapshotting via the plain <see cref="GetMarkdown"/> instead would race a note
    /// switch swapping <see cref="RichEditor.Document"/> during the adoption's DB
    /// awaits — the flush would then persist the NEW note's Markdown into the OLD
    /// note. The captured reference is immune to that wholesale swap. In-place edits
    /// landing inside the (rare — SQLite awaits complete synchronously) yielding
    /// awaits are benign: text edits simply appear in the snapshot; only a mid-window
    /// image PASTE could skip adoption, which degrades to the pre-task dead-ref
    /// behavior and is re-adopted by the next save.
    /// </summary>
    public async Task<(string Markdown, bool Changed)> AdoptAndSnapshotAsync()
    {
        if (_editor.Document is not { } document)
            return (string.Empty, false);

        await AdoptUnlinkedImagesAsync(document);
        var markdown = GetMarkdown(document);
        return (markdown, markdown != _baseline);
    }

    private async Task AdoptUnlinkedImagesAsync(FlowDocument? document)
    {
        if (_notes is null || _images is null || document is null)
            return;

        // Read the note id ONCE up front: every row links to the note whose content
        // the adoption walked, even if a note switch lands inside the awaits below.
        if (CurrentNoteIdProvider?.Invoke() is not { } noteId)
            return;

        // Doc-walk cost equals the save path's own walk in GetMarkdown right after —
        // the accepted double-walk (adoption must precede the emission so the remap
        // entries below are in place when the references are rewritten).
        var unknown = CollectUnlinkedImages(document);
        if (unknown.Count == 0)
            return;

        IReadOnlyDictionary<string, string> originalByDisplay;
        try
        {
            originalByDisplay = await _notes.FindOriginalShaByDisplayHashAsync(
                unknown.Select(image => image.Sha).ToList());
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Adoption lookup of original images failed");
            return; // the save continues with unlinked refs (pre-adoption behavior)
        }

        foreach (var (displaySha, bytes, extension) in unknown)
        {
            try
            {
                if (originalByDisplay.TryGetValue(displaySha, out var originalSha))
                {
                    // Cross-note paste: clone the original's row (incl. its display
                    // copy) onto this note — the pasted bytes ARE that copy.
                    var metadata = await _notes.GetImageMetadataByShaAsync([originalSha]);
                    if (metadata.TryGetValue(originalSha, out var row))
                    {
                        await _notes.AddNoteImagesAsync(noteId,
                            [row with { NoteId = noteId, CreatedAt = DateTimeOffset.UtcNow }]);
                        _displayBytesSha[displaySha] = originalSha;
                    }
                }
                else
                {
                    // External bitmap: full pipeline. The remap key is the sha of the
                    // bytes ACTUALLY in the document; the emitted ref becomes the
                    // imported ORIGINAL's sha. The row's re-normalized display copy may
                    // differ in compression from the pasted bytes — expected; the reload
                    // renders the row's copy.
                    var (imported, _) = await TryImportAndLinkAsync(bytes, extension, noteId);
                    if (imported is not null)
                        _displayBytesSha[displaySha] = imported.Sha256;
                }
            }
            catch (Exception ex)
            {
                _log?.LogWarning(ex, "Adopting pasted image {DisplaySha} into note {NoteId} failed",
                    displaySha, noteId);
            }
        }
    }

    /// <summary>
    /// Every image in the document whose content address is NOT in
    /// <see cref="_displayBytesSha"/> (linked to no row of the open note), with its
    /// inline bytes and best-guess extension. Deduped by address — one remap entry
    /// covers all instances. Byte-less images (render cache only) are skipped,
    /// mirroring the formatter's emission rule (they cannot be content-addressed).
    /// Walks table cells and inline tables at any nesting depth, like the formatter.
    /// </summary>
    private List<(string Sha, byte[] Bytes, string Extension)> CollectUnlinkedImages(FlowDocument document)
    {
        var found = new List<(string, byte[], string)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Visit(byte[]? rawBytes, string? mimeType)
        {
            if (rawBytes is not { Length: > 0 })
                return;
            var sha = Sha256Hex(rawBytes);
            if (_displayBytesSha.ContainsKey(sha) || !seen.Add(sha))
                return;
            found.Add((sha, rawBytes, MimeToExtension(mimeType)));
        }

        void WalkBlocks(IEnumerable<Block> blocks)
        {
            foreach (var block in blocks)
            {
                switch (block)
                {
                    case Paragraph paragraph:
                        foreach (var inline in paragraph.Inlines)
                        {
                            switch (inline)
                            {
                                case InlineImage image:
                                    Visit(image.RawBytes, image.MimeType);
                                    break;
                                case InlineTable inlineTable:
                                    WalkBlocks(CellBlocks(inlineTable.Table));
                                    break;
                            }
                        }
                        break;

                    case ImageBlock imageBlock:
                        Visit(imageBlock.RawBytes, imageBlock.MimeType);
                        break;

                    case TableBlock table:
                        WalkBlocks(CellBlocks(table));
                        break;
                }
            }
        }

        WalkBlocks(document.Blocks);
        return found;
    }

    private static IEnumerable<Block> CellBlocks(TableBlock table) =>
        table.Cells.SelectMany(row => row).SelectMany(cell => cell.Blocks);

    /// <summary>Import extension for the pasted bytes: WRE carries a MIME type on its
    /// images (what the clipboard/insert handed it); anything unknown is PNG — the
    /// WinRT bitmap read re-encodes to PNG.</summary>
    private static string MimeToExtension(string? mimeType) =>
        string.Equals(mimeType, "image/jpeg", StringComparison.OrdinalIgnoreCase) ? "jpg" : "png";

    // ---------- Save-as (built-in image context menu) ----------

    /// <summary>
    /// Handles the built-in image menu's "Save As…": writes the image bytes to a
    /// file-picker location.
    /// </summary>
    private async Task SaveImageBytesAsync(byte[] bytes, string? mime)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary,
                SuggestedFileName = "image",
            };
            var ext = string.Equals(mime, "image/jpeg", StringComparison.OrdinalIgnoreCase) ? ".jpg" : ".png";
            picker.FileTypeChoices.Add(ext.TrimStart('.').ToUpperInvariant(), [ext]);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
            if (await picker.PickSaveFileAsync() is not { } file)
                return;
            await FileIO.WriteBytesAsync(file, bytes);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Saving image via the context menu failed");
            ImportFailed?.Invoke("保存图片失败，请查看日志");
        }
    }

    // ---------- Drag & drop ----------

    /// <summary>Inserts dropped image files; other formats are ignored (editing unaffected).</summary>
    public async Task HandleDropAsync(DataPackageView data)
    {
        if (!data.Contains(StandardDataFormats.StorageItems))
            return;
        try
        {
            var paths = new List<string>();
            foreach (var item in await data.GetStorageItemsAsync())
            {
                if (item is StorageFile file && WicImageNormalizer.IsSupported(file.FileType))
                    paths.Add(file.Path);
            }
            if (paths.Count > 0)
                await InsertImageFilesAsync(paths);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Image drop failed");
        }
    }
}
