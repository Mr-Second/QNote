namespace QNote.Models;

/// <summary>A note category (tab). Linked to notes by <see cref="Name"/>.</summary>
public sealed record Category
{
    public long Id { get; init; }

    public required string Name { get; init; }

    /// <summary>Built-in glyph key (Segoe Fluent Icons hex, e.g. <c>E821</c>).</summary>
    public string IconKey { get; init; } = string.Empty;

    /// <summary>Accent color as <c>#RRGGBB</c>; empty = theme accent.</summary>
    public string Color { get; init; } = string.Empty;

    public int SortOrder { get; init; }
}
