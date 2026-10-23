namespace QNote.Models;

/// <summary>
/// The lightweight local-row stamp the sync engine's delta matrix decides on:
/// (Id, Uuid, UpdatedAt, DeletedAt) for EVERY row of a synced table including
/// tombstones. Full rows are loaded only for the entries an action actually
/// touches — reading every Content blob each cycle would fight the memory
/// north star for nothing.
/// </summary>
public sealed record SyncRowStamp(
    long Id,
    string Uuid,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? DeletedAt)
{
    /// <summary>Null <see cref="DeletedAt"/> = the row is live.</summary>
    public bool IsLive => DeletedAt is null;
}

/// <summary>
/// Expected local state for a replicated write. Null Stamp means absent;
/// Row adds a full comparison for live rows, including same-millisecond edits.
/// </summary>
public sealed record SyncImportSnapshot<T>(SyncRowStamp? Stamp, T? Row) where T : class;
