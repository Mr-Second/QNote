using Windows.UI;
using Windows.UI.Text;
using Microsoft.UI.Text;

namespace WinUIRichEditor.Documents;

/// <summary>A contiguous run of text sharing one set of character formatting (font, size, weight,
/// style, color, highlight, decorations, optional hyperlink). The basic inline building block.</summary>
public class Run : Inline
{
    private string? _text;
    private long? _textHash; // lazily computed FNV-1a of _text; invalidated by the Text setter

    /// <summary>The text content of this run.</summary>
    public string? Text { get => _text; set { _text = value; _textHash = null; } }

    /// <summary>FNV-1a hash of <see cref="Text"/>, cached per run. Strings are immutable, so every text
    /// change must go through the <see cref="Text"/> setter — content cannot change behind this cache.
    /// Lets the editor's paragraph signature (its layout/height/pagination cache key, recomputed for
    /// every paragraph on each relayout) cost O(runs) instead of re-hashing the whole document's
    /// characters per keystroke.</summary>
    internal long TextHash => _textHash ??= ComputeTextHash(_text);

    private static long ComputeTextHash(string? s)
    {
        if (s == null) return -1;
        unchecked
        {
            long h = 1469598103934665603; // FNV-1a 64-bit offset basis
            foreach (char ch in s) h = (h ^ ch) * 1099511628211;
            return h;
        }
    }
    /// <summary>Font weight (e.g. <see cref="FontWeights.Bold"/>). Default: Normal (400). Built as a plain
    /// struct value (not <c>FontWeights.Normal</c>) so the model constructs without activating the WinUI
    /// runtime — keeps it headless-test friendly and avoids a WinRT factory call per run.</summary>
    public FontWeight FontWeight { get; set; } = new() { Weight = 400 };
    /// <summary>Font style (e.g. <see cref="FontStyle.Italic"/>). Default: Normal.</summary>
    public FontStyle FontStyle { get; set; } = FontStyle.Normal;
    /// <summary>Foreground text color. <see langword="null"/> falls back to the editor default.</summary>
    public Color? Foreground { get; set; }
    /// <summary>Highlight (background) color. <see langword="null"/> = none.</summary>
    public Color? Background { get; set; }
    /// <summary>Font family name. <see langword="null"/> falls back to <see cref="Controls.RichEditor.DefaultFontFamily"/>.</summary>
    public string? FontFamily { get; set; }
    /// <summary>Font size in points (pt). Default: 10. Converted to device-independent pixels
    /// (×4/3 at 96 DPI) only at the render boundary; the model, public API and serialization speak pt.</summary>
    public double FontSize { get; set; } = 10;
    /// <summary>Hyperlink URL. When non-null the run is rendered as a blue underlined link.</summary>
    public string? NavigateUri { get; set; }
    /// <summary>Text decorations such as underline or strikethrough. Default: <see cref="TextDecorationFlags.None"/>.</summary>
    public TextDecorationFlags TextDecorations { get; set; } = TextDecorationFlags.None;

    /// <inheritdoc/>
    public override TextElement Clone()
    {
        var clone = new Run
        {
            FontWeight = this.FontWeight,
            FontStyle = this.FontStyle,
            Foreground = this.Foreground,
            Background = this.Background,
            FontFamily = this.FontFamily,
            FontSize = this.FontSize,
            NavigateUri = this.NavigateUri,
            // A flags enum is a value type, so this is a plain copy — no shared-reference aliasing
            // (Avalonia's TextDecorationCollection had to be copied to avoid mutating split tails).
            TextDecorations = this.TextDecorations
        };
        // Copy the backing fields directly so undo snapshots (a deep clone per edit group) share the
        // already-computed text hash instead of re-hashing on their first paragraph-signature read.
        clone._text = _text;
        clone._textHash = _textHash;
        return clone;
    }
}
