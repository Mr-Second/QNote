namespace QNote.Text;

/// <summary>Which blip keyword the display copy must be encoded with.</summary>
public enum DisplayBlipKind
{
    Png,
    Jpeg,
}

/// <summary>The encoding plan for an image's inline display copy (PRD D4).</summary>
/// <param name="Kind">PNG when the image has alpha, else JPEG.</param>
/// <param name="MaxDimension">Longest-side cap for the display copy.</param>
/// <param name="JpegQuality">JPEG quality (only meaningful for <see cref="DisplayBlipKind.Jpeg"/>).</param>
/// <param name="ReuseSourceBytes">
/// True when the source is an in-cap JPEG with no re-encode needed — the display copy
/// is the original bytes verbatim (no generational loss, no wasted work).
/// </param>
public sealed record DisplayEncodePlan(
    DisplayBlipKind Kind,
    int MaxDimension,
    float JpegQuality,
    bool ReuseSourceBytes);

/// <summary>
/// Decides how to build the inline (compressed) display copy of an imported image
/// (PRD D4). The original bytes are always kept untouched on disk; this policy only
/// governs the copy that lives in <c>notes.Content</c>.
///
/// Policy (ponytail: two knobs, no config surface):
/// <list type="bullet">
///   <item>Alpha → PNG (lossless, preserves transparency); no alpha → JPEG.</item>
///   <item>Longest side capped at <see cref="MaxDisplayDimension"/> px (scaled to fit).</item>
///   <item>JPEG re-encodes at <see cref="JpegQuality"/>; a JPEG source already within
///     the cap and under <see cref="JpegReuseMaxBytes"/> is reused verbatim.</item>
/// </list>
/// Pure decision logic — the WIC encode itself lives in Presentation (Windows-only),
/// so this stays unit-testable in the headless Core suite.
/// </summary>
public static class ImageCompressionPolicy
{
    /// <summary>Longest side of the inline display copy, in pixels.</summary>
    public const int MaxDisplayDimension = 1920;

    /// <summary>JPEG quality for re-encoded display copies.</summary>
    public const float JpegQuality = 0.82f;

    /// <summary>A source JPEG at or below this size (and within the cap) is embedded as-is.</summary>
    public const int JpegReuseMaxBytes = 2 * 1024 * 1024;

    /// <summary>
    /// Builds the encode plan for an image. <paramref name="sourceIsJpeg"/> is true
    /// when the source bytes are already JPEG (no alpha possible).
    /// </summary>
    public static DisplayEncodePlan Decide(bool hasAlpha, bool sourceIsJpeg, int pixelWidth, int pixelHeight, long sourceByteSize)
    {
        // Alpha forces PNG regardless of the source container (JPEG can't carry alpha,
        // so sourceIsJpeg and hasAlpha are mutually exclusive by construction).
        var kind = hasAlpha && !sourceIsJpeg ? DisplayBlipKind.Png : DisplayBlipKind.Jpeg;

        var withinCap = Math.Max(pixelWidth, pixelHeight) <= MaxDisplayDimension;
        var reuse = kind == DisplayBlipKind.Jpeg
                    && sourceIsJpeg
                    && withinCap
                    && sourceByteSize <= JpegReuseMaxBytes;

        return new DisplayEncodePlan(kind, MaxDisplayDimension, JpegQuality, reuse);
    }
}
