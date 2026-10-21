using System.Security.Cryptography;

namespace QNote.Sync;

/// <summary>
/// E2EE master-key derivation for one sync target (PRD decision 12): PBKDF2-SHA256
/// over the user's password with the target's single 32-byte salt (stored in the
/// cloud <c>manifest.json</c>), producing the 32-byte AES-256-GCM key the engine
/// feeds to the container codec. Called ONCE per cycle — never per file.
/// The parameters are deliberately constants: a peer must derive the identical
/// key, so tuning them means a cloud-layout migration, not a code change.
/// </summary>
public static class SyncKeyDeriver
{
    /// <summary>210k iterations — the recorded default (params unpinned by the PRD).</summary>
    public const int Iterations = 210_000;

    public const int SaltByteCount = 32;

    public const int KeyByteCount = 32;

    public static byte[] Derive(string password, byte[] salt)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        ArgumentNullException.ThrowIfNull(salt);
        if (salt.Length != SaltByteCount)
            throw new ArgumentException(
                $"The E2EE salt must be {SaltByteCount} bytes, got {salt.Length}.", nameof(salt));

        return Rfc2898DeriveBytes.Pbkdf2(
            password, salt, Iterations, HashAlgorithmName.SHA256, KeyByteCount);
    }
}
