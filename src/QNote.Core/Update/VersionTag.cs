using System.Globalization;

namespace QNote.Update;

/// <summary>Outcome of comparing a GitHub release tag against the running version.</summary>
public enum VersionTagComparison
{
    /// <summary>The tag's numeric core is newer than the running version.</summary>
    Newer = 0,

    /// <summary>Identical numeric core (any prerelease suffix on the tag is ignored).</summary>
    Same = 1,

    /// <summary>The tag's numeric core is older than the running version.</summary>
    Older = 2,

    /// <summary>
    /// The tag (or the running version) could not be parsed. Callers must treat
    /// this as a FAILED check — an unparseable tag must never read as "no update".
    /// </summary>
    Invalid = 3,
}

/// <summary>
/// Pure GitHub release-tag ↔ running-version comparison for the manual update
/// check (portable/GitHub channel). Grammar: optional <c>v</c>/<c>V</c> prefix,
/// 1–4 dot-separated numeric components, then an optional prerelease-ish
/// suffix starting with <c>-</c> or <c>+</c>. Rules:
/// <list type="bullet">
/// <item>Prerelease suffixes are IGNORED — only the numeric core decides. QNote
/// ships stable tags only, so <c>v1.2.0-beta.1</c> counts as <c>1.2.0</c>.</item>
/// <item>Shorter cores are zero-padded to 4 parts: <c>v1.2</c> == <c>1.2.0.0</c>.</item>
/// <item>Anything else (empty, bare "v", 5+ parts, non-numeric or signed
/// components) is <see cref="VersionTagComparison.Invalid"/>.</item>
/// </list>
/// The running version comes from the main module's FileVersion (e.g. "1.1.0.0"
/// — exe VERSIONINFO, robust for unpackaged builds where the MSIX manifest
/// does not apply).
/// </summary>
public static class VersionTag
{
    private const int MaxParts = 4;

    /// <summary>
    /// Compare <paramref name="releaseTag"/> (e.g. "v1.2.0") against
    /// <paramref name="currentVersion"/> (e.g. "1.1.0.0") by numeric core.
    /// </summary>
    public static VersionTagComparison Compare(string? releaseTag, string? currentVersion)
    {
        if (!TryParseCore(releaseTag, out var tagParts) || !TryParseCore(currentVersion, out var currentParts))
            return VersionTagComparison.Invalid;

        for (var i = 0; i < MaxParts; i++)
        {
            if (tagParts[i] != currentParts[i])
                return tagParts[i] > currentParts[i] ? VersionTagComparison.Newer : VersionTagComparison.Older;
        }
        return VersionTagComparison.Same;
    }

    /// <summary>Parse the numeric core into <see cref="MaxParts"/> zero-padded parts.</summary>
    private static bool TryParseCore(string? text, out int[] parts)
    {
        parts = new int[MaxParts];
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var value = text.Trim();
        if (value.Length > 0 && (value[0] == 'v' || value[0] == 'V'))
            value = value[1..];

        // Prerelease-ish suffix (v1.2.0-beta.1, 1.2.0+build.7): ignored by design.
        var suffix = value.IndexOfAny(['-', '+']);
        if (suffix >= 0)
            value = value[..suffix];

        var pieces = value.Split('.');
        if (pieces.Length > MaxParts)
            return false;

        for (var i = 0; i < pieces.Length; i++)
        {
            // NumberStyles.None: no whitespace, no sign — "1 .2" / "1.-2" are invalid.
            if (!int.TryParse(pieces[i], NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                return false;
            parts[i] = number;
        }
        return true;
    }
}
