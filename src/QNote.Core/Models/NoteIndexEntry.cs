namespace QNote.Models;

/// <summary>
/// Lightweight index projection of a <see cref="Note"/> for FTS full rebuilds -
/// only the FTS corpus fields (Title, PlainText) plus the rowid. Excludes RTF
/// <see cref="Note.Content"/> so a rebuild stays cheap.
/// </summary>
public sealed record NoteIndexEntry
{
    public long Id { get; init; }

    public string Title { get; init; } = string.Empty;

    public string PlainText { get; init; } = string.Empty;
}
