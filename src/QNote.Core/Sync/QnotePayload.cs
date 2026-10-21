namespace QNote.Sync;

/// <summary>
/// A decoded .qnote container: the header (identity + LWW clock) plus the body
/// and binary entries. Round-trips with <see cref="QnoteContainerCodec.Encode"/>
/// (modulo the DEFLATE/nonce bytes, which are not part of the record).
/// </summary>
public sealed record QnotePayload
{
    public required QnoteHeader Header { get; init; }

    /// <summary>The note's markdown body (UTF-8 on the wire).</summary>
    public required string Body { get; init; }

    public required IReadOnlyList<QnoteEntry> Entries { get; init; }
}
