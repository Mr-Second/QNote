using QNote.Controls;

namespace QNote.Services;

/// <summary>
/// Presentation-side adapter from the WIC re-encode pipeline
/// (<see cref="WicImageNormalizer"/> — the editor image-insert path's display-copy
/// encoder) to the sync engine's synchronous
/// <see cref="QNote.Sync.SyncEngine.DisplayImageGenerator"/> hook. A container
/// apply lands ORIGINAL bytes only; without this callback the imported
/// <c>note_images</c> rows carry no display copy and degrade to editor
/// placeholders (PRD decision 19: Presentation owns the WIC pipeline, Core stays
/// WinUI-free). Returns null for extensions WIC cannot decode; a throw is caught
/// by the engine and degrades that one image — never the cycle.
/// </summary>
public static class WicDisplayImageGenerator
{
    /// <summary>The composition-root entry point for <c>SyncEngine.DisplayImageGenerator</c>.</summary>
    public static byte[]? Generate(byte[] originalBytes, string ext)
    {
        ArgumentNullException.ThrowIfNull(originalBytes);
        if (!WicImageNormalizer.IsSupported(ext))
            return null;

        // The engine runs cycles on background threads only (the polling timer and
        // the push queue both fire off the UI thread), so this sync-over-async
        // bridge cannot deadlock; Task.Run additionally detaches any ambient
        // context. The bytes are the ORIGINAL's — re-encoding only ever produces
        // the display copy (never a stored-original rewrite).
        return Task.Run(() => WicImageNormalizer.NormalizeAsync(originalBytes, ext))
            .GetAwaiter().GetResult()
            .DisplayBytes;
    }
}
