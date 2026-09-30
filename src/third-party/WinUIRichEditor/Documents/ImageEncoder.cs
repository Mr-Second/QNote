using System.Threading.Tasks;
using Windows.Storage.Streams;
using Microsoft.Graphics.Canvas;

namespace WinUIRichEditor.Documents;

/// <summary>Encodes a Win2D <see cref="CanvasBitmap"/> back to PNG bytes — the fallback path for an
/// image that was set as a decoded bitmap without its original encoded bytes. <c>SaveAsync</c> is
/// async + device-bound; this blocks on it with <c>ConfigureAwait(false)</c> so a UI-thread caller
/// doesn't deadlock on a continuation that would need the UI thread.</summary>
internal static class ImageEncoder
{
    /// <summary>PNG-encodes <paramref name="bmp"/>, or returns <see langword="null"/> on null/failure.</summary>
    public static byte[]? ToPngBytes(CanvasBitmap? bmp)
    {
        if (bmp == null) return null;
        try { return EncodeAsync(bmp).GetAwaiter().GetResult(); }
        catch (Exception ex) { RichEditorDiagnostics.Report(ex); return null; }
    }

    private static async Task<byte[]> EncodeAsync(CanvasBitmap bmp)
    {
        using var stream = new InMemoryRandomAccessStream();
        await bmp.SaveAsync(stream, CanvasBitmapFileFormat.Png).AsTask().ConfigureAwait(false);
        uint size = (uint)stream.Size;
        var bytes = new byte[size];
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync(size).AsTask().ConfigureAwait(false);
        reader.ReadBytes(bytes);
        return bytes;
    }
}
