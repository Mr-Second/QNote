namespace QNote.Text;

/// <summary>
/// Encodes/decodes the <c>qnote:&lt;sha256&gt;</c> marker stored in an embedded image's
/// RTF alt text (<c>{\*\picprop{\sp{\sn wzDescription}{\sv qnote:…}}}</c>). WinRT
/// <c>ITextRange</c> exposes no way to read a pict's bytes at a position, so the alt
/// text is the reverse-lookup key for double-click "open original" (PRD D2/D3).
/// The spike proved alt text round-trips through <c>GetText → SetText → GetText</c>.
/// Pure function — lives in Core so the headless xUnit suite can cover it.
/// </summary>
public static class ImageAltCodec
{
    private const string Prefix = "qnote:";

    /// <summary>Length of a sha256 hex digest.</summary>
    public const int Sha256HexLength = 64;

    /// <summary>Wraps a lowercase hex sha256 in the <c>qnote:</c> marker.</summary>
    public static string Encode(string sha256Hex) => Prefix + sha256Hex;

    /// <summary>
    /// Extracts the sha256 from an alt string, or <c>null</c> when the alt is absent,
    /// carries no marker, or the digest is malformed. Case-insensitive; the digest is
    /// normalized to lowercase so it matches <c>note_images.sha256</c>.
    /// </summary>
    public static string? TryDecode(string? alt)
    {
        if (string.IsNullOrEmpty(alt) || !alt.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            return null;

        var digest = alt[Prefix.Length..].Trim();
        if (digest.Length != Sha256HexLength || !IsHex(digest))
            return null;

        return digest.ToLowerInvariant();
    }

    private static bool IsHex(string value)
    {
        foreach (var c in value)
        {
            if (!Uri.IsHexDigit(c))
                return false;
        }
        return true;
    }
}
