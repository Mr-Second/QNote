using Microsoft.Extensions.Logging;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using QNote.Models;
using QNote.Services;
using QNote.Text;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
using Windows.UI;

namespace QNote.Controls;

/// <summary>Snapshot of the current selection's formatting, for toolbar state sync.
/// <c>null</c> means "mixed / unknown" (three-state UI).</summary>
public sealed record EditorFormatState
{
    public bool? Bold { get; init; }
    public bool? Italic { get; init; }
    public bool? Underline { get; init; }
    public bool? Strikethrough { get; init; }
    public string? FontFamily { get; init; }
    public int? FontSizePx { get; init; }
    public Color? ForegroundColor { get; init; }
    public ParagraphAlignment? Alignment { get; init; }
    public bool? BulletedList { get; init; }
    public bool? NumberedList { get; init; }
}

/// <summary>
/// View-side wrapper around a <see cref="RichEditBox"/> (NOT a view-model — it holds
/// the control, so it lives in code-behind wiring per mvvm-guidelines). Owns RTF in/
/// out, all formatting ops, selection-state reads, paste/drag image import, and the
/// double-click "open original" hit-test.
///
/// Image model (PRD D1/D2): the inline copy is a compressed PNG/JPEG inside the RTF;
/// the original is content-addressed on disk by <see cref="IImageService"/> and linked
/// to the note via the <c>qnote:&lt;sha256&gt;</c> alt text. The controller keeps an RTF
/// baseline captured right after a load so the save guard can tell a real edit
/// (including a format-only one) from RichEdit's normalization noise.
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

    /// <summary>RTF as RichEdit re-emits it right after a load — the no-op baseline.</summary>
    private string _baselineRtf = string.Empty;

    /// <summary>
    /// Ordered <c>qnote:</c> shas of the picts as loaded (or inserted since), used as
    /// the double-click fallback when RichEdit strips the alt on an RTF reload: the
    /// in-memory pict then reports the generic "Image" alt and the sha can only be
    /// recovered by the pict's ordinal in the document. Known residual: deleting a
    /// pict since load shifts later ordinals — the alt-preservation follow-up
    /// replaces this heuristic with a content-keyed mapping.
    /// </summary>
    private readonly List<string?> _pictShas = [];

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
    /// (logged, never crashes); empty content → blank document.</summary>
    public void SetRtf(string? rtf)
    {
        _interactedSinceLoad = false;
        _loadGeneration++;
        _suppressChange++;
        // Capture the ordered image links BEFORE RichEdit can normalize them away —
        // the loaded RTF still carries the qnote: alts written at save time.
        _pictShas.Clear();
        _pictShas.AddRange(RtfPictInspector.FindPicts(rtf).Select(p => p.Sha256));
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

    // ---------- Formatting ops ----------

    public void ToggleBold() => MarkInteracted(() => _box.Document.Selection.CharacterFormat.Bold = FormatEffect.Toggle);

    public void ToggleItalic() => MarkInteracted(() => _box.Document.Selection.CharacterFormat.Italic = FormatEffect.Toggle);

    public void ToggleStrikethrough() =>
        MarkInteracted(() => _box.Document.Selection.CharacterFormat.Strikethrough = FormatEffect.Toggle);

    public void ToggleUnderline()
    {
        MarkInteracted(() =>
        {
            var format = _box.Document.Selection.CharacterFormat;
            format.Underline = format.Underline == UnderlineType.Single
                ? UnderlineType.None
                : UnderlineType.Single;
        });
    }

    public void SetFontFamily(string family) =>
        MarkInteracted(() => _box.Document.Selection.CharacterFormat.Name = family);

    public void SetFontSizePx(int px) =>
        MarkInteracted(() => _box.Document.Selection.CharacterFormat.Size = RtfFontSize.PxToPt(px));

    public void SetForegroundColor(Color color) =>
        MarkInteracted(() => _box.Document.Selection.CharacterFormat.ForegroundColor = color);

    public void SetAlignment(ParagraphAlignment alignment) =>
        MarkInteracted(() => _box.Document.Selection.ParagraphFormat.Alignment = alignment);

    public void ToggleList(MarkerType listType) =>
        MarkInteracted(() =>
        {
            var format = _box.Document.Selection.ParagraphFormat;
            format.ListType = format.ListType == listType ? MarkerType.None : listType;
        });

    // ---------- Selection state ----------

    public EditorFormatState GetSelectionState()
    {
        var character = _box.Document.Selection.CharacterFormat;
        var paragraph = _box.Document.Selection.ParagraphFormat;

        return new EditorFormatState
        {
            Bold = FromEffect(character.Bold),
            Italic = FromEffect(character.Italic),
            Strikethrough = FromEffect(character.Strikethrough),
            Underline = character.Underline switch
            {
                UnderlineType.Single => true,
                UnderlineType.None => false,
                _ => null,
            },
            FontFamily = string.IsNullOrEmpty(character.Name) ? null : character.Name,
            FontSizePx = character.Size > 0 ? RtfFontSize.PtToPx(character.Size) : null,
            ForegroundColor = character.ForegroundColor,
            Alignment = paragraph.Alignment == ParagraphAlignment.Undefined ? null : paragraph.Alignment,
            BulletedList = FromListType(paragraph.ListType, MarkerType.Bullet),
            // "Arabic" is the decimal-numbered list marker (1. 2. 3. …).
            NumberedList = FromListType(paragraph.ListType, MarkerType.Arabic),
        };
    }

    private static bool? FromEffect(FormatEffect effect) => effect switch
    {
        FormatEffect.On => true,
        FormatEffect.Off => false,
        _ => null,
    };

    private static bool? FromListType(MarkerType actual, MarkerType wanted) => actual switch
    {
        _ when actual == wanted => true,
        MarkerType.None => false,
        _ => null,
    };

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
