using Microsoft.Extensions.Logging;
using QNote.Data;
using QNote.Models;

namespace QNote.Services;

/// <summary>
/// Note business logic. Skeleton: read path delegates to the repository (proving
/// the service -> repository -> SQLite wiring); create/delete land in the notes task.
/// </summary>
public sealed class NoteService : INoteService
{
    private readonly INoteRepository _repo;
    private readonly ILogger<NoteService> _log;

    public NoteService(INoteRepository repo, ILogger<NoteService> log)
    {
        _repo = repo;
        _log = log;
    }

    public Task<IReadOnlyList<Note>> GetAllAsync(CancellationToken ct = default) => _repo.GetAllAsync(ct);

    public Task<Note> CreateAsync(string title, string category, CancellationToken ct = default) =>
        throw new NotImplementedException("TODO(notes-task): implement note creation.");

    public Task DeleteAsync(long id, CancellationToken ct = default) =>
        throw new NotImplementedException("TODO(notes-task): implement note deletion.");
}
