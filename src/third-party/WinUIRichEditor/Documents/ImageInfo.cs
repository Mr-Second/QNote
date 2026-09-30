namespace WinUIRichEditor.Documents;

/// <summary>Reads an image's pixel dimensions directly from its encoded header — synchronously and
/// without a GPU device. The Avalonia port decoded a whole <c>Bitmap</c> just to read <c>.Size</c>;
/// a Win2D <c>CanvasBitmap</c> is device-bound and async, so the importers (HTML/RTF) read the natural
/// size from the header here and defer the actual GPU decode to the render layer.</summary>
internal static class ImageInfo
{
    /// <summary>Returns the (width, height) in pixels, or (0, 0) when the format/header isn't recognized.</summary>
    public static (double Width, double Height) GetPixelSize(byte[] b)
    {
        if (b == null || b.Length < 8) return (0, 0);

        // PNG: 8-byte signature, then IHDR (length+type) — width @16, height @20, big-endian.
        if (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47 && b.Length >= 24)
            return (BE32(b, 16), BE32(b, 20));

        // GIF: "GIF", logical screen width @6 / height @8, little-endian 16-bit.
        if (b[0] == (byte)'G' && b[1] == (byte)'I' && b[2] == (byte)'F' && b.Length >= 10)
            return (LE16(b, 6), LE16(b, 8));

        // BMP: BITMAPINFOHEADER width @18 / height @22 (signed 32-bit LE; height may be negative=top-down).
        if (b[0] == (byte)'B' && b[1] == (byte)'M' && b.Length >= 26)
        {
            int w = (int)LE32(b, 18);
            int h = (int)LE32(b, 22);
            return (System.Math.Abs(w), System.Math.Abs(h));
        }

        // JPEG: scan for a Start-Of-Frame marker; height @marker+5, width @marker+7 (big-endian).
        if (b[0] == 0xFF && b[1] == 0xD8)
        {
            int i = 2;
            while (i + 9 < b.Length)
            {
                if (b[i] != 0xFF) { i++; continue; }
                byte marker = b[i + 1];
                // SOF0..SOF15 except DHT(C4)/JPG(C8)/DAC(CC) carry frame dimensions.
                if (marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
                    return (BE16(b, i + 7), BE16(b, i + 5));
                int seg = BE16(b, i + 2);
                if (seg <= 0) break;
                i += 2 + seg;
            }
        }

        // WEBP: "RIFF"…"WEBP", then one of the three first-chunk layouts. Covering all of them matters —
        // most .webp files in the wild are plain lossy 'VP8 ' (no extended header), which used to be
        // rejected here (drag-and-drop refused the file, paste couldn't size it).
        if (b.Length >= 25 && b[0] == (byte)'R' && b[1] == (byte)'I' && b[2] == (byte)'F' && b[3] == (byte)'F'
            && b[8] == (byte)'W' && b[9] == (byte)'E' && b[10] == (byte)'B' && b[11] == (byte)'P'
            && b[12] == (byte)'V' && b[13] == (byte)'P' && b[14] == (byte)'8')
        {
            // 'VP8X' extended header: canvas size @24/27 as 24-bit LE minus 1.
            if (b[15] == (byte)'X' && b.Length >= 30)
            {
                int w = (b[24] | (b[25] << 8) | (b[26] << 16)) + 1;
                int h = (b[27] | (b[28] << 8) | (b[29] << 16)) + 1;
                return (w, h);
            }
            // 'VP8 ' lossy: 3-byte frame tag @20, sync code 9D 01 2A @23, then 14-bit LE width @26 / height @28.
            if (b[15] == (byte)' ' && b.Length >= 30 && b[23] == 0x9D && b[24] == 0x01 && b[25] == 0x2A)
                return (LE16(b, 26) & 0x3FFF, LE16(b, 28) & 0x3FFF);
            // 'VP8L' lossless: signature 0x2F @20, then a 28-bit field: (width-1) low 14 bits, (height-1) next 14.
            if (b[15] == (byte)'L' && b[20] == 0x2F)
            {
                uint bits = (uint)(b[21] | (b[22] << 8) | (b[23] << 16) | (b[24] << 24));
                return ((int)(bits & 0x3FFF) + 1, (int)((bits >> 14) & 0x3FFF) + 1);
            }
        }

        return (0, 0);
    }

    private static int BE16(byte[] b, int i) => (b[i] << 8) | b[i + 1];
    private static long BE32(byte[] b, int i) => ((long)b[i] << 24) | ((long)b[i + 1] << 16) | ((long)b[i + 2] << 8) | b[i + 3];
    private static int LE16(byte[] b, int i) => b[i] | (b[i + 1] << 8);
    private static long LE32(byte[] b, int i) => (uint)(b[i] | (b[i + 1] << 8) | (b[i + 2] << 16) | (b[i + 3] << 24));
}
