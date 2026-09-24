using System.Runtime.InteropServices.WindowsRuntime;
using QNote.Text;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
namespace QNote.Controls;

/// <summary>The normalized display copy produced from a source image.</summary>
/// <param name="DisplayBytes">Bytes to inline into the RTF (PNG or JPEG).</param>
/// <param name="Blip"><c>pngblip</c> or <c>jpegblip</c>.</param>
/// <param name="PixelWidth">Original pixel width (for <c>note_images</c>).</param>
/// <param name="PixelHeight">Original pixel height.</param>
/// <param name="Ext">The original's extension (without dot) for on-disk storage.</param>
public sealed record NormalizedImage(byte[] DisplayBytes, string Blip, int PixelWidth, int PixelHeight, string Ext);

/// <summary>
/// WIC decode / re-encode pipeline for the inline display copy (PRD D4). Lives in
/// Presentation because <c>Windows.Graphics.Imaging</c> needs the Windows TFM; the
/// decision logic is Core's <see cref="ImageCompressionPolicy"/>. The original bytes
/// are handed through untouched — only the copy shown in the note is re-encoded.
/// </summary>
public static class WicImageNormalizer
{
    /// <summary>Source extensions we accept for direct decode (SVG is unsupported by WIC — spike E9).</summary>
    private static readonly HashSet<string> SupportedExt = new(StringComparer.OrdinalIgnoreCase)
    {
        "png", "jpg", "jpeg", "gif", "bmp", "tif", "tiff", "webp",
    };

    /// <summary>True when the extension is a WIC-decodable raster format.</summary>
    public static bool IsSupported(string extension) => SupportedExt.Contains(Normalize(extension));

    /// <summary>
    /// Decodes <paramref name="sourceBytes"/>, detects alpha, decides a plan, and
    /// produces the display copy. Throws <see cref="InvalidDataException"/> for an
    /// undecodable/unsupported image — callers surface that as a user-facing message.
    /// </summary>
    public static async Task<NormalizedImage> NormalizeAsync(byte[] sourceBytes, string extension, CancellationToken ct = default)
    {
        using var stream = await ToStreamAsync(sourceBytes);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        ct.ThrowIfCancellationRequested();

        var width = (int)decoder.PixelWidth;
        var height = (int)decoder.PixelHeight;
        var ext = Normalize(extension);
        var sourceIsJpeg = ext is "jpg" or "jpeg";

        // Alpha probe: JPEG can't carry alpha, so skip the (full-frame) decode for it.
        var hasAlpha = !sourceIsJpeg && await HasAlphaAsync(decoder);

        var plan = ImageCompressionPolicy.Decide(hasAlpha, sourceIsJpeg, width, height, sourceBytes.LongLength);
        if (plan.ReuseSourceBytes)
        {
            return new NormalizedImage(sourceBytes, "jpegblip", width, height, "jpg");
        }

        var (scaledWidth, scaledHeight) = ScaleToFit(width, height, plan.MaxDimension);
        var kind = plan.Kind;
        var bytes = await EncodeAsync(decoder, scaledWidth, scaledHeight, kind, plan.JpegQuality, ct);
        return new NormalizedImage(bytes, kind == DisplayBlipKind.Png ? "pngblip" : "jpegblip", width, height, ext);
    }

    private static (uint Width, uint Height) ScaleToFit(int width, int height, int maxDimension)
    {
        var longest = Math.Max(width, height);
        if (longest <= maxDimension)
            return ((uint)width, (uint)height);

        var scale = maxDimension / (double)longest;
        return ((uint)Math.Max(1, Math.Round(width * scale)), (uint)Math.Max(1, Math.Round(height * scale)));
    }

    private static async Task<bool> HasAlphaAsync(BitmapDecoder decoder)
    {
        // Probe on a small downscaled copy: BGRA8 straight alpha lets us read the alpha
        // bytes directly, and WIC zero-/255-fills alpha for formats without
        // transparency, so any non-255 byte means alpha. Decoding the FULL frame here
        // would allocate width*height*4 bytes (≈33 MB for a 4K source) just to answer a
        // yes/no question — against the memory north star (PRD D4).
        var (probeWidth, probeHeight) = ScaleToFit((int)decoder.PixelWidth, (int)decoder.PixelHeight, AlphaProbeMaxDimension);
        var pixels = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Straight,
            new BitmapTransform { ScaledWidth = probeWidth, ScaledHeight = probeHeight },
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);
        var data = pixels.DetachPixelData();
        for (var i = 3; i < data.Length; i += 4)
        {
            if (data[i] != 0xFF)
                return true;
        }
        return false;
    }

    /// <summary>Longest side of the downscaled alpha probe (cheap, still representative).</summary>
    private const int AlphaProbeMaxDimension = 256;

    private static async Task<byte[]> EncodeAsync(
        BitmapDecoder decoder, uint width, uint height, DisplayBlipKind kind, float jpegQuality, CancellationToken ct)
    {
        var transform = new BitmapTransform
        {
            ScaledWidth = width,
            ScaledHeight = height,
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        var pixels = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Straight,
            transform,
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.ColorManageToSRgb);
        ct.ThrowIfCancellationRequested();

        using var output = new InMemoryRandomAccessStream();
        BitmapEncoder encoder;
        if (kind == DisplayBlipKind.Jpeg)
        {
            var props = new BitmapPropertySet
            {
                { "ImageQuality", new BitmapTypedValue(jpegQuality, Windows.Foundation.PropertyType.Single) },
            };
            encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output, props);
        }
        else
        {
            encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
        }

        // AlphaMode.Ignore for JPEG (opaque), Premultiplied for PNG (real transparency).
        var alphaMode = kind == DisplayBlipKind.Png ? BitmapAlphaMode.Premultiplied : BitmapAlphaMode.Ignore;
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, alphaMode, width, height, decoder.DpiX, decoder.DpiY, pixels.DetachPixelData());
        await encoder.FlushAsync();
        ct.ThrowIfCancellationRequested();

        return await ReadAllAsync(output);
    }

    private static async Task<InMemoryRandomAccessStream> ToStreamAsync(byte[] bytes)
    {
        var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(bytes.AsBuffer());
        await stream.FlushAsync();
        stream.Seek(0);
        return stream;
    }

    /// <summary>In-memory stream for <c>InsertImage</c> (it takes an <c>IRandomAccessStream</c>).</summary>
    public static Task<InMemoryRandomAccessStream> ToInMemoryStreamAsync(byte[] bytes) => ToStreamAsync(bytes);

    internal static async Task<byte[]> ReadAllAsync(IRandomAccessStream stream)
    {
        stream.Seek(0);
        using var memory = new MemoryStream();
        var buffer = new Windows.Storage.Streams.Buffer(1 << 20);
        while (true)
        {
            var read = await stream.ReadAsync(buffer, buffer.Capacity, InputStreamOptions.None);
            if (read.Length == 0)
                break;
            memory.Write(read.ToArray());
        }
        return memory.ToArray();
    }

    private static string Normalize(string extension) =>
        (extension ?? string.Empty).TrimStart('.').ToLowerInvariant();
}
