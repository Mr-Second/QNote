namespace QNote.Sync;

/// <summary>Why a cycle stopped before touching anything. <see cref="None"/> = ran.</summary>
public enum SyncCycleAbortKind
{
    /// <summary>The cycle ran (aborts happen only before the first mutation).</summary>
    None,

    /// <summary>
    /// A listing could not be proven complete (坚果云 750-item cap). PRD decision
    /// 10: block-and-warn — no mutation may be based on an incomplete view.
    /// </summary>
    ListingIncomplete,

    /// <summary>The cloud <c>manifest.json</c> was written by a newer layout version.</summary>
    UnsupportedManifest,

    /// <summary>The manifest's E2EE mode and the supplied key disagree.</summary>
    E2eeKeyMismatch,
}

/// <summary>
/// The outcome of one <see cref="SyncEngine.RunAsync"/> cycle: per-action counts
/// plus the error list (English diagnostics for the log; Presentation maps them
/// or the abort kind to localized wording). All counts are per-entry actions —
/// a DELETE_REMOTE entry counts once even though it writes a tombstone AND
/// deletes a container.
/// </summary>
public sealed record SyncCycleResult
{
    /// <summary>Containers or tombstone files pushed to the cloud.</summary>
    public required int Uploaded { get; init; }

    /// <summary>Entries applied into the local database from the cloud.</summary>
    public required int Downloaded { get; init; }

    /// <summary>Remote deletions performed for locally tombstoned entries.</summary>
    public required int DeletedRemote { get; init; }

    /// <summary>Cloud tombstones applied onto local rows (live-apply or re-stamp).</summary>
    public required int TombstonesApplied { get; init; }

    /// <summary>Conflict copies created (notes only — PRD says note-level LWW).</summary>
    public required int ConflictCopies { get; init; }

    public SyncCycleAbortKind AbortKind { get; init; } = SyncCycleAbortKind.None;

    /// <summary>Per-entry English diagnostics; the cycle continues past them.</summary>
    public required IReadOnlyList<string> Errors { get; init; }

    public bool Ok => AbortKind == SyncCycleAbortKind.None && Errors.Count == 0;

    public static SyncCycleResult Aborted(SyncCycleAbortKind kind, string reason) => new()
    {
        Uploaded = 0,
        Downloaded = 0,
        DeletedRemote = 0,
        TombstonesApplied = 0,
        ConflictCopies = 0,
        AbortKind = kind,
        Errors = [reason],
    };
}
