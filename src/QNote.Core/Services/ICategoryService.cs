using QNote.Models;

namespace QNote.Services;

/// <summary>Category management (port of the Qt CategoryManager).</summary>
public interface ICategoryService
{
    /// <summary>All categories ordered by <see cref="Category.SortOrder"/>.</summary>
    Task<IReadOnlyList<Category>> GetAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Creates a category (appended at the end of the order). Validates the name
    /// (non-empty, unique) at the service boundary.
    /// </summary>
    Task<Category> CreateAsync(string name, string color, string iconKey, CancellationToken ct = default);

    /// <summary>
    /// Updates name/color/icon of a category; a name change fans out to
    /// <c>notes.Category</c> in the same transaction. Built-in categories reject
    /// renames (color/icon edits are allowed).
    /// </summary>
    Task UpdateAsync(Category category, CancellationToken ct = default);

    /// <summary>
    /// Deletes a category AND all its notes (Qt parity: destructive). Built-in
    /// categories cannot be deleted.
    /// </summary>
    Task DeleteAsync(long id, CancellationToken ct = default);

    /// <summary>Persists a new sidebar ordering (index → SortOrder).</summary>
    Task ReorderAsync(IReadOnlyList<long> orderedIds, CancellationToken ct = default);

    /// <summary>Note count per category name (uncategorized notes sit under <c>""</c>).</summary>
    Task<IReadOnlyDictionary<string, int>> CountNotesAsync(CancellationToken ct = default);

    /// <summary>True for the seeded built-in set (工作/生活/重要) — no rename/delete.</summary>
    bool IsBuiltIn(string name);
}
