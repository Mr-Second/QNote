using Microsoft.Extensions.Logging;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using QNote.Text;
using Windows.ApplicationModel.DataTransfer;
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
/// out, all formatting ops, selection-state reads, and paste-with-image-stripping.
/// </summary>
public sealed class RichTextEditorController
{
    private readonly RichEditBox _box;
    private readonly ILogger<RichTextEditorController>? _log;
    private int _suppressChange;

    public RichTextEditorController(RichEditBox box, ILogger<RichTextEditorController>? log = null)
    {
        _box = box;
        _log = log;
        _box.TextChanged += OnTextChanged;
        _box.Paste += OnPaste;
        _box.SelectionChanged += (_, _) => SelectionChanged?.Invoke();
    }

    /// <summary>Raised on user edits (suppressed during programmatic <see cref="SetRtf"/>).</summary>
    public event Action? ContentChanged;

    /// <summary>Raised when the selection (and thus its formatting) may have changed.</summary>
    public event Action? SelectionChanged;

    // ---------- RTF in/out ----------

    /// <summary>Loads RTF into the document. Invalid RTF falls back to a blank document
    /// (logged, never crashes); empty content → blank document.</summary>
    public void SetRtf(string? rtf)
    {
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
            // RichEditBox also raises TextChanged for programmatic SetText — and can
            // raise it again asynchronously when the box first renders / applies its
            // default formatting. A synchronous using-scope expires before those
            // deferred events fire, so release suppression on the dispatcher instead:
            // anything queued by the load is still ignored, user input comes after.
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

    public void Focus() => _box.Focus(FocusState.Programmatic);

    // ---------- Formatting ops ----------

    public void ToggleBold() => _box.Document.Selection.CharacterFormat.Bold = FormatEffect.Toggle;

    public void ToggleItalic() => _box.Document.Selection.CharacterFormat.Italic = FormatEffect.Toggle;

    public void ToggleStrikethrough() =>
        _box.Document.Selection.CharacterFormat.Strikethrough = FormatEffect.Toggle;

    public void ToggleUnderline()
    {
        var format = _box.Document.Selection.CharacterFormat;
        format.Underline = format.Underline == UnderlineType.Single
            ? UnderlineType.None
            : UnderlineType.Single;
    }

    public void SetFontFamily(string family) => _box.Document.Selection.CharacterFormat.Name = family;

    public void SetFontSizePx(int px) => _box.Document.Selection.CharacterFormat.Size = RtfFontSize.PxToPt(px);

    public void SetForegroundColor(Color color) =>
        _box.Document.Selection.CharacterFormat.ForegroundColor = color;

    public void SetAlignment(ParagraphAlignment alignment) =>
        _box.Document.Selection.ParagraphFormat.Alignment = alignment;

    public void ToggleList(MarkerType listType)
    {
        var format = _box.Document.Selection.ParagraphFormat;
        format.ListType = format.ListType == listType ? MarkerType.None : listType;
    }

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

    // ---------- Paste (strip images) ----------

    private async void OnPaste(object sender, TextControlPasteEventArgs e)
    {
        e.Handled = true;
        try
        {
            var data = Clipboard.GetContent();
            var selection = _box.Document.Selection;
            if (data.Contains(StandardDataFormats.Rtf))
            {
                var rtf = RtfPictStripper.StripPictGroups(await data.GetRtfAsync());
                selection.SetText(TextSetOptions.FormatRtf, rtf);
            }
            else if (data.Contains(StandardDataFormats.Text))
            {
                selection.SetText(TextSetOptions.None, await data.GetTextAsync());
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Paste failed; clipboard content ignored");
        }
    }

    private void OnTextChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressChange > 0)
        {
            _log?.LogDebug("Suppressed programmatic TextChanged during RTF load");
            return;
        }
        ContentChanged?.Invoke();
    }
}
