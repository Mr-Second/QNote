using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using QNote.Models;
using QNote.Text;

namespace QNote.Data;

/// <summary>
/// SQLite-backed note/category persistence over the single <c>qnote.db</c>. All
/// queries are parameterized and every connection/command/reader is disposed via
/// <c>await using</c> (see database-guidelines). Timestamps are stored as ISO-8601
/// UTC and read back as UTC. The <c>notes_fts</c> FTS5 shadow is maintained in the
/// SAME transaction as each notes mutation (delete-by-rowid + insert on upsert),
/// so the table and its index cannot drift.
/// </summary>
public sealed class NoteRepository : INoteRepository
{
    /// <summary>Max characters of PlainText projected into a list <see cref="NoteSummary.Preview"/>.</summary>
    private const int PreviewLength = 120;

    private const DateTimeStyles UtcStyles =
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;

    private readonly DbConnectionFactory _factory;

    public NoteRepository(DbConnectionFactory factory) => _factory = factory;

    public async Task<IReadOnlyList<Note>> GetAllAsync(CancellationToken ct = default)
    {
        await using var conn = _factory.OpenRead();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT Id, Uuid, Title, Category, CreatedAt, UpdatedAt, PlainText, Content FROM notes ORDER BY UpdatedAt DESC;";
        var list = new List<Note>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(MapNote(reader));

        return list;
    }

    public async Task<IReadOnlyList<NoteSummary>> GetSummariesAsync(string? category = null, CancellationToken ct = default)
    {
        await using var conn = _factory.OpenRead();
        await using var cmd = conn.CreateCommand();
        var hasCategory = !string.IsNullOrEmpty(category);
        cmd.CommandText =
            "SELECT Id, Uuid, Title, substr(PlainText, 1, $len) AS Preview, Category, CreatedAt, UpdatedAt " +
            "FROM notes " + (hasCategory ? "WHERE Category = $cat " : "") +
            "ORDER BY UpdatedAt DESC;";
        cmd.Parameters.AddWithValue("$len", PreviewLength);
        if (hasCategory)
            cmd.Parameters.AddWithValue("$cat", category);

        var list = new List<NoteSummary>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new NoteSummary
            {
                Id = reader.GetInt64(0),
                Uuid = reader.GetString(1),
                Title = reader.GetString(2),
                // U+FFFC (RichEdit image placeholder) renders as "obj" boxes in the
                // list — previews are text-only, strip it at projection time.
                Preview = reader.GetString(3).Replace("￼", string.Empty),
                Category = reader.GetString(4),
                CreatedAt = ParseUtc(reader.GetString(5)),
                UpdatedAt = ParseUtc(reader.GetString(6)),
            });
        }

        return list;
    }

    public async Task<Note?> GetByIdAsync(long id, CancellationToken ct = default)
    {
        await using var conn = _factory.OpenRead();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT Id, Uuid, Title, Category, CreatedAt, UpdatedAt, PlainText, Content FROM notes WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$id", id);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? MapNote(reader) : null;
    }

    public async Task<Note> CreateAsync(Note note, CancellationToken ct = default)
    {
        await using var conn = _factory.OpenWrite();
        await using var tx = conn.BeginTransaction();

        long id;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText =
                "INSERT INTO notes (Uuid, Title, Content, PlainText, Category, CreatedAt, UpdatedAt) " +
                "VALUES ($uuid, $title, $content, $plainText, $category, $createdAt, $updatedAt) " +
                "RETURNING Id;";
            cmd.Parameters.AddWithValue("$uuid", note.Uuid);
            cmd.Parameters.AddWithValue("$title", note.Title);
            cmd.Parameters.AddWithValue("$content", note.Content);
            cmd.Parameters.AddWithValue("$plainText", note.PlainText);
            cmd.Parameters.AddWithValue("$category", note.Category);
            cmd.Parameters.AddWithValue("$createdAt", ToDbString(note.CreatedAt));
            cmd.Parameters.AddWithValue("$updatedAt", ToDbString(note.UpdatedAt));

            var result = await cmd.ExecuteScalarAsync(ct)
                ?? throw new InvalidOperationException("INSERT ... RETURNING Id returned no value.");
            id = (long)result;
        }

        await UpsertFtsAsync(conn, tx, id, note.Title, note.PlainText, ct);
        await tx.CommitAsync(ct);

        return note with { Id = id };
    }

    public async Task UpdateAsync(Note note, CancellationToken ct = default)
    {
        await using var conn = _factory.OpenWrite();
        await using var tx = conn.BeginTransaction();

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText =
                "UPDATE notes SET Title = $title, Content = $content, PlainText = $plainText, Category = $category, UpdatedAt = $updatedAt " +
                "WHERE Id = $id;";
            cmd.Parameters.AddWithValue("$title", note.Title);
            cmd.Parameters.AddWithValue("$content", note.Content);
            cmd.Parameters.AddWithValue("$plainText", note.PlainText);
            cmd.Parameters.AddWithValue("$category", note.Category);
            cmd.Parameters.AddWithValue("$updatedAt", ToDbString(note.UpdatedAt));
            cmd.Parameters.AddWithValue("$id", note.Id);

            await cmd.ExecuteNonQueryAsync(ct);
        }

        await UpsertFtsAsync(conn, tx, note.Id, note.Title, note.PlainText, ct);
        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<string>> DeleteAsync(long id, CancellationToken ct = default)
    {
        await using var conn = _factory.OpenWrite();
        await using var tx = conn.BeginTransaction();

        // Collect this note's content addresses BEFORE the delete; after it, any that
        // no longer appear in note_images are orphaned originals to prune from disk.
        var before = await GetNoteImagesAsync(conn, tx, id, ct);

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText =
                "DELETE FROM notes WHERE Id = $id;\n" +
                "DELETE FROM notes_fts WHERE rowid = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        var orphans = await FindOrphansAsync(conn, tx, before.Select(i => i.Sha256), ct);
        await tx.CommitAsync(ct);
        return orphans;
    }

    public async Task<IReadOnlyList<NoteIndexEntry>> GetAllForIndexAsync(CancellationToken ct = default)
    {
        await using var conn = _factory.OpenRead();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Title, PlainText FROM notes ORDER BY Id;";

        var list = new List<NoteIndexEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new NoteIndexEntry
            {
                Id = reader.GetInt64(0),
                Title = reader.GetString(1),
                PlainText = reader.GetString(2),
            });
        }

        return list;
    }

    public async Task<IReadOnlyDictionary<string, (long Id, DateTimeOffset UpdatedAt)>> GetUuidTimestampsAsync(CancellationToken ct = default)
    {
        await using var conn = _factory.OpenRead();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Uuid, Id, UpdatedAt FROM notes;";

        var map = new Dictionary<string, (long, DateTimeOffset)>(StringComparer.Ordinal);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            map[reader.GetString(0)] = (reader.GetInt64(1), ParseUtc(reader.GetString(2)));
        return map;
    }

    public async Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken ct = default)
    {
        await using var conn = _factory.OpenRead();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Name, IconKey, Color, SortOrder FROM categories ORDER BY SortOrder;";

        var list = new List<Category>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new Category
            {
                Id = reader.GetInt64(0),
                Name = reader.GetString(1),
                IconKey = reader.GetString(2),
                Color = reader.GetString(3),
                SortOrder = reader.GetInt32(4),
            });
        }

        return list;
    }

    public async Task<Category> CreateCategoryAsync(Category category, CancellationToken ct = default)
    {
        await using var conn = _factory.OpenWrite();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO categories (Name, IconKey, Color, SortOrder) " +
            "VALUES ($name, $icon, $color, (SELECT COALESCE(MAX(SortOrder), -1) + 1 FROM categories)) " +
            "RETURNING Id, SortOrder;";
        cmd.Parameters.AddWithValue("$name", category.Name);
        cmd.Parameters.AddWithValue("$icon", category.IconKey);
        cmd.Parameters.AddWithValue("$color", category.Color);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new InvalidOperationException("INSERT INTO categories ... RETURNING returned no row.");
        return category with { Id = reader.GetInt64(0), SortOrder = reader.GetInt32(1) };
    }

    public async Task UpdateCategoryAsync(Category category, CancellationToken ct = default)
    {
        await using var conn = _factory.OpenWrite();
        await using var tx = conn.BeginTransaction();

        string oldName;
        await using (var read = conn.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = "SELECT Name FROM categories WHERE Id = $id;";
            read.Parameters.AddWithValue("$id", category.Id);
            oldName = (string?)(await read.ExecuteScalarAsync(ct))
                ?? throw new InvalidOperationException($"Category {category.Id} does not exist.");
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText =
                "UPDATE categories SET Name = $name, IconKey = $icon, Color = $color WHERE Id = $id;";
            cmd.Parameters.AddWithValue("$name", category.Name);
            cmd.Parameters.AddWithValue("$icon", category.IconKey);
            cmd.Parameters.AddWithValue("$color", category.Color);
            cmd.Parameters.AddWithValue("$id", category.Id);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Name-link fan-out: notes point at the category by NAME, so a rename must
        // move every note in the same transaction (parity-map §1; stronger than the
        // Qt build's app-side fan-out, which could drift on a mid-write crash).
        if (!string.Equals(oldName, category.Name, StringComparison.Ordinal))
        {
            await using var fanOut = conn.CreateCommand();
            fanOut.Transaction = tx;
            fanOut.CommandText = "UPDATE notes SET Category = $new WHERE Category = $old;";
            fanOut.Parameters.AddWithValue("$new", category.Name);
            fanOut.Parameters.AddWithValue("$old", oldName);
            await fanOut.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<string>> DeleteCategoryAsync(long id, CancellationToken ct = default)
    {
        await using var conn = _factory.OpenWrite();
        await using var tx = conn.BeginTransaction();

        string? name;
        await using (var read = conn.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = "SELECT Name FROM categories WHERE Id = $id;";
            read.Parameters.AddWithValue("$id", id);
            name = (string?)(await read.ExecuteScalarAsync(ct));
        }

        if (name is null)
        {
            await tx.CommitAsync(ct);
            return [];
        }

        // Content addresses of every note about to be deleted (before the delete).
        var before = new List<NoteImage>();
        await using (var read = conn.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText =
                "SELECT ni.note_id, ni.sha256, ni.ext, ni.byte_size, ni.width, ni.height, ni.created_at " +
                "FROM note_images ni JOIN notes n ON n.Id = ni.note_id WHERE n.Category = $name;";
            read.Parameters.AddWithValue("$name", name);
            await using var reader = await read.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                before.Add(MapNoteImage(reader));
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            // FTS rows go first (they key off notes.Id), then the notes; note_images
            // cascades on the note delete (foreign_keys=ON). The category row goes last.
            cmd.CommandText =
                "DELETE FROM notes_fts WHERE rowid IN (SELECT Id FROM notes WHERE Category = $name);\n" +
                "DELETE FROM notes WHERE Category = $name;\n" +
                "DELETE FROM categories WHERE Id = $id;";
            cmd.Parameters.AddWithValue("$name", name);
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        var orphans = await FindOrphansAsync(conn, tx, before.Select(i => i.Sha256), ct);
        await tx.CommitAsync(ct);
        return orphans;
    }

    public async Task ReorderCategoriesAsync(IReadOnlyList<long> orderedIds, CancellationToken ct = default)
    {
        await using var conn = _factory.OpenWrite();
        await using var tx = conn.BeginTransaction();

        for (var i = 0; i < orderedIds.Count; i++)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE categories SET SortOrder = $ord WHERE Id = $id;";
            cmd.Parameters.AddWithValue("$ord", i);
            cmd.Parameters.AddWithValue("$id", orderedIds[i]);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyDictionary<string, int>> CountNotesByCategoryAsync(CancellationToken ct = default)
    {
        await using var conn = _factory.OpenRead();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Category, COUNT(*) FROM notes GROUP BY Category;";

        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            map[reader.GetString(0)] = reader.GetInt32(1);

        return map;
    }

    public async Task AddNoteImagesAsync(long noteId, IReadOnlyList<NoteImage> images, CancellationToken ct = default)
    {
        if (images.Count == 0)
            return;

        await using var conn = _factory.OpenWrite();
        await using var tx = conn.BeginTransaction();

        foreach (var image in images)
            await UpsertNoteImageAsync(conn, tx, noteId, image, ct);

        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<string>> SyncNoteImagesAsync(
        long noteId, IReadOnlyList<string> referencedSha256, CancellationToken ct = default)
    {
        await using var conn = _factory.OpenWrite();
        await using var tx = conn.BeginTransaction();

        var existing = await GetNoteImagesAsync(conn, tx, noteId, ct);
        var referenced = referencedSha256.ToHashSet(StringComparer.Ordinal);
        var dropped = existing.Where(i => !referenced.Contains(i.Sha256)).Select(i => i.Sha256).ToList();

        foreach (var sha in dropped)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM note_images WHERE note_id = $id AND sha256 = $sha;";
            cmd.Parameters.AddWithValue("$id", noteId);
            cmd.Parameters.AddWithValue("$sha", sha);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        var orphans = await FindOrphansAsync(conn, tx, dropped, ct);
        await tx.CommitAsync(ct);
        return orphans;
    }

    public async Task<IReadOnlyList<NoteImage>> GetNoteImagesAsync(long noteId, CancellationToken ct = default)
    {
        await using var conn = _factory.OpenRead();
        return await GetNoteImagesAsync(conn, null, noteId, ct);
    }

    public async Task<IReadOnlyDictionary<string, NoteImage>> GetImageMetadataByShaAsync(
        IReadOnlyList<string> sha256, CancellationToken ct = default)
    {
        var map = new Dictionary<string, NoteImage>(StringComparer.Ordinal);
        var distinct = sha256.Distinct(StringComparer.Ordinal).ToList();
        if (distinct.Count == 0)
            return map;

        await using var conn = _factory.OpenRead();
        await using var cmd = conn.CreateCommand();
        var parameters = string.Join(", ", distinct.Select((_, i) => $"$sha{i}"));
        // Includes display_bytes: the paste-adoption path copies this row (with its
        // blob) onto the adopting note, so the copied image renders without a WIC pass.
        cmd.CommandText =
            "SELECT note_id, sha256, ext, byte_size, width, height, created_at, display_bytes " +
            $"FROM note_images WHERE sha256 IN ({parameters});";
        for (var i = 0; i < distinct.Count; i++)
            cmd.Parameters.AddWithValue($"$sha{i}", distinct[i]);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            // Any note's row carries the same content-addressed metadata; first wins.
            map.TryAdd(reader.GetString(1), MapNoteImage(reader, withDisplay: true));
        }
        return map;
    }

    private static async Task<IReadOnlyList<NoteImage>> GetNoteImagesAsync(
        SqliteConnection conn, SqliteTransaction? tx, long noteId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "SELECT note_id, sha256, ext, byte_size, width, height, created_at " +
            "FROM note_images WHERE note_id = $id ORDER BY created_at DESC;";
        cmd.Parameters.AddWithValue("$id", noteId);

        var list = new List<NoteImage>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(MapNoteImage(reader));
        return list;
    }

    public async Task<IReadOnlyList<NoteImage>> GetNoteImagesWithDisplayAsync(
        long noteId, CancellationToken ct = default)
    {
        await using var conn = _factory.OpenRead();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT note_id, sha256, ext, byte_size, width, height, created_at, display_bytes " +
            "FROM note_images WHERE note_id = $id ORDER BY created_at DESC;";
        cmd.Parameters.AddWithValue("$id", noteId);

        var list = new List<NoteImage>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(MapNoteImage(reader, withDisplay: true));
        return list;
    }

    private static async Task UpsertNoteImageAsync(
        SqliteConnection conn, SqliteTransaction tx, long noteId, NoteImage image, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        // DO UPDATE (not DO NOTHING): a re-import of the same (note, sha) refreshes
        // metadata and backfills a display copy onto a legacy blob-less row; the
        // COALESCE never lets a null overwrite bytes we already have.
        cmd.CommandText =
            "INSERT INTO note_images (note_id, sha256, ext, byte_size, width, height, created_at, display_bytes) " +
            "VALUES ($id, $sha, $ext, $size, $w, $h, $created, $display) " +
            "ON CONFLICT(note_id, sha256) DO UPDATE SET " +
            "ext = excluded.ext, byte_size = excluded.byte_size, " +
            "width = excluded.width, height = excluded.height, " +
            "display_bytes = COALESCE(excluded.display_bytes, note_images.display_bytes);";
        cmd.Parameters.AddWithValue("$id", noteId);
        cmd.Parameters.AddWithValue("$sha", image.Sha256);
        cmd.Parameters.AddWithValue("$ext", image.Ext);
        cmd.Parameters.AddWithValue("$size", image.ByteSize);
        cmd.Parameters.AddWithValue("$w", image.Width);
        cmd.Parameters.AddWithValue("$h", image.Height);
        cmd.Parameters.AddWithValue("$created", ToDbString(image.CreatedAt));
        cmd.Parameters.AddWithValue("$display", (object?)image.DisplayBytes ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Of the given candidate addresses, the ones that no longer appear in
    /// <c>note_images</c> for any note — i.e. whose original files can be deleted.
    /// Evaluated inside the caller's transaction so it sees the post-delete state.
    /// </summary>
    private static async Task<IReadOnlyList<string>> FindOrphansAsync(
        SqliteConnection conn, SqliteTransaction tx, IEnumerable<string> candidates, CancellationToken ct)
    {
        var orphans = new List<string>();
        foreach (var sha in candidates.Distinct(StringComparer.Ordinal))
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT COUNT(*) FROM note_images WHERE sha256 = $sha;";
            cmd.Parameters.AddWithValue("$sha", sha);
            if (Convert.ToInt64(await cmd.ExecuteScalarAsync(ct) ?? 0L) == 0)
                orphans.Add(sha);
        }
        return orphans;
    }

    /// <summary>Maps the 7-column projection (no display_bytes).</summary>
    private static NoteImage MapNoteImage(DbDataReader reader) => new()
    {
        NoteId = reader.GetInt64(0),
        Sha256 = reader.GetString(1),
        Ext = reader.GetString(2),
        ByteSize = reader.GetInt64(3),
        Width = reader.GetInt32(4),
        Height = reader.GetInt32(5),
        CreatedAt = ParseUtc(reader.GetString(6)),
    };

    /// <summary>Maps the 8-column projection (created_at, then display_bytes).</summary>
    private static NoteImage MapNoteImage(DbDataReader reader, bool withDisplay)
    {
        if (!withDisplay)
            return MapNoteImage(reader);
        return new NoteImage
        {
            NoteId = reader.GetInt64(0),
            Sha256 = reader.GetString(1),
            Ext = reader.GetString(2),
            ByteSize = reader.GetInt64(3),
            Width = reader.GetInt32(4),
            Height = reader.GetInt32(5),
            CreatedAt = ParseUtc(reader.GetString(6)),
            DisplayBytes = reader.IsDBNull(7) ? null : (byte[])reader[7],
        };
    }

    /// <summary>
    /// Upsert one row into <c>notes_fts</c> (delete-by-rowid + insert) inside the
    /// given transaction. Both title and body are bigram-tokenized here so the index
    /// never sees raw CJK text (parity: indexing/querying share the same transform).
    /// </summary>
    private static async Task UpsertFtsAsync(
        SqliteConnection conn, SqliteTransaction tx, long id, string title, string plainText, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "DELETE FROM notes_fts WHERE rowid = $id;\n" +
            "INSERT INTO notes_fts (rowid, title, body) VALUES ($id, $title, $body);";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$title", BigramTokenizer.Tokenize(title));
        cmd.Parameters.AddWithValue("$body", BigramTokenizer.Tokenize(plainText));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static Note MapNote(DbDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        Uuid = reader.GetString(1),
        Title = reader.GetString(2),
        Category = reader.GetString(3),
        CreatedAt = ParseUtc(reader.GetString(4)),
        UpdatedAt = ParseUtc(reader.GetString(5)),
        PlainText = reader.GetString(6),
        Content = reader.GetString(7),
    };

    private static DateTimeOffset ParseUtc(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, UtcStyles);

    private static string ToDbString(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
