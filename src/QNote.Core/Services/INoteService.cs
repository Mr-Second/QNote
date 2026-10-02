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
    /// Reconciles a note's <c>note_images</c> rows with the images its current
    /// Markdown actually references (<c>![alt](qnote-img:&lt;sha256&gt;)</c>) and
    /// deletes originals that became unreferenced by any note. Images referenced
    /// without a row for this note are adopted from other notes' metadata (or the
    /// original file on disk) so pasted copies stay linked.
    /// </summary>
    Task SyncNoteImagesAsync(long noteId, string? markdown, CancellationToken ct = default);

    /// <summary>
    /// Full image-link rows including display bytes — the editor load path (MD→RTF
    /// rendering resolves <c>qnote-img:</c> references to these).
    /// </summary>
    Task<IReadOnlyList<NoteImage>> GetNoteImagesWithDisplayAsync(long noteId, CancellationToken ct = default);

    /// <summary>
    /// Known metadata (INCLUDING <see cref="NoteImage.DisplayBytes"/>) for the given
    /// content addresses, from any note that references them — the row-clone source
    /// for save-time adoption of a pasted image (no WIC decode needed).
    /// </summary>
    Task<IReadOnlyDictionary<string, NoteImage>> GetImageMetadataByShaAsync(
        IReadOnlyList<string> sha256, CancellationToken ct = default);

    /// <summary>
    /// Of the given display-copy hashes, the ones some <c>note_images</c> row already
    /// carries, mapped to the row's original content address — save-time adoption
    /// uses this to relink a cross-note pasted image to the shared original
    /// (task 10-03). Full-table scan; call only when unlinked images exist.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> FindOriginalShaByDisplayHashAsync(
        IReadOnlyList<string> displaySha256, CancellationToken ct = default);
}
