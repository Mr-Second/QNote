using Microsoft.Extensions.Logging;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using QNote.Markdown;
using QNote.Models;
using QNote.Services;
using QNote.Text;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
namespace QNote.Controls;

/// <summary>
/// View-side wrapper around a <see cref="RichEditBox"/> (NOT a view-model — it holds
/// the control, so it lives in code-behind wiring per mvvm-guidelines). Owns content
/// in/out (<see cref="SetMarkdownAsync"/> / <see cref="GetMarkdown"/>), all formatting
/// ops, selection-state reads, paste/drag image import, and the double-click "open
/// original" hit-test.
///
/// Image model (schema B2): storage is Markdown with
/// <c>![alt](qnote-img:&lt;sha256&gt;)</c> references; the RichEdit document only ever
/// holds the compressed display copies. Load resolves references from
/// <c>note_images.display_bytes</c>; save re-identifies each inline pict by hashing
/// its bytes against those same rows — byte identity survives RichEdit reloads,
/// deletions, reordering and undo, none of which the alt marker survives (msftedit
/// rewrites it to "Image" on RTF parse; it stays only as the in-session fallback for
/// freshly inserted picts). The controller keeps an RTF baseline captured right after
/// a load so the save guard can tell a real edit (including a format-only one) from
/// RichEdit's normalization noise.
/// </summary>
public sealed class RichTextEditorController
{
    private readonly RichEditBox _box;
    private readonly ILogger<RichTextEditorController>? _log;
    private readonly IImageService? _images;
    private readonly INoteService? _notes;
    private int _suppressChange;
    private bool _interactedSinceLoad;

    /// <summary>Bumped on every user interaction; invalidates deferred load-noise signals.</summary>
    private int _loadGeneration;

    /// <summary>Generation the current baseline was captured in.</summary>
    private int _baselineGeneration;

    /// <summary>Bumped on every <see cref="SetMarkdownAsync"/>; stale loads abort at their await.</summary>
    private int _markdownLoadGeneration;

    /// <summary>RTF as RichEdit re-emits it right after a load — the no-op baseline.</summary>
    private string _baselineRtf = string.Empty;

    /// <summary>
    /// Ordered <c>qnote-img:</c> shas of the picts as loaded from the note's Markdown
    /// references (or inserted since), by U+FFFC ordinal — the double-click fallback
    /// when RichEdit strips the alt on an RTF reload. <see cref="GetMarkdown"/>
    /// refreshes it with the byte-identity-resolved ordinals, so deletions since load
    /// stop shifting the mapping at the next save.
    /// </summary>
    private readonly List<string?> _pictShas = [];

    /// <summary>
    /// SHA256(display bytes) → original sha, filled at load from
    /// <c>note_images.display_bytes</c> and extended at insert. The save path
    /// resolves each inline pict by hashing its bytes against this map — its
    /// content-addressed identity, independent of RichEdit's alt handling.
    /// </summary>
    private readonly Dictionary<string, string> _displayBytesSha = new(StringComparer.Ordinal);

    public RichTextEditorController(
        RichEditBox box,
        ILogger<RichTextEditorController>? log = null,
        IImageService? images = null,
        INoteService? notes = null)
    {
        _box = box;
        _log = log;
        _images = images;
        _notes = notes;
        _box.TextChanged += OnTextChanged;
        _box.Paste += OnPaste;
        _box.KeyDown += (_, _) => MarkInteracted();
        // RichEditBox's inner editing surface marks PointerPressed as handled before it
        // reaches the control, so `+=` never fires. AddHandler with handledEventsToo
        // forces delivery — required for the image double-click hit-test.
        _box.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnPointerPressed), true);
        _box.SelectionChanged += (_, _) => SelectionChanged?.Invoke();
    }

    /// <summary>Raised on user edits (suppressed during programmatic <see cref="SetRtf"/>).</summary>
    public event Action? ContentChanged;

    /// <summary>Raised when the selection (and thus its formatting) may have changed.</summary>
    public event Action? SelectionChanged;

    /// <summary>
    /// Set by the view: the id of the note currently open, so an import can link the
    /// original to that note immediately (metadata is only known at import time).
    /// Returns <c>null</c> when no note is open.
    /// </summary>
    public Func<long?>? CurrentNoteIdProvider { get; set; }

    /// <summary>Raised when an import failed and the view should tell the user (localized message).</summary>
    public event Action<string>? ImportFailed;

    /// <summary>Raised when a double-click hit an image whose original file is missing/unlaunchable.</summary>
    public event Action<string>? ImageOpenFailed;
    /// <summary>Raised when the user double-clicked a note image; the view opens the original file.</summary>
    public event Action<string>? OpenImageRequested;

    // ---------- RTF in/out ----------

    /// <summary>Loads RTF into the document. Invalid RTF falls back to a blank document
    /// (logged, never crashes); empty content → blank document. Internal engine of
    /// <see cref="SetMarkdownAsync"/> — image bookkeeping is owned by the MD path.</summary>
    public void SetRtf(string? rtf)
    {
        _interactedSinceLoad = false;
        _loadGeneration++;
        _suppressChange++;
        try
        {
            if (string.IsNullOrWhiteSpace(rtf))
            {
                _box.Document.SetText(TextSetOptions.None, string.Empty);
                return;
            }
            try
            {
                _box.Document.SetText(TextSetOptions.FormatRtf, rtf);
            }
            catch (Exception ex)
            {
                _log?.LogWarning(ex, "Invalid RTF in note content; falling back to a blank document");
                _box.Document.SetText(TextSetOptions.None, string.Empty);
            }
        }
        finally
        {
            // Capture the baseline SYNCHRONOUSLY: this is the document as RichEdit emits
            // it right after normalizing the load, so a later compare is normalization-
            // insensitive. It must not be deferred — a user who toggles bold (or types)
            // before the dispatcher runs would otherwise have their edit captured AS the
            // baseline, and the save guard would drop it as "no change".
            _baselineRtf = GetRtf();
            _baselineGeneration = _loadGeneration;

            // RichEditBox also raises TextChanged for programmatic SetText — and can
            // raise it again asynchronously when the box first renders / applies its
            // default formatting. A synchronous using-scope expires before those
            // deferred events fire, so release suppression on the dispatcher instead:
            // anything queued by the load is still ignored, user input comes after.
            // Only the counter is released here; the baseline is already set above.
            _box.DispatcherQueue.TryEnqueue(() => _suppressChange--);
        }
    }

    public string GetRtf()
    {
        _box.Document.GetText(TextGetOptions.FormatRtf, out var rtf);
        return rtf;
    }

    public string GetPlainText()
    {
        _box.Document.GetText(TextGetOptions.None, out var text);
        return text;
    }

    public bool IsEmpty
    {
        get
        {
            _box.Document.GetText(TextGetOptions.None, out var text);
            return string.IsNullOrWhiteSpace(text);
        }
    }

    // ---------- Markdown in/out (schema B2) ----------

    /// <summary>
    /// Loads the note's Markdown: <see cref="MarkdownParser"/> → neutral document →
    /// <see cref="RtfEmitter"/> (resolving <c>qnote-img:</c> references to the note's
    /// display copies) → <see cref="SetRtf"/>. Display geometry fits the note's
    /// ORIGINAL dimensions (stored on the image rows) into the editor width — the
    /// display bytes themselves are already the compressed copy. References with no
    /// resolvable row (legacy rows without a blob) degrade to the alt text.
    /// </summary>
    public async Task SetMarkdownAsync(string? markdown)
    {
        var md = markdown ?? string.Empty;

        _displayBytesSha.Clear();
        _pictShas.Clear();

        // Fast note switching: two loads can interleave at the image-row await;
        // the stale one must not overwrite the newer document.
        var generation = ++_markdownLoadGeneration;

        Dictionary<string, RtfImagePayload> payloads = new(StringComparer.Ordinal);
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

            if (generation != _markdownLoadGeneration)
                return;

            foreach (var row in rows)
            {
                if (row.DisplayBytes is { } displayBytes)
                    _displayBytesSha[Sha256Hex(displayBytes)] = row.Sha256;
                if (row.DisplayBytes is { } bytes && RtfImagePayload.FromBytes(bytes) is { } payload)
                {
                    // Display size comes from the ORIGINAL dimensions (row.Width/Height),
                    // not the compressed copy's pixels — the compressed copy may be
                    // downscaled by policy, and the note should still show the same
                    // layout it had in the RTF era.
                    var (widthDip, heightDip) = ImageDisplaySize.Fit(row.Width, row.Height, AvailableImageWidth);
                    payloads[row.Sha256] = payload with { DisplayWidthDip = widthDip, DisplayHeightDip = heightDip };
                }
            }
        }

        _pictShas.AddRange(MarkdownParser.ReferencedImageShas(md));

        var content = MarkdownParser.Parse(md);
        SetRtf(string.IsNullOrWhiteSpace(md) ? null : RtfEmitter.Emit(content, sha =>
            payloads.TryGetValue(sha, out var payload) ? payload : null));
    }

    /// <summary>
    /// Saves the document as Markdown: TOM walk (<see cref="TomDocumentWalker"/>)
    /// → neutral document → <see cref="MarkdownEmitter"/>. Each inline pict is
    /// re-identified by hashing its display bytes against <c>note_images</c> (the
    /// in-memory map built at load and extended at insert), falling back to the
    /// pict's alt — freshly inserted picts carry a <c>qnote:</c> alt until the next
    /// reload strips it. Unresolvable picts are dropped rather than written with a
    /// bogus reference.
    /// </summary>
    public string GetMarkdown()
    {
        if (IsEmpty)
            return string.Empty;

        var rtf = GetRtf();
        var picts = RtfPictInspector.FindPicts(rtf);
        var resolved = new List<string?>(picts.Count);
        foreach (var pict in picts)
        {
            var sha = pict.Bytes is { } bytes && _displayBytesSha.TryGetValue(Sha256Hex(bytes), out var byBytes)
                ? byBytes
                : pict.Sha256;
            resolved.Add(sha);
        }

        var content = TomDocumentWalker.Walk(_box, resolved);

        // Self-heal the double-click map with the authoritative ordinals — deletes
        // that happened since load stop shifting the mapping from here on.
        _pictShas.Clear();
        _pictShas.AddRange(resolved);

        return MarkdownEmitter.Emit(content);
    }

    /// <summary>Lowercase hex SHA256 of the given bytes (identity key for pict matching).</summary>
    private static string Sha256Hex(byte[] bytes)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// True when the document differs from the post-load baseline. Catches format-only
    /// edits (bold/colour/alignment), which the plain-text compare in the save guard
    /// cannot see. Load noise never reaches here because it refreshes the baseline.
    /// </summary>
    public bool IsRtfChangedFromBaseline() => GetRtf() != _baselineRtf;

    public void Focus() => _box.Focus(FocusState.Programmatic);

    /// <summary>Editor content width available to an image (shared by import paths).</summary>
    private double AvailableImageWidth
    {
        get
        {
            var width = _box.ActualWidth - _box.Padding.Left - _box.Padding.Right;
            return width > 0 ? width : _box.ActualWidth;
        }
    }

    // ---------- Formatting ops (the locked Markdown subset) ----------

    public void ToggleBold() => MarkInteracted(() => _box.Document.Selection.CharacterFormat.Bold = FormatEffect.Toggle);

    public void ToggleItalic() => MarkInteracted(() => _box.Document.Selection.CharacterFormat.Italic = FormatEffect.Toggle);

    public void ToggleStrikethrough() =>
        MarkInteracted(() => _box.Document.Selection.CharacterFormat.Strikethrough = FormatEffect.Toggle);

    /// <summary>Body text size in points — matches the RTF emitter's <c>\fs22</c>.</summary>
    private const float BodyFontPoints = 11f;

    /// <summary>Heading sizes in points — must stay inside the walker's classification bands
    /// (H1 ≥ 18, H2 ≥ 15, H3 ≥ 12; body 11 falls below all of them).</summary>
    private static readonly Dictionary<int, float> HeadingSizesPt = new()
    {
        [1] = 20f,
        [2] = 16f,
        [3] = 13f,
    };

    /// <summary>
    /// Applies (or removes) a heading level to every paragraph the selection touches.
    /// The range is first expanded to whole paragraphs — the TOM walker classifies a
    /// paragraph by its leading character, so partial-paragraph formatting would
    /// produce a heading whose tail still renders as body text. Applying the level the
    /// first touched paragraph already carries clears it back to body (toggle
    /// semantics, like Word's Ctrl+Alt+N styles).
    /// </summary>
    public void ApplyHeading(int level)
    {
        MarkInteracted(() =>
        {
            _box.Document.GetText(TextGetOptions.None, out var text);
            var start = _box.Document.Selection.StartPosition;
            var end = _box.Document.Selection.EndPosition;

            // Expand to whole paragraphs [paraStart, paraEnd), including the trailing
            // paragraph mark, so typing at the paragraph end inherits the new format.
            var paraStart = start;
            while (paraStart > 0 && text[paraStart - 1] != '\r')
                paraStart--;
            var paraEnd = end;
            while (paraEnd < text.Length && text[paraEnd] != '\r')
                paraEnd++;
            if (paraEnd < text.Length)
                paraEnd++; // the '\r' itself
            if (paraEnd <= paraStart)
                return;

            var headingSize = HeadingSizesPt[level];
            var clear = IsParagraphAtHeading(text, paraStart, paraEnd, headingSize);

            var format = _box.Document.GetRange(paraStart, paraEnd).CharacterFormat;
            format.Size = clear ? BodyFontPoints : headingSize;
            format.Bold = clear ? FormatEffect.Off : FormatEffect.On;
        });
    }

    /// <summary>True when the paragraph's leading character is bold at exactly the given size.</summary>
    private bool IsParagraphAtHeading(string text, int start, int end, float sizePt)
    {
        for (var i = start; i < end; i++)
        {
            var c = text[i];
            if (c == '\uFFFC' || char.IsWhiteSpace(c))
                continue;

            var format = _box.Document.GetRange(i, i + 1).CharacterFormat;
            return format.Bold is FormatEffect.On or FormatEffect.Toggle
                && Math.Abs(format.Size - sizePt) < 0.5f;
        }
        return false;
    }

    public void ToggleList(MarkerType listType) =>
        MarkInteracted(() =>
        {
            var format = _box.Document.Selection.ParagraphFormat;
            format.ListType = format.ListType == listType ? MarkerType.None : listType;
        });

    // ---------- Image insertion ----------

    /// <summary>
    /// Inserts an image from a file at the current selection: the original is stored
    /// content-addressed, the compressed copy is inlined, and the resulting image is
    /// linked to the current note. Returns true when the image was inserted (the paste
    /// path uses this to decide whether it consumed the clipboard).
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

    /// <summary>Inserts every supported image file from a drop / clipboard file list.</summary>
    public async Task InsertImageFilesAsync(IEnumerable<string> filePaths)
    {
        foreach (var path in filePaths)
            await InsertImageFromFileAsync(path);
    }

    /// <summary>
    /// Inserts clipboard images (StorageItems or a loose bitmap). Returns true when at
    /// least one image was actually inserted, so the paste handler can stop before the
    /// text fallback (a clipboard full of non-image files must NOT swallow the paste).
    /// </summary>
    public async Task<bool> InsertClipboardImagesAsync()
    {
        var sources = await ClipboardImageReader.ReadAsync(_log);
        if (sources.Count == 0)
            return false;

        var inserted = false;
        foreach (var source in sources)
        {
            if (source.FilePath is { } path)
                inserted |= await InsertImageFromFileAsync(path);
            else if (source.Bytes is { } bytes)
                inserted |= await InsertImageBytesAsync(bytes, source.Extension);
        }
        return inserted;
    }

    private async Task<bool> InsertImageBytesAsync(byte[] bytes, string extension)
    {
        if (_images is null)
        {
            _log?.LogWarning("No IImageService wired; image insert skipped");
            ImportFailed?.Invoke("图片服务不可用，无法插入图片");
            return false;
        }

        NormalizedImage normalized;
        try
        {
            normalized = await WicImageNormalizer.NormalizeAsync(bytes, extension);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Image decode failed ({Ext})", extension);
            ImportFailed?.Invoke("无法解码该图片（可能是不支持的格式）");
            return false;
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
            ImportFailed?.Invoke("保存图片失败，请查看日志");
            return false;
        }

        // Link the original to the current note now — import-time is the only moment
        // the byte size / dimensions are known without a WIC round-trip on reload.
        if (_notes is not null && CurrentNoteIdProvider?.Invoke() is { } noteId)
        {
            try
            {
                await _notes.AddNoteImagesAsync(noteId, [ToNoteImage(noteId, imported)]);
            }
            catch (Exception ex)
            {
                _log?.LogWarning(ex, "Linking image {Sha} to note {NoteId} failed", imported.Sha256, noteId);
            }
        }

        await InsertImageIntoDocumentAsync(imported, normalized);
        return true;
    }

    private static NoteImage ToNoteImage(long noteId, ImportedImage image) => new()
    {
        NoteId = noteId,
        Sha256 = image.Sha256,
        Ext = image.Ext,
        ByteSize = image.ByteSize,
        Width = image.PixelWidth,
        Height = image.PixelHeight,
        // Schema v6: the display copy lives in note_images now (not inside RTF) —
        // persisted at import time so MD loads can resolve qnote-img: references.
        DisplayBytes = image.DisplayBytes,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private async Task InsertImageIntoDocumentAsync(ImportedImage imported, NormalizedImage normalized)
    {
        var (widthDip, heightDip) = ImageDisplaySize.Fit(
            normalized.PixelWidth, normalized.PixelHeight, AvailableImageWidth);

        MarkInteracted();
        using var stream = await WicImageNormalizer.ToInMemoryStreamAsync(imported.DisplayBytes);
        var insertPosition = _box.Document.Selection.StartPosition;
        _box.Document.Selection.InsertImage(
            widthDip, heightDip, 0, VerticalCharacterAlignment.Baseline,
            ImageAltCodec.Encode(imported.Sha256), stream);
        _pictShas.Insert(CountPictPlaceholdersBefore(insertPosition), imported.Sha256);
        // Register the display-copy identity so GetMarkdown can byte-match this pict
        // even after a reload strips its alt; the row persisted above is the same map
        // entry on the next load.
        _displayBytesSha[Sha256Hex(imported.DisplayBytes)] = imported.Sha256;
        ContentChanged?.Invoke();
    }

    /// <summary>Number of embedded-image placeholders (U+FFFC) before a story position.</summary>
    private int CountPictPlaceholdersBefore(int storyPosition)
    {
        _box.Document.GetText(TextGetOptions.None, out var text);
        var end = Math.Min(storyPosition, text.Length);
        var count = 0;
        for (var i = 0; i < end; i++)
        {
            if (text[i] == '￼')
                count++;
        }
        return count;
    }

    /// <summary>
    /// The load-time <c>qnote:</c> sha for the pict at <paramref name="storyPosition"/>,
    /// matched by ordinal (U+FFFC count). <c>null</c> when the ordinal is out of range
    /// or the loaded pict never carried a link.
    /// </summary>
    private string? GetLoadedShaByPictOrdinal(int storyPosition)
    {
        var ordinal = CountPictPlaceholdersBefore(storyPosition);
        return ordinal >= 0 && ordinal < _pictShas.Count ? _pictShas[ordinal] : null;
    }

    // ---------- Double-click image hit-test ----------

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // A click is user interaction: stop treating any later TextChanged as load noise.
        MarkInteracted();

        var p = e.GetCurrentPoint(_box);

        // We cheap-probe for an image and only record it; the actual open happens on the
        // second click within the double-click window (mirrors RichEdit's own selection
        // behaviour without a Tapped event swallowing the click).
        if (p.Properties.PointerUpdateKind
            != Microsoft.UI.Input.PointerUpdateKind.LeftButtonPressed)
            return;

        if (!TryGetImageShaAt(p.Position, out var sha))
            return;

        var now = DateTime.UtcNow;

        // RichEdit's inner editing surface re-raises PointerPressed several times for ONE
        // physical press (observed ~4x; AddHandler(handledEventsToo) delivers every one).
        // Without this guard the 2nd..4th duplicate firings of a SINGLE click would fall
        // inside the double-click window and open the original repeatedly. Treat firings
        // within DuplicatePressWindow of the last processed press as the same physical
        // press, and do not let them advance the double-click state.
        if ((now - _lastPressAt) < DuplicatePressWindow)
            return;
        _lastPressAt = now;

        if (_lastImageClickSha == sha && (now - _lastImageClickAt) <= DoubleClickWindow)
        {
            _lastImageClickSha = null;
            OpenOriginal(sha!);
            e.Handled = true;
            return;
        }

        _lastImageClickSha = sha;
        _lastImageClickAt = now;
    }

    private static readonly TimeSpan DoubleClickWindow = TimeSpan.FromMilliseconds(500);

    /// <summary>Firings closer than this are the same physical press (RichEdit re-raises it).</summary>
    private static readonly TimeSpan DuplicatePressWindow = TimeSpan.FromMilliseconds(50);

    private string? _lastImageClickSha;
    private DateTime _lastImageClickAt;
    private DateTime _lastPressAt;

    /// <summary>
    /// Returns the content address of the image at a point, or <c>null</c> when the
    /// point is not on an image. Uses <c>GetRangeFromPoint</c> to collapse onto the
    /// character under the pointer, then reads that one-character range's RTF and parses
    /// the <c>qnote:&lt;sha&gt;</c> alt back out (<see cref="RtfPictInspector"/>). The alt
    /// is the reverse-lookup key because WinRT <c>ITextRange</c> exposes no way to read a
    /// pict's bytes (spike: research/richeditbox-image-spike.md).
    /// </summary>
    public bool TryGetImageShaAt(Point point, out string? sha256)
    {
        sha256 = null;
        try
        {
            var range = _box.Document.GetRangeFromPoint(point, PointOptions.ClientCoordinates);
            if (range is null)
                return false;

            // Collapse to the single character at the point and require the image
            // placeholder (U+FFFC) before reading the alt — avoids treating ordinary
            // text that happens to contain "qnote:" as an image.
            // Clamp the end: an image at the very end of the document sits at
            // (length - 1), and SetRange past the end throws a COMException, which
            // would silently miss the click.
            var end = Math.Min(range.StartPosition + 1, range.StoryLength);
            range.SetRange(range.StartPosition, end);
            range.GetText(TextGetOptions.None, out var raw);
            if (raw.Length == 0 || raw[0] != '\uFFFC')
                return false;

            // UseObjectText returns RichEdit's generic object placeholder ("Image"), NOT
            // the {\*\picprop{\sp{\sn wzDescription}...}} we wrote — so it cannot be the
            // reverse-lookup key. Read the range's RTF instead and parse the picprop out.
            range.GetText(TextGetOptions.FormatRtf, out var pictRtf);
            sha256 = RtfPictInspector.FindPicts(pictRtf)
                .Select(p => p.Sha256)
                .FirstOrDefault(s => s is not null);

            // RichEdit rewrites the qnote: alt to "Image" when a note RTF is RELOADED
            // (verified 2026-09-26, msftedit in WinAppSDK 2.3.1), so a pict inserted
            // before the last reload has no alt in the live document. Recover the sha
            // by the pict's ordinal against the links captured at load time. Residual:
            // a pict deleted since load shifts later ordinals (follow-up task).
            sha256 ??= GetLoadedShaByPictOrdinal(range.StartPosition);
            return sha256 is not null;
        }
        catch (Exception ex)
        {
            _log?.LogDebug(ex, "Image hit-test at {X},{Y} failed", point.X, point.Y);
            return false;
        }
    }

    /// <summary>Opens an original image with the system default viewer (PRD D3).</summary>
    public void OpenOriginal(string sha256)
    {
        var path = _images?.FindOriginalPath(sha256);
        if (path is null)
        {
            ImageOpenFailed?.Invoke("原图文件已丢失，无法打开");
            return;
        }
        OpenImageRequested?.Invoke(path);
    }

    // ---------- Paste (keep images) ----------

    private async void OnPaste(object sender, TextControlPasteEventArgs e)
    {
        // Handled MUST be decided SYNCHRONOUSLY, before the first await. This handler is
        // async void: awaiting yields back to RichEdit, whose own default paste then runs
        // and inserts a *second* copy of the image (at RichEdit's size, not ours). So we
        // probe the available formats up-front, take the paste for every format we know
        // how to handle, then await the actual data. RichEdit's default paste only runs
        // for a clipboard we do not own (formats we can't insert anyway).
        bool handlesImages, handlesRtf, handlesText;
        DataPackageView data;
        try
        {
            data = Clipboard.GetContent();
            // Contains() can throw too (clipboard closed between calls) — keep the whole
            // probe inside the guard, or this async-void handler would crash the app.
            handlesImages = data.Contains(StandardDataFormats.StorageItems)
                            || data.Contains(StandardDataFormats.Bitmap);
            handlesRtf = data.Contains(StandardDataFormats.Rtf);
            handlesText = data.Contains(StandardDataFormats.Text);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Clipboard read failed; paste ignored");
            return;
        }

        if (!handlesImages && !handlesRtf && !handlesText)
            return; // nothing we can consume — let the default paste try

        e.Handled = true;

        try
        {
            // Ordering matters: RTF first. A Word/browser rich-text copy puts a flattened
            // bitmap on the clipboard ALONGSIDE its RTF, so probing images first would
            // paste just a screenshot and silently drop the accompanying text. RTF carries
            // both the text and the embedded picts, so it is the richer source whenever
            // it is present. Images are only the fallback for a clipboard with no RTF —
            // a screenshot (Bitmap only) or Explorer Ctrl+C of image files (StorageItems).
            if (handlesRtf)
            {
                // Keep embedded pictures verbatim (PRD "QNote 内复制图片再粘贴不丢图").
                // No more RtfPictStripper on this path — picts are now first-class content.
                // WinRT GetRtfAsync is apartment-sensitive (RPC_E_WRONG_THREAD for some
                // producers); fall back to the raw CF_RTF read, which has no apartment.
                string? rtf;
                try
                {
                    rtf = await data.GetRtfAsync();
                }
                catch (Exception ex)
                {
                    _log?.LogWarning(ex, "Reading clipboard RTF via WinRT failed; trying Win32 fallback");
                    rtf = Win32Clipboard.ReadRtfText();
                }
                if (rtf is null)
                {
                    ImportFailed?.Invoke("无法读取剪贴板中的内容");
                    return;
                }
                MarkInteracted();
                _box.Document.Selection.SetText(TextSetOptions.FormatRtf, rtf);
                ContentChanged?.Invoke();
            }
            else if (await InsertClipboardImagesAsync())
            {
                // Screenshot / Explorer image files: no RTF to fall back on.
            }
            else if (handlesText)
            {
                // Same apartment story as RTF: WinRT GetTextAsync can fail for
                // producer-specific clipboards; CF_UNICODETEXT is always readable.
                string? text;
                try
                {
                    text = await data.GetTextAsync();
                }
                catch (Exception ex)
                {
                    _log?.LogWarning(ex, "Reading clipboard text via WinRT failed; trying Win32 fallback");
                    text = Win32Clipboard.ReadUnicodeText();
                }
                if (text is null)
                {
                    ImportFailed?.Invoke("无法读取剪贴板中的内容");
                    return;
                }
                MarkInteracted();
                _box.Document.Selection.SetText(TextSetOptions.None, text);
                ContentChanged?.Invoke();
            }
            else if (handlesImages)
            {
                // The paste was claimed synchronously for the image formats, but every
                // read path came back empty (and there is no text to fall back on).
                // Surface the failure — a silent no-op reads as "paste is broken".
                ImportFailed?.Invoke("无法读取剪贴板中的图片");
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Paste failed; clipboard content ignored");
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
            {
                Focus();
                await InsertImageFilesAsync(paths);
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Image drop failed");
        }
    }

    // ---------- TextChanged / interaction tracking ----------

    /// <summary>
    /// Runs a formatting operation and flags the document as really edited. RichEdit
    /// does not reliably raise <see cref="ContentChanged"/> for a format-only change
    /// (bold/colour/alignment leave the text identical), so this must raise it here —
    /// otherwise <c>IsDirty</c> stays false and the flush is skipped as a no-op, which
    /// is exactly the format-only-edit loss this task set out to fix.
    /// </summary>
    private void MarkInteracted(Action apply)
    {
        MarkInteracted();
        apply();
        ContentChanged?.Invoke();
    }

    /// <summary>
    /// Records that the user has acted on the document since the last load. Bumping the
    /// generation invalidates any deferred load-noise signal still queued.
    /// </summary>
    private void MarkInteracted()
    {
        _interactedSinceLoad = true;
        _loadGeneration++;
    }

    private void OnTextChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressChange > 0)
        {
            _log?.LogDebug("Suppressed programmatic TextChanged during RTF load");
            return;
        }

        // TextChanged that arrives before any real interaction is RichEdit's deferred
        // load/render normalization (not user input): refresh the baseline instead of
        // flagging the note dirty, which would otherwise save on every fresh load.
        // The generation check makes this benign refresh impossible once the user has
        // acted — otherwise a late load-noise event would erase a real early edit.
        if (!_interactedSinceLoad && _loadGeneration == _baselineGeneration)
        {
            _baselineRtf = GetRtf();
            _baselineGeneration = _loadGeneration;
            return;
        }

        ContentChanged?.Invoke();
    }
}
