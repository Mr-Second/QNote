namespace QNote.Sync;

/// <summary>Which side a resolved conflict/tie keeps.</summary>
public enum ConflictWinner
{
    Local,
    Cloud,
}

/// <summary>
/// The LWW tiebreak (L1 step 4, user ruling 2026-10-21 recorded as PRD decision
/// 21). The engine's matrix already orders every STRICT case (C &gt; L download,
/// C &lt; L upload, both-moved conflict on C != L) — plain timestamps cannot
/// order exactly one case: a TIE. Two peers writing different content inside the
/// same millisecond (or under clock skew) both stamp UpdatedAt = T, and the
/// engine only notices because the cloud ETag drifted off the recorded agreement.
///
/// The rule: each side digests its own content and the peer's; the
/// lexicographically LARGER hex digest wins. Both peers compute the same two
/// digests, so both reach the SAME verdict without communicating — the loser
/// becomes a conflict copy (notes; PRD note-level LWW) or is discarded
/// (categories, plain LWW by design).
/// </summary>
public static class ConflictResolver
{
    /// <summary>
    /// Deterministic tiebreak over content digests (lowercase hex sha256).
    /// Equal digests mean identical content — the caller must have already
    /// handled that; <see cref="ConflictWinner.Cloud"/> keeps the resolution
    /// total and deterministic anyway.
    /// </summary>
    public static ConflictWinner Resolve(string localDigest, string cloudDigest) =>
        string.Compare(localDigest, cloudDigest, StringComparison.Ordinal) > 0
            ? ConflictWinner.Local
            : ConflictWinner.Cloud;
}
