using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace QNote.Controls;

/// <summary>A clipboard image source: either raw bytes (with an extension hint) or a file path.</summary>
/// <param name="Bytes">Encoded image bytes, or <c>null</c> when <paramref name="FilePath"/> is set.</param>
/// <param name="Extension">Extension without the dot (bytes path only).</param>
/// <param name="FilePath">Absolute path of a file on the clipboard (StorageItems path only).</param>
public sealed record ClipboardImageSource(byte[]? Bytes, string Extension, string? FilePath);

/// <summary>
/// Reads image content off the Windows clipboard, normalizing the various shapes into
/// <see cref="ClipboardImageSource"/>s the import pipeline can consume:
/// StorageItems (Explorer Ctrl+C of files), then a bitmap (screenshot / browser
/// "copy image" — which arrives as <c>image/bmp</c> from a CF_DIB-only clipboard and
/// is re-encoded to PNG here; spike E6), then nothing. RTF is handled separately by
/// the editor's paste path (it may carry picts we keep verbatim).
/// </summary>
public static class ClipboardImageReader
{
    /// <summary>Returns clipboard image sources in priority order (files before a loose bitmap).</summary>
    public static async Task<IReadOnlyList<ClipboardImageSource>> ReadAsync(ILogger? log = null)
    {
        var result = new List<ClipboardImageSource>();
        DataPackageView content;
        try
        {
            content = Clipboard.GetContent();
        }
        catch (Exception ex)
        {
            log?.LogWarning(ex, "Clipboard read failed; treated as no image");
            return result; // clipboard busy/closed — treated as "no image"
        }

        if (content.Contains(StandardDataFormats.StorageItems))
        {
            try
            {
                foreach (var item in await content.GetStorageItemsAsync())
                {
                    if (item is StorageFile file && WicImageNormalizer.IsSupported(file.FileType))
                        result.Add(new ClipboardImageSource(null, Normalize(file.FileType), file.Path));
                }
            }
            catch (Exception ex)
            {
                log?.LogWarning(ex, "Reading clipboard StorageItems failed; falling back to bitmap");
                // fall through to a bitmap attempt
            }

            // WinRT's GetStorageItemsAsync can throw RPC_E_WRONG_THREAD (0x8001010E)
            // for clipboards produced by some apps (observed with a WinForms
            // SetFileDropList clipboard; the async OLE read is apartment-sensitive).
            // The raw Win32 clipboard APIs are apartment-free, so retry there before
            // giving up on the file list.
            if (result.Count == 0)
            {
                foreach (var path in Win32Clipboard.ReadFileDrop())
                {
                    var ext = Normalize(Path.GetExtension(path));
                    if (WicImageNormalizer.IsSupported(ext))
                        result.Add(new ClipboardImageSource(null, ext, path));
                }
                if (result.Count > 0)
                    log?.LogInformation("Clipboard file list recovered via the Win32 fallback ({Count} image(s))", result.Count);
            }
        }

        if (result.Count == 0 && content.Contains(StandardDataFormats.Bitmap))
        {
            try
            {
                var reference = await content.GetBitmapAsync();
                using var stream = await reference.OpenReadAsync();
                // A CF_DIB-only clipboard surfaces here as image/bmp; PNG stays PNG.
                var bytes = await WicImageNormalizer.ReadAllAsync(stream);
                result.Add(new ClipboardImageSource(bytes, ExtensionFor(stream.ContentType), null));
            }
            catch (Exception ex)
            {
                log?.LogWarning(ex, "Reading clipboard bitmap via WinRT failed");
                // no usable bitmap — try the raw DIB below
            }

            // Same apartment story as the file list: a plain CF_DIB/CF_DIBV5 from an
            // app whose DIB shape WinRT rejects (e.g. WinForms Clipboard.SetImage)
            // silently yields nothing above. Read the raw DIB and wrap it in a BMP
            // file header — WIC's BMP decoder handles the variants WinRT will not.
            if (result.Count == 0)
            {
                var bmp = Win32Clipboard.ReadDibAsBmp();
                if (bmp is not null)
                {
                    result.Add(new ClipboardImageSource(bmp, "bmp", null));
                    log?.LogInformation("Clipboard bitmap recovered via the Win32 DIB fallback ({Bytes} bytes)", bmp.Length);
                }
            }
        }

        return result;
    }

    private static string ExtensionFor(string contentType) => contentType switch
    {
        "image/png" => "png",
        "image/jpeg" or "image/jpg" => "jpg",
        "image/gif" => "gif",
        "image/webp" => "webp",
        "image/tiff" => "tif",
        _ => "bmp",
    };

    private static string Normalize(string extension) =>
        (extension ?? string.Empty).TrimStart('.').ToLowerInvariant();
}
