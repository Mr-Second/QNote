using QNote.Sync;

namespace QNote.Tests;

/// <summary>
/// In-memory <see cref="ISyncDriver"/> for the sync-engine battery: a path→bytes
/// dictionary, a monotonically increasing etag counter, optional conditional-write
/// enforcement (a mismatched ifMatchEtag throws PreconditionFailed), a
/// truncated-listing flag, and one-shot failure injection for the inline
/// re-resolve test. Write/delete calls are recorded so tests can assert the
/// zero-writes idempotence and the exact mutation set.
/// </summary>
internal sealed class FakeSyncDriver : ISyncDriver
{
    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Etags { get; } = new(StringComparer.Ordinal);
    public List<string> Writes { get; } = [];
    public List<string> Deletes { get; } = [];

    private int _etagCounter;

    /// <summary>Set by a test to simulate a peer write between the engine's read and write.</summary>
    public bool ThrowPreconditionOnNextWrite { get; set; }

    /// <summary>Every listing reports IsTruncated (the 坚果云 750-cap abort signal).</summary>
    public bool Truncated { get; set; }
    public HashSet<string> TruncatedFolders { get; } = new(StringComparer.Ordinal);

    public bool SupportsRangeRead { get; set; } = true;

    public Func<string, CancellationToken, Task>? BeforeRead { get; set; }

    public FakeSyncDriver Seed(string path, byte[] content)
    {
        Files[path] = content;
        Etags[path] = $"e{++_etagCounter}";
        return this;
    }

    public FakeSyncDriver SeedText(string path, string content) => Seed(path, System.Text.Encoding.UTF8.GetBytes(content));

    public Task<DriverListing> ListAsync(string folderPath, CancellationToken ct = default)
    {
        var prefix = folderPath + "/";
        var files = Files
            .Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal))
            .Select(kv => new DriverFile(kv.Key, Etags.GetValueOrDefault(kv.Key, string.Empty), kv.Value.LongLength))
            .ToList();
        return Task.FromResult(new DriverListing(files, Truncated || TruncatedFolders.Contains(folderPath)));
    }

    public async Task<byte[]?> ReadFileAsync(string path, CancellationToken ct = default)
    {
        if (BeforeRead is { } before)
            await before(path, ct);
        return Files.TryGetValue(path, out var bytes) ? bytes : null;
    }

    public Task<byte[]?> ReadRangeAsync(string path, long offset, int count, CancellationToken ct = default)
    {
        if (!Files.TryGetValue(path, out var bytes))
            return Task.FromResult<byte[]?>(null);
        var available = (int)Math.Max(0, bytes.Length - offset);
        var take = Math.Min(count, available);
        var slice = new byte[take];
        Array.Copy(bytes, offset, slice, 0, take);
        return Task.FromResult<byte[]?>(slice);
    }

    public Task<DriverWriteResult> WriteFileAsync(
        string path, byte[] content, string? ifMatchEtag = null, CancellationToken ct = default)
    {
        if (ThrowPreconditionOnNextWrite)
        {
            ThrowPreconditionOnNextWrite = false;
            throw new SyncDriverException(
                SyncDriverErrorKind.PreconditionFailed, $"injected precondition failure on {path}");
        }
        if (ifMatchEtag is not null
            && Etags.TryGetValue(path, out var current)
            && current != ifMatchEtag)
        {
            throw new SyncDriverException(
                SyncDriverErrorKind.PreconditionFailed,
                $"etag precondition failed on {path}: expected {ifMatchEtag}, found {current}");
        }

        Files[path] = content;
        Etags[path] = $"e{++_etagCounter}";
        Writes.Add(path);
        return Task.FromResult(new DriverWriteResult(RemoteId: string.Empty, Etag: Etags[path]));
    }

    public Task DeleteFileAsync(string path, CancellationToken ct = default)
    {
        Deletes.Add(path);
        Files.Remove(path);
        Etags.Remove(path);
        return Task.CompletedTask;
    }
}
