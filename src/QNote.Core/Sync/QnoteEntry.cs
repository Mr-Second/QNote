namespace QNote.Sync;

/// <summary>The two binary payload kinds a container entry can carry.</summary>
public enum QnoteEntryKind : byte
{
    Image = 1,
    Attachment = 2,
}

/// <summary>
/// One binary payload inside the container's binary region (PRD decision 10): an
/// inline image or a todo attachment, content-addressed by its sha256 — the
/// string form of <see cref="Data"/>'s digest, and the codec enforces that pair
/// both ways. The on-disk <c>[offset][length]</c> fields are a wire detail the
/// codec computes on write and validates (entries must tile the region) on read;
/// callers only ever see the bytes.
/// </summary>
public sealed record QnoteEntry
{
    public required QnoteEntryKind Kind { get; init; }

    /// <summary>64 hex characters, any case accepted on write, lowercase on read.</summary>
    public required string Sha256 { get; init; }

    /// <summary>Original file name. Empty for images (their display name lives in the DB).</summary>
    public required string FileName { get; init; }

    public required byte[] Data { get; init; }
}
