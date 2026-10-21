namespace QNote.Infrastructure;

/// <summary>
/// The 64-hex-character sha256 gate (database-guidelines rule, born from the
/// wildcard-delete incident): every digest string must pass before it may reach
/// the filesystem or a container entry. Shared so every future check stays
/// byte-identical instead of growing a fifth private copy.
/// </summary>
public static class HexDigest
{
    public const int Sha256CharCount = 64;

    public static bool IsSha256Hex(string? value)
    {
        if (value is null || value.Length != Sha256CharCount)
            return false;
        foreach (var c in value)
        {
            if (!Uri.IsHexDigit(c))
                return false;
        }
        return true;
    }
}
