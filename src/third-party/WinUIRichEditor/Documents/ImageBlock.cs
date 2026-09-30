using Microsoft.Graphics.Canvas;

namespace WinUIRichEditor.Documents;

/// <summary>A block-level image (its own line/paragraph), as opposed to a small in-line
/// <see cref="InlineImage"/>. Used for larger pictures; supports resize.</summary>
public class ImageBlock : Block
{
    /// <summary>Creates a block picture. Its top margin is <see cref="Block.AutoTopMargin"/>: one line
    /// gap below whatever precedes it, since a paragraph carries no bottom margin of its own.</summary>
    public ImageBlock()
    {
        MarginTop = AutoTopMargin;
    }

    /// <summary>Original encoded image bytes (JPEG/PNG/...). When present this is the data source
    /// of truth: serialization stores these bytes verbatim (no re-encoding) and <see cref="Image"/>
    /// is decoded from them by the render layer (which owns a Win2D device).</summary>
    public byte[]? RawBytes { get; private set; }

    /// <summary>MIME type of <see cref="RawBytes"/> (e.g. "image/jpeg").</summary>
    public string? MimeType { get; private set; }

    /// <summary>Decoded GPU bitmap (render cache). Unlike Avalonia's device-independent <c>Bitmap</c>,
    /// a Win2D <see cref="CanvasBitmap"/> is bound to a device and decoded asynchronously, so it is
    /// populated by the rendering layer from <see cref="RawBytes"/> rather than lazily in this getter.
    /// Setting a bitmap directly discards the raw bytes — serialization then falls back to PNG-encoding it.</summary>
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

    /// <summary>Display width in device-independent pixels. <see cref="double.NaN"/> = natural size.</summary>
    public double Width { get; set; } = double.NaN;
    /// <summary>Display height in device-independent pixels. <see cref="double.NaN"/> = natural size.</summary>
    public double Height { get; set; } = double.NaN;
    /// <summary>Accessibility description of the image (HTML <c>alt</c>). Null = none.</summary>
    public string? AltText { get; set; }

    /// <inheritdoc/>
    public override TextElement Clone()
    {
        // RawBytes and the decoded bitmap are immutable as used here, so clones (undo snapshots)
        // share the references — no extra memory per checkpoint.
        var c = new ImageBlock
        {
            Width = this.Width,
            Height = this.Height,
            AltText = this.AltText,
            Indent = this.Indent,
            MarginTop = this.MarginTop,
            MarginBottom = this.MarginBottom
        };
        c.RawBytes = RawBytes;
        c.MimeType = MimeType;
        c._cachedBitmap = _cachedBitmap;
        return c;
    }
}
