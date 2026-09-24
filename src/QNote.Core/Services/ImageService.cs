using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using QNote.Infrastructure;

namespace QNote.Services;

/// <summary>
/// Content-addressed storage for original images (PRD D2). The original bytes live at
/// <c>%LocalAppData%\QNote\images\&lt;sha256&gt;.&lt;ext&gt;</c>; importing the same content twice
/// never writes a second file. The inline (compressed) display copy is produced by the
/// Presentation layer via WIC and passed in — Core stays free of WinUI/WinAppSDK types.
/// </summary>
public sealed class ImageService : IImageService
{
    private readonly AppPaths _paths;
    private readonly ILogger<ImageService> _log;

    public ImageService(AppPaths paths, ILogger<ImageService> log)
    {
        _paths = paths;
        _log = log;
    }

    /// <inheritdoc/>
    public async Task<ImportedImage> ImportAsync(
        byte[] originalBytes,
        string ext,
        byte[] displayBytes,
        string displayBlip,
        int pixelWidth,
        int pixelHeight,
        CancellationToken ct = default)
    {
        if (originalBytes.Length == 0)
            throw new ArgumentException("Image bytes are empty.", nameof(originalBytes));

        var sha = Convert.ToHexStringLower(SHA256.HashData(originalBytes));
        ext = NormalizeExt(ext);
        var path = OriginalPath(sha, ext);

        if (!File.Exists(path))
        {
            Directory.CreateDirectory(_paths.ImagesDir);
            // Write to a unique temp file then move: a crash mid-write can never leave a
            // truncated file behind the content address, and two concurrent imports of the
            // same content cannot scribble over each other's temp file.
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(temp, originalBytes, ct);
                // The destination may have appeared while we were writing (a parallel
                // import of identical bytes). The content is the same by construction,
                // so losing that race is a no-op, not an error.
                File.Move(temp, path, overwrite: false);
            }
            catch (IOException) when (File.Exists(path))
            {
                // Lost the race — the identical content is already stored.
            }
            finally
            {
                TryDelete(temp);
            }
            _log.LogInformation("Stored original image {Sha} ({Bytes} B) as {Path}", sha, originalBytes.Length, path);
        }

        return new ImportedImage(sha, ext, path, pixelWidth, pixelHeight, originalBytes.LongLength, displayBytes, displayBlip);
    }

    /// <inheritdoc/>
    public string? GetOriginalPath(string sha256, string ext)
    {
        var path = OriginalPath(sha256, NormalizeExt(ext));
        return File.Exists(path) ? path : null;
    }

    /// <inheritdoc/>
    public string? FindOriginalPath(string sha256)
    {
        if (!IsHexDigest(sha256) || !Directory.Exists(_paths.ImagesDir))
            return null;

        // The ext lives in note_images, but a double-click path lookup must survive a
        // stale/missing row — scan for any <sha256>.* file. Leftover temp files
        // (<sha>.<guid>.tmp) are skipped: they are not written content addresses.
        try
        {
            return Directory
                .EnumerateFiles(_paths.ImagesDir, sha256 + ".*")
                .FirstOrDefault(p => File.Exists(p) && !p.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));
        }
        catch (IOException ex)
        {
            _log.LogWarning(ex, "Lookup of original image {Sha} failed", sha256);
            return null;
        }
    }

    /// <summary>
    /// True for a 64-char hex sha256. Also guards the <c>EnumerateFiles</c> pattern —
    /// a caller-supplied wildcard could otherwise match an unrelated file.
    /// </summary>
    private static bool IsHexDigest(string? value)
    {
        if (value is null || value.Length != 64)
            return false;
        foreach (var c in value)
        {
            if (!Uri.IsHexDigit(c))
                return false;
        }
        return true;
    }

    /// <inheritdoc/>
    public Task DeleteOriginalsAsync(IReadOnlyList<string> sha256, CancellationToken ct = default)
    {
        foreach (var sha in sha256)
        {
            ct.ThrowIfCancellationRequested();
            if (FindOriginalPath(sha) is not { } path)
                continue;
            try
            {
                File.Delete(path);
                _log.LogInformation("Deleted orphaned original image {Path}", path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A locked file (e.g. open in an image viewer) must not fail the delete
                // of the note; the file is orphaned and can be GC'd later (out of scope).
                _log.LogWarning(ex, "Could not delete orphaned original image {Path}", path);
            }
        }
        return Task.CompletedTask;
    }

    private string OriginalPath(string sha256, string ext) =>
        Path.Combine(_paths.ImagesDir, $"{sha256}.{ext}");

    /// <summary>Best-effort cleanup of a temp file; never throws at the caller.</summary>
    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp file is harmless (not content-addressed, so never read).
        }
    }

    private static string NormalizeExt(string ext)
    {
        ext = (ext ?? string.Empty).TrimStart('.').ToLowerInvariant();
        if (ext.Length == 0)
            return "bin";
        foreach (var c in ext)
        {
            if (!char.IsAsciiLetterOrDigit(c))
                return "bin";
        }
        return ext;
    }
}
