namespace QNote.Sync;

/// <summary>One file in a driver listing.</summary>
/// <param name="Path">
/// Driver-root-relative logical path, identical in form to what the engine passes
/// to <see cref="ISyncDriver"/> (e.g. <c>notes/{uuid}.qnote</c>). The engine never
/// concatenates paths — the listing form and the request form are the same.
/// </param>
/// <param name="Etag">Cloud concurrency token; empty when the driver has none.</param>
/// <param name="Size">Byte size when known, -1 otherwise.</param>
public sealed record DriverFile(string Path, string Etag, long Size);

/// <summary>The result of listing one cloud folder.</summary>
/// <param name="Files">Every file under the folder, pagination already resolved.</param>
/// <param name="IsTruncated">
/// True when the driver could NOT prove the listing is complete (e.g. 坚果云's
/// 750-item cap). The engine aborts the whole cycle on it — PRD decision 10:
/// block-and-warn, never infer a deletion from a missing entry.
/// </param>
public sealed record DriverListing(IReadOnlyList<DriverFile> Files, bool IsTruncated);

/// <summary>The cloud identity a conditional write produced.</summary>
public sealed record DriverWriteResult(string RemoteId, string Etag);

/// <summary>Classification of sync-driver failures, for localized UI messages.</summary>
public enum SyncDriverErrorKind
{
    /// <summary>Credentials are missing, expired, or were rejected.</summary>
    AuthRequired,

    /// <summary>The service is unreachable (offline, DNS, timeout).</summary>
    NetworkUnavailable,

    /// <summary>The provider refused the write for storage-quota reasons.</summary>
    QuotaExceeded,

    /// <summary>The listing could not be proven complete (e.g. 坚果云 750-item cap).</summary>
    ListingIncomplete,

    /// <summary>A conditional write lost the race: the file changed under us.</summary>
    PreconditionFailed,

    /// <summary>Any other service-side failure.</summary>
    ServiceError,
}

/// <summary>
/// An expected sync-driver failure (error-handling spec: expected outcomes are
/// modeled, not buried in generic exceptions). <see cref="Kind"/> tells the sync
/// engine / Presentation which localized message applies; the technical detail
/// travels via <see cref="Exception.Message"/>/<see cref="Exception.InnerException"/>.
/// Messages are English diagnostics — Core never owns user-visible wording.
/// </summary>
public sealed class SyncDriverException : Exception
{
    public SyncDriverException(SyncDriverErrorKind kind, string message, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
    }

    public SyncDriverErrorKind Kind { get; }
}

/// <summary>
/// The L1 sync transport abstraction (task 10-06): OneDrive (L2) and WebDAV (L3)
/// only implement this — the delta matrix lives entirely in the
/// <see cref="SyncEngine"/>. Every path is a driver-ROOT-RELATIVE logical path
/// (<c>notes/{uuid}.qnote</c>, <c>categories/{uuid}.json</c>,
/// <c>tombstones/{uuid}</c>, <c>manifest.json</c>); the driver maps it onto its
/// own storage root and owns pagination, auth, and retry-free error mapping.
/// </summary>
public interface ISyncDriver
{
    /// <summary>
    /// Lists every file under <paramref name="folderPath"/> (e.g. <c>notes</c>),
    /// resolving the driver's pagination internally.
    /// </summary>
    Task<DriverListing> ListAsync(string folderPath, CancellationToken ct = default);

    /// <summary>
    /// Reads a whole file. Returns <c>null</c> when the file does not exist —
    /// absence is an expected outcome (error-handling spec), not an exception;
    /// transport failures throw <see cref="SyncDriverException"/>.
    /// </summary>
    Task<byte[]?> ReadFileAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Whether <see cref="ReadRangeAsync"/> is trustworthy on this backend. False
    /// (WebDAV servers without Range support) and the engine falls back to full
    /// downloads for its header-only LWW probes.
    /// </summary>
    bool SupportsRangeRead { get; }

    /// <summary>
    /// Reads <paramref name="count"/> bytes starting at <paramref name="offset"/>
    /// (the 48-byte header probe: <c>offset 0, count 48</c>). Returns fewer bytes
    /// when the file is shorter; <c>null</c> when the file does not exist.
    /// Only called when <see cref="SupportsRangeRead"/> is true.
    /// </summary>
    Task<byte[]?> ReadRangeAsync(string path, long offset, int count, CancellationToken ct = default);

    /// <summary>
    /// Writes (creating or replacing) a file. <paramref name="ifMatchEtag"/> turns
    /// the write conditional — the cross-peer correctness mechanism: when a peer
    /// overwrote the cloud file under us, the driver fails the write with
    /// <see cref="SyncDriverErrorKind.PreconditionFailed"/> and the engine
    /// re-resolves inline. <c>null</c> writes unconditionally (creates).
    /// </summary>
    Task<DriverWriteResult> WriteFileAsync(
        string path, byte[] content, string? ifMatchEtag = null, CancellationToken ct = default);

    /// <summary>Deletes a file. Idempotent: a missing file is success.</summary>
    Task DeleteFileAsync(string path, CancellationToken ct = default);
}
