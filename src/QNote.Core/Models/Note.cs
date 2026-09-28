namespace QNote.Models;

/// <summary>
/// A sticky note. <see cref="Content"/> holds the Markdown source (schema v6,
/// Markdown-storage B2); the editor renders it through the MD→RTF conversion layer.
/// Timestamps are stored UTC and presented local.
/// </summary>
public sealed record Note
{
    public long Id { get; init; }

    /// <summary>Stable sync identity, generated eagerly at creation.</summary>
    public required string Uuid { get; init; }

    public string Title { get; init; } = string.Empty;

    /// <summary>Markdown document text (schema v6).</summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>
    /// Plain-text projection of <see cref="Content"/>, derived by
    /// <c>NoteService.UpdateAsync</c> on every save. Source for list previews and
    /// the FTS corpus (keeps Markdown syntax out of both).
    /// </summary>
    public string PlainText { get; init; } = string.Empty;

    /// <summary>Category name-link (matches the Qt build; no FK).</summary>
    public string Category { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}
