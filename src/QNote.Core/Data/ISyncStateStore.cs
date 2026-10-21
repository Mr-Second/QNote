using QNote.Models;

namespace QNote.Data;

/// <summary>
/// Persistence for per-entry sync bookkeeping over the v8 <c>sync_state</c>
/// table (PRD decision 11). Standalone instance methods own their connection;
/// the static <c>*WithinAsync</c> members are the composition points for callers
/// that must record sync state in the SAME transaction as the data write it
/// describes — the atomicity contract that killed the sidecar-JSON alternative.
/// </summary>
public interface ISyncStateStore
{
    /// <summary>The bookkeeping row for one entry, or <c>null</c> if never synced.</summary>
    Task<SyncStateEntry?> GetAsync(SyncEntryKind kind, string entryUuid, CancellationToken ct = default);

    /// <summary>Every bookkeeping row of one kind, ordered by uuid.</summary>
    Task<IReadOnlyList<SyncStateEntry>> GetByKindAsync(SyncEntryKind kind, CancellationToken ct = default);

    /// <summary>
    /// Inserts or overwrites the row for (<paramref name="entry"/>.EntryKind,
    /// EntryUuid) in its own transaction. Standalone use only — a caller inside
    /// its own data-write transaction must use
    /// <see cref="SyncStateStore.UpsertWithinAsync"/> instead.
    /// </summary>
    Task UpsertAsync(SyncStateEntry entry, CancellationToken ct = default);

    /// <summary>Drops the row for one entry (no-op if absent).</summary>
    Task DeleteAsync(SyncEntryKind kind, string entryUuid, CancellationToken ct = default);
}
