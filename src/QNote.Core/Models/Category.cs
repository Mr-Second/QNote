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

    /// <summary>
    /// Stable sync identity (schema v7). Generated at insert by the repository;
    /// pre-v7 rows were backfilled by the migration. Identity — never rewritten.
    /// </summary>
    public string Uuid { get; init; } = string.Empty;

    /// <summary>Creation instant (schema v8, epoch-ms in storage).</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Last mutation instant (schema v8) — the LWW key for a category row.</summary>
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Soft-delete tombstone (schema v8): <c>null</c> = live.</summary>
    public DateTimeOffset? DeletedAt { get; init; }
}
