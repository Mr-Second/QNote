using QNote.Models;

namespace QNote.Data;

/// <summary>
/// Persistence for notes and categories over the single <c>qnote.db</c>. The
/// repository maintains the <c>notes_fts</c> FTS5 shadow inside the same write
/// transaction as the notes mutation, so the table and its index stay consistent
/// without a separate indexing service (parity-map §6).
/// Services depend on this interface, never on raw connections.
/// </summary>
public interface INoteRepository
{
    /// <summary>All notes as full records (incl. Content), newest-updated first.</summary>
    Task<IReadOnlyList<Note>> GetAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Lightweight list projection (no full Content, only a short preview snippet),
    /// newest-updated first. Use for the note list; fetch full content via
    /// <see cref="GetByIdAsync"/> when a note is selected. When
    /// <paramref name="category"/> is given, only notes of that category are returned
    /// (SQL-side filter; <c>null</c> = the synthetic "全部" scope, no WHERE).
    /// </summary>
    Task<IReadOnlyList<NoteSummary>> GetSummariesAsync(string? category = null, CancellationToken ct = default);

    /// <summary>The full note (incl. Content) by id, or <c>null</c> if it does not exist.</summary>
    Task<Note?> GetByIdAsync(long id, CancellationToken ct = default);

    /// <summary>Inserts a new note and returns it with the DB-assigned <see cref="Note.Id"/>.</summary>
    Task<Note> CreateAsync(Note note, CancellationToken ct = default);

    /// <summary>Updates Title/Content/Category/UpdatedAt of an existing note by id.</summary>
    Task UpdateAsync(Note note, CancellationToken ct = default);

    /// <summary>
    /// Deletes a note by id (no-op if it does not exist). Returns the content addresses
    /// (<c>note_images.sha256</c>) that are no longer referenced by ANY note after the
    /// delete — the caller prunes those original files from disk.
    /// </summary>
    Task<IReadOnlyList<string>> DeleteAsync(long id, CancellationToken ct = default);

    /// <summary>
    /// Full index projection of every note (Id, Title, PlainText) for an FTS full
    /// rebuild. Excludes RTF Content; caller is responsible for bigram tokenization.
    /// </summary>
    Task<IReadOnlyList<NoteIndexEntry>> GetAllForIndexAsync(CancellationToken ct = default);

    /// <summary>
    /// Uuid → (Id, UpdatedAt) for every note. Used by the backup restore pipeline
    /// for conflict analysis and merge decisions (notes carry no other identity
    /// across databases).
    /// </summary>
    Task<IReadOnlyDictionary<string, (long Id, DateTimeOffset UpdatedAt)>> GetUuidTimestampsAsync(CancellationToken ct = default);

    /// <summary>All categories ordered by <see cref="Category.SortOrder"/>.</summary>
    Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken ct = default);

    /// <summary>
    /// Inserts a category with <c>SortOrder = MAX(SortOrder)+1</c> and returns it
    /// with the DB-assigned <see cref="Category.Id"/>.
    /// </summary>
    Task<Category> CreateCategoryAsync(Category category, CancellationToken ct = default);

    /// <summary>
    /// Updates Name/IconKey/Color of a category. When the name changed, the rename
    /// fans out to <c>notes.Category</c> inside the SAME transaction (name-link,
    /// parity-map §1) so the link cannot drift.
    /// </summary>
    Task UpdateCategoryAsync(Category category, CancellationToken ct = default);

    /// <summary>
    /// Deletes a category AND all its notes (Qt parity: destructive, caller confirms
    /// first) plus their FTS shadow rows — all inside one transaction. Returns the
    /// content addresses no longer referenced by any note (for original-file pruning).
    /// </summary>
    Task<IReadOnlyList<string>> DeleteCategoryAsync(long id, CancellationToken ct = default);

    /// <summary>Persists a new ordering: <paramref name="orderedIds"/> index → SortOrder.</summary>
    Task ReorderCategoriesAsync(IReadOnlyList<long> orderedIds, CancellationToken ct = default);

    /// <summary>Note count per category name (<c>GROUP BY Category</c>; uncategorized notes sit under <c>""</c>).</summary>
    Task<IReadOnlyDictionary<string, int>> CountNotesByCategoryAsync(CancellationToken ct = default);

    /// <summary>Upserts the <c>note_images</c> link rows for a note (dedup on (note_id, sha256)).</summary>
    Task AddNoteImagesAsync(long noteId, IReadOnlyList<NoteImage> images, CancellationToken ct = default);

    /// <summary>
    /// Reconciles a note's <c>note_images</c> rows with the content addresses its
    /// current RTF actually references: rows for dropped images are deleted, kept
    /// (and still-present) addresses are left alone. Returns the addresses that became
    /// unreferenced by ANY note — the caller prunes those original files. Metadata for
    /// newly referenced addresses must already have been inserted via
    /// <see cref="AddNoteImagesAsync"/> at import time.
    /// </summary>
    Task<IReadOnlyList<string>> SyncNoteImagesAsync(long noteId, IReadOnlyList<string> referencedSha256, CancellationToken ct = default);

    /// <summary>The original-image links for a note, newest first.</summary>
    Task<IReadOnlyList<NoteImage>> GetNoteImagesAsync(long noteId, CancellationToken ct = default);

    /// <summary>
    /// Known metadata for the given content addresses, from any note that already
    /// references them (used to adopt a pasted image's metadata without a WIC decode).
    /// </summary>
    Task<IReadOnlyDictionary<string, NoteImage>> GetImageMetadataByShaAsync(
        IReadOnlyList<string> sha256, CancellationToken ct = default);
}
