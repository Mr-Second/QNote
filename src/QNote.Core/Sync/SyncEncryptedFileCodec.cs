using System.Security.Cryptography;
using System.Text;

namespace QNote.Sync;

/// <summary>Authenticated storage for JSON/deletions; the logical path is bound as AAD.</summary>
public static class SyncEncryptedFileCodec
{
    private static ReadOnlySpan<byte> Magic => "QNE1"u8;
    private const int PrefixBytes = 4 + 12 + 16;

    public static bool IsEncrypted(ReadOnlySpan<byte> bytes) => bytes.Length >= PrefixBytes && bytes.StartsWith(Magic);

    public static byte[] Encrypt(string path, byte[] bytes, byte[] key)
    {
        ValidateKey(key);
        var output = new byte[PrefixBytes + bytes.Length];
        Magic.CopyTo(output);
        RandomNumberGenerator.Fill(output.AsSpan(4, 12));
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(output.AsSpan(4, 12), bytes, output.AsSpan(PrefixBytes), output.AsSpan(16, 16), Encoding.UTF8.GetBytes(path));
        return output;
    }

    public static byte[] Decrypt(string path, byte[] bytes, byte[] key)
    {
        ValidateKey(key);
        if (bytes.Length < PrefixBytes || !IsEncrypted(bytes)) throw new CryptographicException("Invalid encrypted file header.");
        var plain = new byte[bytes.Length - PrefixBytes];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(bytes.AsSpan(4, 12), bytes.AsSpan(PrefixBytes), bytes.AsSpan(16, 16), plain, Encoding.UTF8.GetBytes(path));
            return plain;
        }
        catch { CryptographicOperations.ZeroMemory(plain); throw; }
    }

    public static string CreateKeyCheck(byte[] key) => Convert.ToBase64String(Encrypt("key-check", "QNote sync encryption v1"u8.ToArray(), key));

    public static string CreateDisabledProof(byte[] key, string salt) =>
        Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes("QNote encryption disabled v1\n" + salt)));

    public static bool VerifyDisabledProof(string? proof, byte[] key, string? salt)
    {
        if (string.IsNullOrEmpty(proof) || string.IsNullOrEmpty(salt)) return false;
        try
        {
            var expected = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes("QNote encryption disabled v1\n" + salt));
            return CryptographicOperations.FixedTimeEquals(expected, Convert.FromBase64String(proof));
        }
        catch (FormatException) { return false; }
    }

    public static bool VerifyKey(string? check, byte[] key)
    {
        if (key.Length != 32) return false;
        if (string.IsNullOrEmpty(check)) return false;
        byte[]? plain = null;
        try
        {
            plain = Decrypt("key-check", Convert.FromBase64String(check), key);
            return CryptographicOperations.FixedTimeEquals(plain, "QNote sync encryption v1"u8);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException) { return false; }
        finally { if (plain is not null) CryptographicOperations.ZeroMemory(plain); }
    }

    private static void ValidateKey(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != 32) throw new ArgumentException("Sync storage requires an AES-256 key.", nameof(key));
    }
}
