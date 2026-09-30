using System.Linq;
using System.Threading.Tasks;
using WinUIRichEditor.Documents;
using WinUIRichEditor.Formatters;

using Microsoft.UI.Xaml;

namespace WinUIRichEditor.Controls;

// Document-level I/O API: HTML/RTF/JSON/.flow load-save (sync + async snapshot variants), Clear,
// plain-text extraction and undo availability. Mirrors the Avalonia original's RichEditor.DocumentApi.
// Unlike Avalonia, the model's colors are value types (Windows.UI.Color), so serialization is
// thread-safe over a cloned snapshot — the async variants Clone on the UI thread, then encode in the
// background; parsing builds the model on the UI thread (CanvasBitmap decode is deferred to render).
public partial class RichEditor
{
    /// <summary>Identifies the <see cref="AllowLocalFileImages"/> dependency property.</summary>
    public static readonly DependencyProperty AllowLocalFileImagesProperty = DependencyProperty.Register(
        nameof(AllowLocalFileImages), typeof(bool), typeof(RichEditor), new PropertyMetadata(true));

    /// <summary>When true, <c>file://</c> and local-path images are loaded when parsing HTML
    /// via <see cref="LoadHtml"/>/<see cref="InsertHtml"/>. When false, none are — files under the temp
    /// directory included (only clipboard paste reads those, for Word/HWP pictures). Default true.</summary>
    public bool AllowLocalFileImages
    {
        get => (bool)GetValue(AllowLocalFileImagesProperty);
        set => SetValue(AllowLocalFileImagesProperty, value);
    }

    /// <summary>Serializes the document to HTML.</summary>
    public string ToHtml() => Document != null ? HtmlDocumentFormatter.ToHtml(Document) : "";

    /// <summary>Replaces the document with one parsed from HTML (empty document if null/empty).</summary>
    public void LoadHtml(string? html)
        => LoadDocument(string.IsNullOrEmpty(html)
            ? new FlowDocument()
            : HtmlDocumentFormatter.ParseHtml(html, AllowLocalFileImages, AllowRemoteImagesOnPaste, allowTempFileImages: false));

    /// <summary>Replaces the document with one parsed from HTML, downloading remote (<c>http</c>)
    /// images off the UI thread first so a slow network can't freeze the UI. Await from the UI thread.</summary>
    public async Task LoadHtmlAsync(string? html)
        => LoadDocument(string.IsNullOrEmpty(html)
            ? new FlowDocument()
            : await HtmlDocumentFormatter.ParseHtmlAsync(html, AllowLocalFileImages, AllowRemoteImagesOnPaste, allowTempFileImages: false));

    /// <summary>Serializes the document to RTF (Rich Text Format) — readable by Word, WordPad, LibreOffice,
    /// and HWP.</summary>
    public string ToRtf() => Document != null ? RtfDocumentFormatter.Write(Document) : "";

    /// <summary>Replaces the document with one parsed from RTF (empty document if null/empty or not RTF).
    /// <para>DAMAGED RTF leaves the open document ALONE — the same reasoning as <see cref="LoadJson"/>,
    /// which reports rather than blanking: replacing open content with an empty document means the next
    /// save writes that blank over the original file. Unlike <c>LoadJson</c> this stays silent rather
    /// than throwing, because <c>LoadRtf</c> shipped in 1.0 as <c>void</c> and a new exception out of it
    /// would kill hosts that have no handler. Hosts that need the failure — a file-open path, which must
    /// tell the user why nothing happened — should call
    /// <see cref="RtfDocumentFormatter.TryParse"/> and load the result themselves.</para></summary>
    public void LoadRtf(string? rtf)
    {
        if (string.IsNullOrEmpty(rtf) || !RtfDocumentFormatter.LooksLikeRtf(rtf))
        {
            LoadDocument(new FlowDocument());
            return;
        }
        // Keep the open document on failure — but only when there IS one. With no document loaded there
        // is nothing to protect, and bailing would leave the editor inert (null Document, no caret), so
        // an empty document is the better landing spot.
        if (RtfDocumentFormatter.TryParse(rtf, out var parsed, out _)) LoadDocument(parsed);
        else if (Document == null) LoadDocument(new FlowDocument());
    }

    /// <summary>Serializes the document to the library's JSON format.</summary>
    public string ToJson() => Document != null ? DocumentSerializer.Serialize(Document) : "";

    /// <summary>Replaces the document with one loaded from the library's JSON format.</summary>
    /// <exception cref="System.Text.Json.JsonException"><paramref name="json"/> is not valid JSON — or is
    /// valid JSON that is not a document of this library (an object with no <c>Blocks</c>, such as another
    /// application's settings file). Either way the input is reported rather than read as an empty
    /// document, so the open document is left alone instead of being replaced by a blank one the next
    /// save would write over the original. A literal <c>null</c> reads as an empty document, as
    /// <see cref="DocumentSerializer.Deserialize"/> documents.</exception>
    public void LoadJson(string json) => LoadDocument(DocumentSerializer.DeserializeDocument(json));

    /// <summary>Serializes the document to JSON on a background thread, keeping the UI responsive for
    /// large documents. A clone is taken on the calling (UI) thread so concurrent edits can't tear the
    /// output; only JSON encoding runs in the background. Call from the UI thread.</summary>
    public Task<string> ToJsonAsync()
    {
        if (Document == null) return Task.FromResult("");
        var snapshot = Document.Clone();
        return Task.Run(() => DocumentSerializer.Serialize(snapshot));
    }

    /// <summary>Parses JSON into a document on a background thread, then swaps it in. Model objects are
    /// built in the background (value-type colors are thread-safe; image decode is deferred to first
    /// render), so only the document swap touches the UI. Call (and await) from the UI thread.</summary>
    /// <exception cref="System.Text.Json.JsonException"><paramref name="json"/> is not valid JSON or not a
    /// document of this library (see <see cref="LoadJson"/>). The open document is left alone.</exception>
    public async Task LoadJsonAsync(string json)
        => LoadDocument(await Task.Run(() => DocumentSerializer.DeserializeDocument(json)));

    /// <summary>Writes the document to <paramref name="destination"/> as a <c>.flow</c> package
    /// (ZIP: document.json + raw image entries, no base64 overhead) on a background thread. A clone is
    /// taken on the calling thread; only the zip writing runs in the background.</summary>
    public Task SavePackageAsync(System.IO.Stream destination)
    {
        var snapshot = (Document ?? new FlowDocument()).Clone();
        return Task.Run(() => DocumentPackage.Save(snapshot, destination));
    }

    /// <summary>Reads a <c>.flow</c> package from <paramref name="source"/> on a background thread, then
    /// swaps the document in. Call (and await) from the UI thread.</summary>
    /// <exception cref="System.IO.InvalidDataException"><paramref name="source"/> is not a readable
    /// <c>.flow</c> package: not a zip, damaged, or a zip with no <c>document.json</c> (a .docx, say).</exception>
    /// <exception cref="System.Text.Json.JsonException">The package's <c>document.json</c> is not valid
    /// JSON, or not a document of this library. As with <see cref="LoadJson"/>, such input is reported
    /// rather than read as an empty document; the open document is left alone.</exception>
    public async Task LoadPackageAsync(System.IO.Stream source)
        => LoadDocument(await Task.Run(() =>
        {
            var (dto, pool) = DocumentPackage.ReadPackage(source);
            DocumentSerializer.EnsureDocumentShape(dto);
            return DocumentSerializer.FromDto(dto, pool);
        }));

    /// <summary>Clears the document to a single empty paragraph.</summary>
    public void Clear() => LoadDocument(new FlowDocument());

    /// <summary>The document's text content as plain text (paragraphs/cells separated by the platform
    /// newline — CRLF on Windows — so the result shows real line breaks when written to a file or pasted
    /// into a native text control; soft '\n' breaks inside a paragraph are normalized too).</summary>
    public string GetPlainText()
    {
        if (Document == null) return "";
        var sb = new System.Text.StringBuilder();
        bool first = true;
        // Every paragraph in document order at ANY depth. The walk used to descend exactly one level
        // (top-level paragraphs + a cell's own paragraphs), so text inside a nested table or an inline
        // table was dropped entirely — including from the accessibility peer, which reads this.
        foreach (var p in AllParagraphs())
        {
            if (!first) sb.Append('\n');
            first = false;
            sb.Append(BuildPlain(p));
        }
        return sb.ToString().ReplaceLineEndings();
    }

    /// <summary>True if there is an edit to undo.</summary>
    public bool CanUndo => _undo.CanUndo;

    /// <summary>True if there is an undone edit to redo.</summary>
    public bool CanRedo => _undo.CanRedo;

    /// <summary>Parses an HTML fragment and inserts the resulting blocks at the caret position.</summary>
    public void InsertHtml(string html)
    {
        if (Document == null || IsReadOnly || string.IsNullOrEmpty(html)) return;
        // Inserting is subject to AllowImages/AllowTables (see AdaptToCapabilities); LoadHtml, which opens
        // a document rather than inserting into one, is not.
        var parsed = AdaptToCapabilities(
            HtmlDocumentFormatter.ParseHtml(html, AllowLocalFileImages, AllowRemoteImagesOnPaste, allowTempFileImages: false),
            out bool emptied);
        if (parsed.Blocks.Count == 0 || emptied) return;
        PushUndo(null);
        InsertDocumentAtCaret(parsed);
        AfterEdit();
    }

    // Swaps in a new document and resets caret/selection/undo to a clean state. Shared by Load*/Clear.
    // Setting the Document DP fires OnDocumentChanged → OnDocumentAssigned (wires parents, normalizes
    // blocks, resets caret to the first paragraph) and RelayoutToViewport.
    //
    // internal, not private, so the toolbar's file-open path can load an already-parsed document: it has
    // to parse first to report WHY a damaged file was rejected, and assigning the Document DP directly
    // would skip the undo reset and leave the previous file's history undoable into the new one.
    internal void LoadDocument(FlowDocument doc)
    {
        _loadingDocument = true; // no "modified" report for the swap — MarkSaved below clears it (see ReportModified)
        try { Document = doc; }
        finally { _loadingDocument = false; }
        _undo.Clear();
        _coalesceKey = null;
        MarkSaved(); // freshly loaded content is the baseline, not a pending modification
        InvalidateCanvas();
    }
}
