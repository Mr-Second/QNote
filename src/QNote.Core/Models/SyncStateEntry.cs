namespace QNote.Models;

/// <summary>
/// Per-entry sync bookkeeping (one <c>sync_state</c> row, schema v8 / PRD
/// decision 11): what the cloud copy of a local entry looked like the last time
/// the engine saw them agree. Written IN THE SAME TRANSACTION as the data write
/// it describes, so a crash can never leave a row whose sync state is missing or
/// stale beyond its own content.
/// </summary>
public sealed record SyncStateEntry
{
    public required SyncEntryKind EntryKind { get; init; }

    /// <summary>The local entry's Uuid (the cross-device key).</summary>
    public required string EntryUuid { get; init; }

    /// <summary>
    /// Driver-specific cloud identity of the synced file (OneDrive item id, WebDAV
    /// path, …). Empty when the driver addresses entries by path alone.
    /// </summary>
    public required string RemoteId { get; init; }

    /// <summary>The cloud ETag at last agreement (empty when the driver has none).</summary>
    public required string Etag { get; init; }

    /// <summary>Epoch-ms instant of the last confirmed upload/download agreement.</summary>
    public required long LastSyncedAtMs { get; init; }
}
