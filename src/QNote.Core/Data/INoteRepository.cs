using QNote.Models;

namespace QNote.Data;

/// <summary>
/// Persistence for notes and categories over the single <c>qnote.db</c>.
/// Services depend on this interface, never on raw connections.
/// </summary>
public interface INoteRepository
{
    /// <summary>All notes as full records (incl. Content), newest-updated first.</summary>
    Task<IReadOnlyList<Note>> GetAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Lightweight list projection (no full Content, only a short preview snippet),
    /// newest-updated first. Use for the note list; fetch full content via
    /// <see cref="GetByIdAsync"/> when a note is selected.
    /// </summary>
    Task<IReadOnlyList<NoteSummary>> GetSummariesAsync(CancellationToken ct = default);

    /// <summary>The full note (incl. Content) by id, or <c>null</c> if it does not exist.</summary>
    Task<Note?> GetByIdAsync(long id, CancellationToken ct = default);

    /// <summary>Inserts a new note and returns it with the DB-assigned <see cref="Note.Id"/>.</summary>
    Task<Note> CreateAsync(Note note, CancellationToken ct = default);

    /// <summary>Updates Title/Content/Category/UpdatedAt of an existing note by id.</summary>
    Task UpdateAsync(Note note, CancellationToken ct = default);

    /// <summary>Deletes a note by id (no-op if it does not exist).</summary>
    Task DeleteAsync(long id, CancellationToken ct = default);

    Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken ct = default);
}
