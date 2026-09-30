namespace QNote.Markdown;

/// <summary>
/// Inline image payload the editor bridge needs to embed a <c>qnote-img:</c> reference:
/// the compressed display bytes plus display geometry. Resolved by the caller (wiring
/// layer) from persistence; the model itself never touches files.
/// </summary>
/// <param name="Blip">Blip keyword — <c>pngblip</c> or <c>jpegblip</c>.</param>
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
    /// is how the editor renders picts inserted at natural size. Returns null for
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
