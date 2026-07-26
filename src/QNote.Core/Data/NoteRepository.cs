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
            "SELECT Id, Uuid, Title, Content, PlainText, Category, CreatedAt, UpdatedAt FROM notes ORDER BY UpdatedAt DESC;";

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
                Preview = reader.GetString(3),
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
            "SELECT Id, Uuid, Title, Content, PlainText, Category, CreatedAt, UpdatedAt FROM notes WHERE Id = $id;";
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

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        await using var conn = _factory.OpenWrite();
        await using var tx = conn.BeginTransaction();

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "DELETE FROM notes WHERE Id = $id;\n" +
            "DELETE FROM notes_fts WHERE rowid = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct);

        await tx.CommitAsync(ct);
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

    public async Task DeleteCategoryAsync(long id, CancellationToken ct = default)
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

        if (name is not null)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            // FTS rows go first (they key off notes.Id), then the notes, then the
            // category row — one transaction, so the destructive cascade is atomic.
            cmd.CommandText =
                "DELETE FROM notes_fts WHERE rowid IN (SELECT Id FROM notes WHERE Category = $name);\n" +
                "DELETE FROM notes WHERE Category = $name;\n" +
                "DELETE FROM categories WHERE Id = $id;";
            cmd.Parameters.AddWithValue("$name", name);
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
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
        Content = reader.GetString(3),
        PlainText = reader.GetString(4),
        Category = reader.GetString(5),
        CreatedAt = ParseUtc(reader.GetString(6)),
        UpdatedAt = ParseUtc(reader.GetString(7)),
    };

    private static DateTimeOffset ParseUtc(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, UtcStyles);

    private static string ToDbString(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
