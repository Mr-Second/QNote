using QNote.Models;

namespace QNote.Data;

/// <summary>
/// Persistence for notes and categories over the single <c>qnote.db</c>.
/// Services depend on this interface, never on raw connections.
/// </summary>
public interface INoteRepository
{
    Task<IReadOnlyList<Note>> GetAllAsync(CancellationToken ct = default);

    Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken ct = default);
}
