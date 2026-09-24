namespace QNote.Models;

/// <summary>
/// Links a note to an original image stored content-addressed on disk
/// (<c>%LocalAppData%\QNote\images\&lt;sha256&gt;.&lt;ext&gt;</c>). One row per (note, sha256);
/// the same sha256 can be shared by several notes (content-addressed dedup, PRD D2).
/// The <c>note_images</c> table cascades on note delete, so a row never outlives its note.
/// </summary>
public sealed record NoteImage
{
    public long NoteId { get; init; }

    /// <summary>Lowercase hex sha256 of the original bytes (the content address).</summary>
    public required string Sha256 { get; init; }

    /// <summary>Original file extension without the dot (e.g. <c>png</c>).</summary>
    public string Ext { get; init; } = string.Empty;

    /// <summary>Original byte size.</summary>
    public long ByteSize { get; init; }

    /// <summary>Original pixel dimensions.</summary>
    public int Width { get; init; }

    public int Height { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}
