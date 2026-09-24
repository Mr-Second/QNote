namespace QNote.Services;

/// <summary>
/// The outcome of importing an image: the original is now content-addressed on disk
/// and the (possibly re-encoded) display copy is ready to be inlined into the RTF.
/// </summary>
/// <param name="Sha256">Lowercase hex sha256 of the original bytes — the content address.</param>
/// <param name="Ext">Original file extension without the dot (e.g. <c>png</c>).</param>
/// <param name="OriginalPath">Absolute path of the original on disk.</param>
/// <param name="PixelWidth">Original pixel width (metadata for <c>note_images</c>).</param>
/// <param name="PixelHeight">Original pixel height.</param>
/// <param name="ByteSize">Original byte size (metadata for <c>note_images</c>).</param>
/// <param name="DisplayBytes">Bytes of the compressed copy to inline into the RTF.</param>
/// <param name="DisplayBlip"><c>pngblip</c> or <c>jpegblip</c> — the RTF blip keyword.</param>
public sealed record ImportedImage(
    string Sha256,
    string Ext,
    string OriginalPath,
    int PixelWidth,
    int PixelHeight,
    long ByteSize,
    byte[] DisplayBytes,
    string DisplayBlip);

/// <summary>Image import / content-addressed original storage (port of the Qt ImageManager).</summary>
public interface IImageService
{
    /// <summary>
    /// Stores the original bytes at <c>images\&lt;sha256&gt;.&lt;ext&gt;</c> (no-op when the
    /// content already exists — automatic dedup) and returns the address plus the
    /// display copy. The WIC decode/re-encode that produces
    /// <paramref name="displayBytes"/> happens in Presentation (Windows-only); this
    /// service owns hashing, naming, and disk layout.
    /// </summary>
    Task<ImportedImage> ImportAsync(
        byte[] originalBytes,
        string ext,
        byte[] displayBytes,
        string displayBlip,
        int pixelWidth,
        int pixelHeight,
        CancellationToken ct = default);

    /// <summary>Absolute path of the original image, or <c>null</c> when the file is missing.</summary>
    string? GetOriginalPath(string sha256, string ext);

    /// <summary>Absolute path of the original image, whichever extension it was stored with.</summary>
    string? FindOriginalPath(string sha256);

    /// <summary>
    /// Deletes the originals for the given content addresses. Callers pass only
    /// addresses no longer referenced by any note (the repository computes the orphan
    /// set under the delete transaction). Missing files are ignored.
    /// </summary>
    Task DeleteOriginalsAsync(IReadOnlyList<string> sha256, CancellationToken ct = default);
}
