namespace QNote.Models;

/// <summary>
/// A sticky note. <see cref="Content"/> holds RTF (parity-map decision ④:
/// RichEditBox + RTF). Timestamps are stored UTC and presented local.
/// </summary>
public sealed record Note
{
    public long Id { get; init; }

    /// <summary>Stable sync identity, generated eagerly at creation.</summary>
    public required string Uuid { get; init; }

    public string Title { get; init; } = string.Empty;

    /// <summary>RTF document text.</summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>Category name-link (matches the Qt build; no FK).</summary>
    public string Category { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}
