namespace QNote.Sync;

/// <summary>Keep conversion progress alive across provider/request-budget pauses.</summary>
internal sealed class EncryptionConversionDriver(ISyncDriver inner) : ISyncDriver
{
    public bool SupportsRangeRead => inner.SupportsRangeRead;
    public Task<DriverListing> ListAsync(string path, CancellationToken ct = default) => RetryAsync(() => inner.ListAsync(path, ct), ct);
    public Task<byte[]?> ReadFileAsync(string path, CancellationToken ct = default) => RetryAsync(() => inner.ReadFileAsync(path, ct), ct);
    public Task<DriverReadResult?> ReadFileWithEtagAsync(string path, CancellationToken ct = default) => RetryAsync(() => inner.ReadFileWithEtagAsync(path, ct), ct);
    public Task<byte[]?> ReadRangeAsync(string path, long offset, int count, CancellationToken ct = default) => RetryAsync(() => inner.ReadRangeAsync(path, offset, count, ct), ct);
    public Task<DriverWriteResult> WriteFileAsync(string path, byte[] bytes, string? etag = null, CancellationToken ct = default) =>
        RetryAsync(() => inner.WriteFileAsync(path, bytes, etag, ct), ct);
    public async Task DeleteFileAsync(string path, CancellationToken ct = default) =>
        await RetryAsync(async () => { await inner.DeleteFileAsync(path, ct); return true; }, ct);

    private static async Task<T> RetryAsync<T>(Func<Task<T>> operation, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { return await operation(); }
            catch (SyncDriverException ex) when (ex.RetryAfter is { } delay && delay > TimeSpan.Zero)
            { await Task.Delay(delay > TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : delay, ct); }
        }
    }
}
