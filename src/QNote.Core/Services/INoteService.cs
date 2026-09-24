using QNote.Models;

namespace QNote.Services;

/// <summary>Note business logic (port of the Qt NoteController).</summary>
public interface INoteService
{
    /// <summary>All notes as full records (incl. Content), newest-updated first.</summary>
    Task<IReadOnlyList<Note>> GetAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Lightweight list projection for the note list (no full Content).
    /// <paramref name="category"/> scopes the list SQL-side (<c>null</c> = "全部").
    /// </summary>
    Task<IReadOnlyList<NoteSummary>> GetSummariesAsync(string? category = null, CancellationToken ct = default);

    /// <summary>The full note (incl. Content) by id, or <c>null</c> if it does not exist.</summary>
    Task<Note?> GetByIdAsync(long id, CancellationToken ct = default);

    /// <summary>
    /// Creates a new blank note in <paramref name="category"/> (empty title/content),
    /// generating its Uuid and timestamps, and returns it with the assigned Id.
    /// </summary>
    Task<Note> CreateAsync(string category = "", CancellationToken ct = default);

    /// <summary>Persists edits, stamping <see cref="Note.UpdatedAt"/> = now; returns the saved note.</summary>
    Task<Note> UpdateAsync(Note note, CancellationToken ct = default);

    /// <summary>Deletes a note by id and prunes any original images no longer referenced.</summary>
    Task DeleteAsync(long id, CancellationToken ct = default);

    /// <summary>Links a note to its originals (called at import time, when metadata is known).</summary>
    Task AddNoteImagesAsync(long noteId, IReadOnlyList<NoteImage> images, CancellationToken ct = default);

    /// <summary>
    /// Reconciles a note's <c>note_images</c> rows with the images its current RTF
    /// actually references (parsed from the <c>qnote:&lt;sha256&gt;</c> alt marker) and
    /// deletes originals that became unreferenced by any note.
    /// </summary>
    Task SyncNoteImagesAsync(long noteId, string? rtf, CancellationToken ct = default);
}
