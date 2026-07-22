namespace QNote.Models;

/// <summary>
/// Lightweight list projection of a <see cref="Note"/> for the note list. Excludes
/// the full <see cref="Note.Content"/> — only a short <see cref="Preview"/> snippet —
/// so loading the list stays cheap. Full content is fetched on demand via
/// <c>INoteRepository.GetByIdAsync</c> when a note is selected.
/// </summary>
public sealed record NoteSummary
{
    public long Id { get; init; }

    /// <summary>Stable sync identity (mirrors <see cref="Note.Uuid"/>).</summary>
    public required string Uuid { get; init; }

    public string Title { get; init; } = string.Empty;

    /// <summary>Leading snippet of the content (plain text this slice; RTF later revisits extraction).</summary>
    public string Preview { get; init; } = string.Empty;

    /// <summary>Category name-link (matches the Qt build; no FK).</summary>
    public string Category { get; init; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; init; }
}
