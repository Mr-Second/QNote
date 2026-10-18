using QNote.Models;

namespace QNote.Data;

/// <summary>
/// Persistence for notes and categories over the single <c>qnote.db</c>. The
/// repository maintains the <c>notes_fts</c> FTS5 shadow inside the same write
/// transaction as the notes mutation, so the table and its index stay consistent
/// without a separate indexing service (parity-map §6). Since schema v8 every read
/// path excludes soft-deleted rows (<c>DeletedAt</c> set) and every delete is a
/// tombstone write; timestamps are millisecond-epoch integers in storage.
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
    /// Soft-deletes a note by id (no-op if it does not exist): stamps
    /// <c>DeletedAt</c> AND <c>UpdatedAt</c> with one instant (so a peer's LWW can
    /// never treat the deletion as stale and resurrect the row) and drops the FTS
    /// shadow row. The row itself stays until a tombstone purge.
    /// Performs NO image GC: the originals stay on disk while the tombstone exists.
    /// Returns an empty list (the return type is kept for the hard-delete variants).
    /// </summary>
    Task<IReadOnlyList<string>> DeleteAsync(long id, CancellationToken ct = default);

    /// <summary>
    /// Sync import primitive (schema v8): upserts a FULL note row keyed by
    /// <see cref="Note.Uuid"/>, preserving every timestamp — including
    /// <see cref="Note.DeletedAt"/> — exactly as supplied, in ONE transaction, and
    /// keeps <c>notes_fts</c> in step (indexed when live, removed when a tombstone).
    /// The local <see cref="Note.Id"/> is preserved on conflict.
    /// Unlike <see cref="CreateAsync"/>/<see cref="UpdateAsync"/>, which exist for
    /// local editing and re-stamp identity and time, this is the writer for
    /// replicated rows: re-applying the same input is idempotent.
    /// </summary>
    Task<Note> ImportNoteAsync(Note note, CancellationToken ct = default);

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

    /// <summary>All categories ordered by <see cref="Category.SortOrder"/> (incl. <see cref="Category.Uuid"/>).</summary>
    Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken ct = default);

    /// <summary>
    /// Inserts a category with <c>SortOrder = MAX(SortOrder)+1</c> and returns it
    /// with the DB-assigned <see cref="Category.Id"/>. Generates the
    /// <see cref="Category.Uuid"/> identity when the caller did not supply one.
    /// </summary>
    Task<Category> CreateCategoryAsync(Category category, CancellationToken ct = default);

    /// <summary>
    /// Updates Name/IconKey/Color of a category. When the name changed, the rename
    /// fans out to <c>notes.Category</c> inside the SAME transaction (name-link,
    /// parity-map §1) so the link cannot drift.
    /// </summary>
    Task UpdateCategoryAsync(Category category, CancellationToken ct = default);

    /// <summary>
    /// Soft-deletes a category and every live note under it (Qt parity: destructive,
    /// caller confirms first) — all tombstones share one instant — and drops the
    /// affected FTS shadow rows, in one transaction. Performs no image GC (see
    /// <see cref="DeleteAsync"/>); returns an empty list.
    /// </summary>
    Task<IReadOnlyList<string>> DeleteCategoryAsync(long id, CancellationToken ct = default);

    /// <summary>Persists a new ordering: <paramref name="orderedIds"/> index → SortOrder (bumps UpdatedAt).</summary>
    Task ReorderCategoriesAsync(IReadOnlyList<long> orderedIds, CancellationToken ct = default);

    /// <summary>Note count per category name (<c>GROUP BY Category</c>; uncategorized notes sit under <c>""</c>).</summary>
    Task<IReadOnlyDictionary<string, int>> CountNotesByCategoryAsync(CancellationToken ct = default);

    /// <summary>Upserts the <c>note_images</c> link rows for a note (dedup on (note_id, sha256)).</summary>
    Task AddNoteImagesAsync(long noteId, IReadOnlyList<NoteImage> images, CancellationToken ct = default);

    /// <summary>
    /// Reconciles a note's <c>note_images</c> rows with the content addresses its
    /// current Markdown actually references: rows for dropped images are deleted,
    /// kept (and still-present) addresses are left alone. Returns the addresses that
    /// became unreferenced by ANY note — the caller prunes those original files.
    /// Metadata for newly referenced addresses must already have been inserted via
    /// <see cref="AddNoteImagesAsync"/> at import time.
    /// </summary>
    Task<IReadOnlyList<string>> SyncNoteImagesAsync(long noteId, IReadOnlyList<string> referencedSha256, CancellationToken ct = default);

    /// <summary>
    /// The original-image links for a note, newest first — WITHOUT
    /// <see cref="NoteImage.DisplayBytes"/> (the sync/prune paths only need the
    /// metadata; blob reads are reserved for the editor load path).
    /// </summary>
    Task<IReadOnlyList<NoteImage>> GetNoteImagesAsync(long noteId, CancellationToken ct = default);

    /// <summary>
    /// Full link rows INCLUDING <see cref="NoteImage.DisplayBytes"/> — the editor
    /// load path (MD→RTF rendering resolves <c>qnote-img:</c> references to these).
    /// </summary>
    Task<IReadOnlyList<NoteImage>> GetNoteImagesWithDisplayAsync(long noteId, CancellationToken ct = default);

    /// <summary>
    /// Known metadata for the given content addresses, from any note that already
    /// references them (used to adopt a pasted image's metadata without a WIC decode).
    /// </summary>
    Task<IReadOnlyDictionary<string, NoteImage>> GetImageMetadataByShaAsync(
        IReadOnlyList<string> sha256, CancellationToken ct = default);

    /// <summary>
    /// Of the given display-copy hashes (SHA256 of <see cref="NoteImage.DisplayBytes"/>),
    /// the ones some <c>note_images</c> row anywhere already carries, mapped to that
    /// row's ORIGINAL content address. Save-time image adoption uses this to recognize
    /// a pasted image as another note's display copy and relink the shared original
    /// instead of importing a duplicate (task 10-03). Full-table scan — call it only
    /// when unlinked images exist; a stored display_sha256 column is the future
    /// optimization, not a schema change now.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> FindOriginalShaByDisplayHashAsync(
        IReadOnlyList<string> displaySha256, CancellationToken ct = default);
}
