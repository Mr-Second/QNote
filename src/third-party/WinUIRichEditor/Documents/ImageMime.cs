namespace WinUIRichEditor.Documents;

// Sniffs the MIME type of encoded image bytes from their magic numbers.
// Covers the common formats Win2D's CanvasBitmap can decode; anything unrecognized falls back to image/png.
internal static class ImageMime
{
    public static string Detect(byte[] b)
    {
        if (b.Length >= 4 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return "image/png";
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "image/jpeg";
        if (b.Length >= 3 && b[0] == (byte)'G' && b[1] == (byte)'I' && b[2] == (byte)'F') return "image/gif";
        if (b.Length >= 2 && b[0] == (byte)'B' && b[1] == (byte)'M') return "image/bmp";
        if (b.Length >= 12 && b[0] == (byte)'R' && b[1] == (byte)'I' && b[2] == (byte)'F' && b[3] == (byte)'F'
            && b[8] == (byte)'W' && b[9] == (byte)'E' && b[10] == (byte)'B' && b[11] == (byte)'P') return "image/webp";
        return "image/png";
    }
}
