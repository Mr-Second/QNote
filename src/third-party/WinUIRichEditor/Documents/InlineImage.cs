using Microsoft.Graphics.Canvas;

namespace WinUIRichEditor.Documents;

/// <summary>An image that flows inline within a paragraph's text (e.g. a small icon or logo).
/// Occupies exactly one logical character position (object-replacement character U+FFFC).
/// For large block-level pictures use <see cref="ImageBlock"/> instead.</summary>
public class InlineImage : Inline
{
    /// <summary>Original encoded image bytes (JPEG/PNG/...). When present this is the data source
    /// of truth: serialization stores these bytes verbatim (no re-encoding) and <see cref="Image"/>
    /// is decoded from them by the render layer (which owns a Win2D device).</summary>
    public byte[]? RawBytes { get; private set; }

    /// <summary>MIME type of <see cref="RawBytes"/> (e.g. "image/jpeg").</summary>
    public string? MimeType { get; private set; }

    /// <summary>Decoded GPU bitmap (render cache). Populated by the rendering layer from
    /// <see cref="RawBytes"/> (a Win2D <see cref="CanvasBitmap"/> is device-bound and decoded
    /// asynchronously). Setting a bitmap directly discards the raw bytes.</summary>
    public CanvasBitmap? Image
    {
        get => _cachedBitmap;
        set { _cachedBitmap = value; RawBytes = null; MimeType = null; }
    }
    private CanvasBitmap? _cachedBitmap;

    /// <summary>Sets the image from its original encoded bytes. Pass <paramref name="decoded"/>
    /// when a bitmap is already in hand to seed the render cache and avoid a second decode.</summary>
    public void SetImageData(byte[] bytes, string? mimeType, CanvasBitmap? decoded = null)
    {
        RawBytes = bytes;
        MimeType = mimeType ?? "image/png";
        _cachedBitmap = decoded;
    }

    /// <summary>Display width in device-independent pixels. Default: 16.</summary>
    public double Width { get; set; } = 16;
    /// <summary>Display height in device-independent pixels. Default: 16.</summary>
    public double Height { get; set; } = 16;
    /// <summary>Accessibility description of the image (HTML <c>alt</c>). Null = none.</summary>
    public string? AltText { get; set; }

    /// <inheritdoc/>
    public override TextElement Clone()
    {
        // Shares RawBytes/bitmap references — see ImageBlock.Clone.
        var c = new InlineImage
        {
            Width = this.Width,
            Height = this.Height,
            AltText = this.AltText
        };
        c.RawBytes = RawBytes;
        c.MimeType = MimeType;
        c._cachedBitmap = _cachedBitmap;
        return c;
    }
}
