using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using WinUIRichEditor.Documents;

namespace WinUIRichEditor.Controls;

// Source-compatibility shims for the original AvaloniaRichEditor 0.8.0 public API. The port's own,
// more descriptive names remain the primary surface (SetRunFontFamily, InsertImageBlock, PasteAsync);
// these thin wrappers let code written against the original compile unchanged. The aliases are hidden
// from IntelliSense with [EditorBrowsable(Never)] so they don't clutter the primary API.
public partial class RichEditor
{
    /// <summary>Alias of <see cref="SetRunFontFamily"/> (original AvaloniaRichEditor name).</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public void SetFontFamily(string family) => SetRunFontFamily(family);

    /// <summary>Alias of <see cref="InsertImageBlock(byte[], string?)"/> (original AvaloniaRichEditor name).</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public void InsertImageBytes(byte[] bytes) => InsertImageBlock(bytes);

    /// <summary>Alias of <see cref="PasteAsync"/> (original AvaloniaRichEditor name).</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public Task PasteFromClipboardAsync() => PasteAsync();

    /// <summary>Focuses the editor and moves the caret to the end of the document
    /// (original AvaloniaRichEditor convenience API).</summary>
    public void FocusDocumentEnd()
    {
        if (Document == null) return;
        FocusEditor();
        GoToDocEdge(start: false, shift: false); // scrolls, restarts blink, syncs IME, raises status
    }

    /// <summary>Gives keyboard focus back to the editing surface without moving the caret. Focus lives on
    /// the inner canvas (the control itself is not a tab stop), so a host or the toolbar cannot simply
    /// call <c>Focus()</c> on this control. The caret is only painted while the canvas is focused, so
    /// anything that takes focus away — a toolbar button, a picker popup — must hand it back through
    /// here or the caret vanishes and the next keystroke goes elsewhere.</summary>
    public bool FocusEditor() => _canvas?.Focus(Microsoft.UI.Xaml.FocusState.Programmatic) ?? false;

    /// <summary>Inserts an inline table (flows within a text line, like an inline image) of the given
    /// size at the caret. The port otherwise reaches this via <see cref="InsertTable"/> + the right-click
    /// "treat as character" toggle; this restores the original's one-call command.</summary>
    public void InsertInlineTable(int rows, int cols)
    {
        if (Document == null || IsReadOnly || !AllowTables || rows < 1 || cols < 1) return;
        if (_caret.Paragraph is not { } p) return;

        PushUndo(null);
        var tb = new TableBlock(rows, cols);
        // Modest inline width so the table sits inside the text line rather than spanning the page.
        double avail = _layoutWidth > 120 ? Math.Min(_layoutWidth - 40, 400) : 300;
        double w = Math.Max(30, avail / cols);
        for (int c = 0; c < tb.ColumnWidths.Count; c++) tb.ColumnWidths[c] = w;

        var it = new InlineTable { Table = tb, Parent = p };
        int splitIdx = SplitInlinesAt(p, _caret.Offset);
        p.Inlines.Insert(splitIdx, it);
        _caret = new TextPointer(p, _caret.Offset + 1); // inline object = one offset
        CollapseSelectionToCaret();
        UpdateParents(Document);
        AfterEdit();
    }

    /// <summary>Opens a file picker and inserts the chosen image as a block image. Unpackaged WinUI file
    /// pickers require the owning window handle, which a control in the tree can't reach on its own — so
    /// this convenience overload takes it explicitly. (The primary path is the host-supplied
    /// <see cref="ImageReplacePicker"/> / toolbar <c>ImagePicker</c> callback.)</summary>
    public async Task InsertImageFromFileAsync(nint windowHandle)
    {
        if (Document == null || IsReadOnly || !AllowImages || windowHandle == 0) return;
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp" }) picker.FileTypeFilter.Add(ext);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);
        var file = await picker.PickSingleFileAsync();
        if (file == null) return;

        var buffer = await Windows.Storage.FileIO.ReadBufferAsync(file);
        byte[] bytes = new byte[buffer.Length];
        using (var reader = DataReader.FromBuffer(buffer)) reader.ReadBytes(bytes);
        if (bytes.Length > 0) InsertImageBlock(bytes);
    }
}
