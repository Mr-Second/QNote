namespace QNote.Services;

/// <summary>Classification of category CRUD failures, for localized UI messages.</summary>
public enum CategoryErrorKind
{
    /// <summary>The category name is blank / whitespace.</summary>
    BlankName,

    /// <summary>Another category with the same name already exists.</summary>
    DuplicateName,

    /// <summary>The category id no longer exists (deleted concurrently).</summary>
    NotFound,

    /// <summary>A seeded built-in category cannot be renamed.</summary>
    BuiltInRename,

    /// <summary>A seeded built-in category cannot be deleted.</summary>
    BuiltInDelete,

    /// <summary>The color is not a #RRGGBB hex string.</summary>
    InvalidColor,
}

/// <summary>
/// An expected, user-facing category failure (mirrors <see cref="BackupException"/>:
/// expected outcomes are modeled as typed exceptions, not buried in generic ones).
/// <see cref="Kind"/> tells Presentation which localized message to show; the
/// technical detail is logged via <see cref="Exception.Message"/>. Messages are
/// English diagnostics — Core never owns user-visible wording.
/// </summary>
public sealed class CategoryException : Exception
{
    public CategoryException(CategoryErrorKind kind, string message, string? name = null)
        : base(message)
    {
        Kind = kind;
        Name = name;
    }

    public CategoryErrorKind Kind { get; }

    /// <summary>
    /// Category name the failure refers to (duplicates / built-ins), so
    /// Presentation can interpolate it into the localized message.
    /// </summary>
    public string? Name { get; }
}
