using Microsoft.Extensions.Logging;
using QNote.Data;
using QNote.Markdown;
using QNote.Models;

namespace QNote.Services;

/// <summary>
/// Note business logic. Owns creation identity (Uuid) and timestamp stamping;
/// delegates all SQL to <see cref="INoteRepository"/>.
/// </summary>
public sealed class NoteService : INoteService
{
    private readonly INoteRepository _repo;
    private readonly IImageService _images;
    private readonly ILogger<NoteService> _log;

    public NoteService(INoteRepository repo, IImageService images, ILogger<NoteService> log)
    {
        _repo = repo;
        _images = images;
        _log = log;
    }

    public Task<IReadOnlyList<Note>> GetAllAsync(CancellationToken ct = default) => _repo.GetAllAsync(ct);

    public Task<IReadOnlyList<NoteSummary>> GetSummariesAsync(string? category = null, CancellationToken ct = default) =>
        _repo.GetSummariesAsync(category, ct);

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
        // PlainText is derived from the Markdown content here — a single authority,
        // so every save path (editor save, VM, future importers) projects identically.
        var updated = note with
        {
            PlainText = MarkdownText.ToPlainText(note.Content),
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await _repo.UpdateAsync(updated, ct);
        return updated;
    }

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        var orphans = await _repo.DeleteAsync(id, ct);
        await _images.DeleteOriginalsAsync(orphans, ct);
        _log.LogInformation("Deleted note {Id} (pruned {Orphans} orphaned original image(s))", id, orphans.Count);
    }

    public Task AddNoteImagesAsync(long noteId, IReadOnlyList<NoteImage> images, CancellationToken ct = default) =>
        _repo.AddNoteImagesAsync(noteId, images, ct);

    public async Task SyncNoteImagesAsync(long noteId, string? markdown, CancellationToken ct = default)
    {
        var referenced = MarkdownParser.ReferencedImageShas(markdown ?? string.Empty)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // Adopt images that arrived via paste/copy rather than import: they carry a
        // qnote-img:<sha> reference but may have no row for THIS note. Reuse metadata
        // (incl. the display copy) another note already recorded, or a minimal row
        // when only the original file is on disk.
        var existing = (await _repo.GetNoteImagesAsync(noteId, ct)).Select(i => i.Sha256)
            .ToHashSet(StringComparer.Ordinal);
        var missing = referenced.Where(sha => !existing.Contains(sha)).ToList();
        if (missing.Count > 0)
        {
            var known = await _repo.GetImageMetadataByShaAsync(missing, ct);
            var toAdd = new List<NoteImage>();
            foreach (var sha in missing)
            {
                if (known.TryGetValue(sha, out var meta))
                {
                    toAdd.Add(meta with { NoteId = noteId, CreatedAt = DateTimeOffset.UtcNow });
                }
                else if (_images.FindOriginalPath(sha) is { } path)
                {
                    toAdd.Add(new NoteImage
                    {
                        NoteId = noteId,
                        Sha256 = sha,
                        Ext = Path.GetExtension(path).TrimStart('.'),
                        ByteSize = new FileInfo(path).Length,
                        CreatedAt = DateTimeOffset.UtcNow,
                    });
                }
            }
            await _repo.AddNoteImagesAsync(noteId, toAdd, ct);
        }

        var orphans = await _repo.SyncNoteImagesAsync(noteId, referenced, ct);
        await _images.DeleteOriginalsAsync(orphans, ct);
    }

    public Task<IReadOnlyList<NoteImage>> GetNoteImagesWithDisplayAsync(long noteId, CancellationToken ct = default) =>
        _repo.GetNoteImagesWithDisplayAsync(noteId, ct);
}
