namespace QNote.Sync;

/// <summary>
/// The 48-byte plaintext header every .qnote container starts with (PRD decision
/// 10): identity plus the LWW clock, and nothing else. <see
/// cref="QnoteContainerCodec.ReadHeader"/> parses it without touching the payload,
/// so the sync engine can judge LWW from a 48-byte <c>Range: bytes=0-47</c> fetch.
/// Timestamps are raw epoch-ms (0 = "not set"); the DeletedAtMs == 0 test is the
/// liveness check.
/// </summary>
public sealed record QnoteHeader
{
    /// <summary>Note/category identity; raw 16 bytes on the wire (Guid layout).</summary>
    public required Guid Uuid { get; init; }

    public required long CreatedAtMs { get; init; }

    /// <summary>Epoch-ms LWW clock. Soft deletes must bump it to the delete instant.</summary>
    public required long UpdatedAtMs { get; init; }

    /// <summary>Epoch-ms soft-delete instant; 0 = live.</summary>
    public required long DeletedAtMs { get; init; }
}
