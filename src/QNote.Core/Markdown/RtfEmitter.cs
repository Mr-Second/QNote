using System.Text;

namespace QNote.Markdown;

/// <summary>
/// Inline image payload the RTF emitter needs to embed a <c>qnote-img:</c> reference:
/// the compressed display bytes plus display geometry. Resolved by the caller (wiring
/// layer) from persistence; the emitter itself never touches files.
/// </summary>
/// <param name="Blip">RTF blip keyword — <c>pngblip</c> or <c>jpegblip</c>.</param>
public sealed record RtfImagePayload(
    byte[] Bytes,
    int PixelWidth,
    int PixelHeight,
    double DisplayWidthDip,
    double DisplayHeightDip,
    string Blip)
{
    public const string PngBlip = "pngblip";
    public const string JpegBlip = "jpegblip";

    /// <summary>
    /// Sniffs the blip kind and pixel dimensions straight from the display bytes
    /// (PNG IHDR / JPEG SOF header parse — no full decode; the bytes are already
    /// the policy-compressed display copy). Display size = pixels at 96 DPI, which
    /// is how RichEdit renders picts inserted at natural size. Returns null for
    /// bytes that are neither PNG nor JPEG — the caller degrades to the alt text.
    /// </summary>
    public static RtfImagePayload? FromBytes(byte[] bytes)
    {
        if (TrySniffPng(bytes, out var pw, out var ph))
            return new RtfImagePayload(bytes, pw, ph, pw, ph, PngBlip);
        if (TrySniffJpeg(bytes, out pw, out ph))
            return new RtfImagePayload(bytes, pw, ph, pw, ph, JpegBlip);
        return null;
    }

    private static bool TrySniffPng(byte[] bytes, out int width, out int height)
    {
        // 89 50 4E 47 0D 0A 1A 0A | IHDR chunk: len(4) "IHDR"(4) width(4 BE) height(4 BE)
        width = height = 0;
        if (bytes.Length < 24 ||
            bytes[0] != 0x89 || bytes[1] != 0x50 || bytes[2] != 0x4E || bytes[3] != 0x47 ||
            bytes[4] != 0x0D || bytes[5] != 0x0A || bytes[6] != 0x1A || bytes[7] != 0x0A)
            return false;

        width = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
        height = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
        return width > 0 && height > 0;
    }

    private static bool TrySniffJpeg(byte[] bytes, out int width, out int height)
    {
        // FF D8, then marker segments; SOF0–SOF15 (C0–CF, minus C4/C8/CC) carry
        // precision(1) height(2 BE) width(2 BE) right after their length field.
        width = height = 0;
        if (bytes.Length < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8)
            return false;

        var i = 2;
        while (i + 9 <= bytes.Length)
        {
            if (bytes[i] != 0xFF)
                return false;
            var marker = bytes[i + 1];

            // Padding FF bytes before a marker are legal — skip them.
            if (marker == 0xFF)
            {
                i++;
                continue;
            }

            // Standalone markers (RSTn, TEM, SOI/EOI) carry no length field.
            if (marker is (>= 0xD0 and <= 0xD9) or 0x01)
            {
                i += 2;
                continue;
            }

            if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC))
            {
                height = (bytes[i + 5] << 8) | bytes[i + 6];
                width = (bytes[i + 7] << 8) | bytes[i + 8];
                return width > 0 && height > 0;
            }

            // Skip this segment. The 2-byte length INCLUDES itself but NOT the
            // marker, so the next marker sits 2 + segmentLength past the FF.
            var segmentLength = (bytes[i + 2] << 8) | bytes[i + 3];
            if (segmentLength < 2)
                return false;
            i += 2 + segmentLength;
        }

        return false;
    }
}

/// <summary>
/// Emits display RTF from the neutral <see cref="DocumentContent"/> model — the load
/// direction. RichEdit is the consumer: the output targets msftedit's documented
/// subset (plain character/paragraph formatting, legacy <c>\pn</c> lists, PNG picts).
/// The format subset is locked to the Markdown model, so no color/font/size controls
/// beyond heading sizes are emitted.
/// </summary>
public static class RtfEmitter
{
    /// <summary>Normal body font size in half-points (11pt), matching the editor default.</summary>
    private const int BodyFontHalfPoints = 22;

    private static readonly Dictionary<HeadingLevel, int> HeadingFontHalfPoints = new()
    {
        [HeadingLevel.H1] = 40, // 20pt
        [HeadingLevel.H2] = 32, // 16pt
        [HeadingLevel.H3] = 26, // 13pt
    };

    /// <summary>Resolves an image sha to its inline payload; null = skip the picture.</summary>
    public delegate RtfImagePayload? ImageProvider(string sha256);

    public static string Emit(DocumentContent content, ImageProvider? imageProvider = null)
    {
        var buffer = new StringBuilder(512);

        buffer.Append(@"{\rtf1\ansi\deff0{\fonttbl{\f0\fswiss Segoe UI;}}");
        buffer.Append(@"\uc1\pard\fs").Append(BodyFontHalfPoints);

        foreach (var block in content.Blocks)
        {
            buffer.Append(@"\pard\sa120\sl280\slmult1");

            switch (block.Kind)
            {
                case BlockKind.Heading:
                    buffer.Append(@"\b\fs").Append(HeadingFontHalfPoints[block.Level]).Append(' ');
                    EmitInlines(buffer, block.Inlines, imageProvider);
                    buffer.Append(@"\b0\fs").Append(BodyFontHalfPoints).Append(' ');
                    buffer.Append(@"\par");
                    break;

                case BlockKind.ListItem:
                    EmitListItem(buffer, block, imageProvider);
                    break;

                default:
                    EmitInlines(buffer, block.Inlines, imageProvider);
                    buffer.Append(@"\par");
                    break;
            }
        }

        buffer.Append('}');
        return buffer.ToString();
    }

    private static void EmitListItem(StringBuilder buffer, DocumentBlock block,
        ImageProvider? imageProvider)
    {
        // Legacy Word-95 \pn form: RichEdit reads it into its internal list model, so
        // TOM ListType/MarkerType report the list on load (flyout state sync works).
        // (Verified design risk: if msftedit rejects \pn here, switch to LRTF \ls —
        // tracked as a wiring-time check, B2 PRD.)
        if (block.Ordered)
        {
            buffer.Append(@"{\pntext\f0 ").Append(block.ListNumber).Append(@".\tab}")
                .Append(@"{\*\pn\pnlevel1\pndec\pnindent-360{\pntxta ")
                .Append(block.ListNumber).Append(@".}}")
                .Append(@"\fi-360\li720 ");
        }
        else
        {
            buffer.Append(@"{\pntext\f0\'b7\tab}")
                .Append(@"{\*\pn\pnlevel1\pndec\pnindent-360{\pntxtb\'b7}}")
                .Append(@"\fi-360\li720 ");
        }

        EmitInlines(buffer, block.Inlines, imageProvider);
        buffer.Append(@"\par");
    }

    private static void EmitInlines(StringBuilder buffer, IReadOnlyList<DocumentInline> inlines,
        ImageProvider? imageProvider)
    {
        var bold = false;
        var italic = false;
        var strike = false;

        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case DocumentRun run:
                    // Trailing space after each toggle: a letter following the bare
                    // control word (\bb) would extend it into a different keyword.
                    if (run.Bold != bold)
                    {
                        buffer.Append(run.Bold ? @"\b " : @"\b0 ");
                        bold = run.Bold;
                    }

                    if (run.Italic != italic)
                    {
                        buffer.Append(run.Italic ? @"\i " : @"\i0 ");
                        italic = run.Italic;
                    }

                    if (run.Strikethrough != strike)
                    {
                        buffer.Append(run.Strikethrough ? @"\strike " : @"\strike0 ");
                        strike = run.Strikethrough;
                    }

                    EmitText(buffer, run.Text);
                    break;

                case DocumentImage image:
                    var payload = imageProvider?.Invoke(image.Sha256);
                    if (payload is null)
                    {
                        // Missing original: render the alt text so the note stays readable.
                        EmitText(buffer, string.IsNullOrWhiteSpace(image.AltText)
                            ? $"[{image.Sha256[..Math.Min(8, image.Sha256.Length)]}]"
                            : image.AltText);
                        break;
                    }

                    EmitPicture(buffer, payload);
                    break;
            }
        }

        // Close any open toggles so paragraph-level formatting resets cleanly.
        if (bold)
            buffer.Append(@"\b0");
        if (italic)
            buffer.Append(@"\i0");
        if (strike)
            buffer.Append(@"\strike0");
    }

    private static void EmitPicture(StringBuilder buffer, RtfImagePayload payload)
    {
        // DIP → twips: 1 DIP = 1/96", 1 twip = 1/1440" ⇒ factor 15.
        var widthTwips = (long)Math.Round(payload.DisplayWidthDip * 15);
        var heightTwips = (long)Math.Round(payload.DisplayHeightDip * 15);

        buffer.Append(@"{\pict\").Append(payload.Blip)
            .Append(@"\picw").Append(payload.PixelWidth)
            .Append(@"\pich").Append(payload.PixelHeight)
            .Append(@"\picwgoal").Append(widthTwips)
            .Append(@"\pichgoal").Append(heightTwips)
            .Append(' ');

        foreach (var b in payload.Bytes)
            buffer.Append(HexDigits[b >> 4]).Append(HexDigits[b & 0xF]);

        buffer.Append('}');
    }

    private static readonly char[] HexDigits = "0123456789abcdef".ToCharArray();

    private static void EmitText(StringBuilder buffer, string text)
    {
        foreach (var c in text)
        {
            switch (c)
            {
                case '\\':
                    buffer.Append(@"\\");
                    break;
                case '{':
                    buffer.Append(@"\{");
                    break;
                case '}':
                    buffer.Append(@"\}");
                    break;
                case '\n':
                    buffer.Append(@"\line ");
                    break;
                case '\r':
                    break; // CR is noise; LF carries the break
                default:
                    if (c <= 0x7F)
                    {
                        buffer.Append(c);
                    }
                    else
                    {
                        // RTF \uN? uses a signed 16-bit code unit with a literal
                        // fallback char consumed per unit. Surrogate pairs emit two units.
                        var unit = (short)c;
                        buffer.Append(@"\u").Append(unit).Append('?');
                    }

                    break;
            }
        }
    }
}
