using System.Data.Common;
using System.Globalization;
using QNote.Models;

namespace QNote.Data;

/// <summary>
/// SQLite-backed note/category persistence over the single <c>qnote.db</c>. All
/// queries are parameterized and every connection/command/reader is disposed via
/// <c>await using</c> (see database-guidelines). Timestamps are stored as ISO-8601
/// UTC and read back as UTC.
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

    public async Task<IReadOnlyList<NoteSummary>> GetSummariesAsync(CancellationToken ct = default)
    {
        await using var conn = _factory.OpenRead();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT Id, Uuid, Title, substr(PlainText, 1, $len) AS Preview, Category, UpdatedAt " +
            "FROM notes ORDER BY UpdatedAt DESC;";
        cmd.Parameters.AddWithValue("$len", PreviewLength);

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
                UpdatedAt = ParseUtc(reader.GetString(5)),
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
        await using var cmd = conn.CreateCommand();
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
        return note with { Id = (long)result };
    }

    public async Task UpdateAsync(Note note, CancellationToken ct = default)
    {
        await using var conn = _factory.OpenWrite();
        await using var cmd = conn.CreateCommand();
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

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        await using var conn = _factory.OpenWrite();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM notes WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken ct = default)
    {
        await using var conn = _factory.OpenRead();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Name, IconKey, SortOrder FROM categories ORDER BY SortOrder;";

        var list = new List<Category>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new Category
            {
                Id = reader.GetInt64(0),
                Name = reader.GetString(1),
                IconKey = reader.GetString(2),
                SortOrder = reader.GetInt32(3),
            });
        }

        return list;
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
