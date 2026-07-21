namespace QNote.Models;

/// <summary>A note category (tab). Linked to notes by <see cref="Name"/>.</summary>
public sealed record Category
{
    public long Id { get; init; }

    public required string Name { get; init; }

    /// <summary>Built-in glyph key or a <c>custom:&lt;md5&gt;.&lt;ext&gt;</c> image reference.</summary>
    public string IconKey { get; init; } = string.Empty;

    public int SortOrder { get; init; }
}
