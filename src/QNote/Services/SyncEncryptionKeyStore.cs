using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using QNote.Infrastructure;

namespace QNote.Services;

/// <summary>Target-bound derived keys, never passwords; excluded from database and backups.</summary>
public sealed class SyncEncryptionKeyStore(AppPaths paths, ILogger<SyncEncryptionKeyStore> log)
{
    private string FilePath => Path.Combine(paths.Root, "sync-encryption-keys.bin");

    public async Task<byte[]?> LoadAsync(string target, string? salt, CancellationToken ct = default)
    {
        var entries = await ReadAsync(ct);
        if (!entries.TryGetValue(target, out var entry) || salt is not null && entry.Salt != salt) return null;
        try
        {
            var key = Convert.FromBase64String(entry.Key);
            if (key.Length == 32) return key;
            CryptographicOperations.ZeroMemory(key);
            return null;
        }
        catch (FormatException) { return null; }
    }

    public async Task SaveAsync(string target, string salt, byte[] key, CancellationToken ct = default, bool prepared = false)
    {
        if (key.Length != 32) throw new ArgumentException("Invalid sync key length.", nameof(key));
        var entries = await ReadAsync(ct);
        entries[target] = new SyncStoredKey(salt, Convert.ToBase64String(key), prepared);
        await WriteAsync(entries, ct);
    }

    public async Task ForgetAsync(string target, CancellationToken ct = default)
    {
        var entries = await ReadAsync(ct);
        if (entries.Remove(target)) await WriteAsync(entries, ct);
    }

    public async Task<bool> IsPreparedAsync(string target, CancellationToken ct = default) =>
        (await ReadAsync(ct)).TryGetValue(target, out var entry) && entry.Prepared;

    public async Task<string?> GetSaltAsync(string target, CancellationToken ct = default) =>
        (await ReadAsync(ct)).TryGetValue(target, out var entry) ? entry.Salt : null;

    private async Task<Dictionary<string, SyncStoredKey>> ReadAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Sync keys require Windows DPAPI.");
        if (!File.Exists(FilePath)) return new(StringComparer.Ordinal);
        byte[]? plain = null;
        try
        {
            plain = ProtectedData.Unprotect(await File.ReadAllBytesAsync(FilePath, ct), null, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize(plain, SyncStoredKeyJsonContext.Default.DictionaryStringSyncStoredKey)
                ?? throw new InvalidDataException("The protected sync key cache is empty.");
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or InvalidDataException)
        {
            // Re-entering the password can reconstruct a derived key. An unreadable
            // cache must not permanently prevent unlocking the cloud target.
            log.LogWarning(ex, "The protected sync key cache is unreadable; password unlock is required.");
            return new(StringComparer.Ordinal);
        }
        finally { if (plain is not null) CryptographicOperations.ZeroMemory(plain); }
    }

    private async Task WriteAsync(Dictionary<string, SyncStoredKey> entries, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Sync keys require Windows DPAPI.");
        var plain = JsonSerializer.SerializeToUtf8Bytes(entries, SyncStoredKeyJsonContext.Default.DictionaryStringSyncStoredKey);
        byte[] encrypted;
        try { encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        Directory.CreateDirectory(paths.Root);
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, encrypted, ct);
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

internal sealed record SyncStoredKey(string Salt, string Key, bool Prepared = false)
{
    public override string ToString() => "Protected synchronization key";
}

[JsonSerializable(typeof(Dictionary<string, SyncStoredKey>))]
internal sealed partial class SyncStoredKeyJsonContext : JsonSerializerContext;
