using Microsoft.Extensions.Logging;
using QNote.Data;
using QNote.Models;

namespace QNote.Services;

/// <summary>
/// Note business logic. Owns creation identity (Uuid) and timestamp stamping;
/// delegates all SQL to <see cref="INoteRepository"/>.
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

    public Task<IReadOnlyList<NoteSummary>> GetSummariesAsync(CancellationToken ct = default) =>
        _repo.GetSummariesAsync(ct);

    public Task<Note?> GetByIdAsync(long id, CancellationToken ct = default) => _repo.GetByIdAsync(id, ct);

    public async Task<Note> CreateAsync(string category = "", CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var note = new Note
        {
            Uuid = Guid.NewGuid().ToString("N"),
            Title = string.Empty,
            Content = string.Empty,
            Category = category,
            CreatedAt = now,
            UpdatedAt = now,
        };

        var created = await _repo.CreateAsync(note, ct);
        _log.LogInformation("Created note {Id} (category '{Category}')", created.Id, category);
        return created;
    }

    public async Task<Note> UpdateAsync(Note note, CancellationToken ct = default)
    {
        var updated = note with { UpdatedAt = DateTimeOffset.UtcNow };
        await _repo.UpdateAsync(updated, ct);
        return updated;
    }

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        await _repo.DeleteAsync(id, ct);
        _log.LogInformation("Deleted note {Id}", id);
    }
}
