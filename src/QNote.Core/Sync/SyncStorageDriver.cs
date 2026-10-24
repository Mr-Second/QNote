using System.Security.Cryptography;

namespace QNote.Sync;

/// <summary>One cycle's immutable encryption view. Does not own the underlying transport or key.</summary>
public sealed class SyncStorageDriver(ISyncDriver inner, byte[]? key, byte[]? manifestSnapshot) : ISyncDriver
{
    private byte[]? _manifestSnapshot = manifestSnapshot;
    public bool SupportsRangeRead => inner.SupportsRangeRead;
    public Task<DriverListing> ListAsync(string path, CancellationToken ct = default) => inner.ListAsync(path, ct);

    public async Task<byte[]?> ReadFileAsync(string path, CancellationToken ct = default)
    {
        var bytes = await inner.ReadFileAsync(path, ct);
        if (bytes is null || key is null || !SyncEncryptionManager.IsManagedPath(path)) return bytes;
        if (path.EndsWith(".qnote", StringComparison.Ordinal))
        {
            if (!QnoteContainerCodec.IsEncrypted(bytes)) throw new CryptographicException("An encrypted target contains an unencrypted container.");
            return bytes;
        }
        return SyncEncryptedFileCodec.Decrypt(path, bytes, key);
    }

    public async Task<byte[]?> ReadRangeAsync(string path, long offset, int count, CancellationToken ct = default)
    {
        if (!path.EndsWith(".qnote", StringComparison.Ordinal))
        {
            var bytes = await ReadFileAsync(path, ct);
            return bytes?.Skip(checked((int)offset)).Take(count).ToArray();
        }
        var header = await inner.ReadRangeAsync(path, offset, count, ct);
        if (key is not null && header is not null && offset == 0 && header.Length >= QnoteContainerCodec.HeaderByteCount
            && !QnoteContainerCodec.IsEncrypted(header)) throw new CryptographicException("An encrypted target contains an unencrypted container.");
        return header;
    }

    public async Task<DriverWriteResult> WriteFileAsync(string path, byte[] bytes, string? ifMatchEtag = null, CancellationToken ct = default)
    {
        await CheckManifestAsync(ct);
        var stored = key is not null && SyncEncryptionManager.IsManagedPath(path) && !path.EndsWith(".qnote", StringComparison.Ordinal)
            ? SyncEncryptedFileCodec.Encrypt(path, bytes, key) : bytes;
        if (key is not null && path.EndsWith(".qnote", StringComparison.Ordinal) && !QnoteContainerCodec.IsEncrypted(bytes))
            throw new CryptographicException("An unencrypted write to an encrypted target was blocked.");
        var result = await inner.WriteFileAsync(path, stored, ifMatchEtag, ct);
        if (path == SyncEngine.ManifestPath) _manifestSnapshot = bytes.ToArray();
        return result;
    }

    public async Task DeleteFileAsync(string path, CancellationToken ct = default)
    {
        await CheckManifestAsync(ct);
        await inner.DeleteFileAsync(path, ct);
    }

    private async Task CheckManifestAsync(CancellationToken ct)
    {
        var current = await inner.ReadFileAsync(SyncEngine.ManifestPath, ct);
        if (current is null ? _manifestSnapshot is not null : _manifestSnapshot is null || !current.AsSpan().SequenceEqual(_manifestSnapshot))
            throw new SyncDriverException(SyncDriverErrorKind.PreconditionFailed, "The target encryption state changed during this cycle.");
    }
}
