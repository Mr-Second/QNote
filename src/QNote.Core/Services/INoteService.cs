using QNote.Models;

namespace QNote.Services;

/// <summary>Note business logic (port of the Qt NoteController).</summary>
public interface INoteService
{
    Task<IReadOnlyList<Note>> GetAllAsync(CancellationToken ct = default);

    Task<Note> CreateAsync(string title, string category, CancellationToken ct = default);

    Task DeleteAsync(long id, CancellationToken ct = default);
}
