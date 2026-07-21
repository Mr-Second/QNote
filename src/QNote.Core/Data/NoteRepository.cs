using System.Globalization;
using QNote.Models;

namespace QNote.Data;

/// <summary>
/// SQLite-backed note/category persistence. Skeleton scope: read paths are real;
/// write and search paths land in their feature tasks.
/// </summary>
public sealed class NoteRepository : INoteRepository
{
    private const DateTimeStyles UtcStyles =
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;

    private readonly DbConnectionFactory _factory;

    public NoteRepository(DbConnectionFactory factory) => _factory = factory;

    public async Task<IReadOnlyList<Note>> GetAllAsync(CancellationToken ct = default)
    {
        await using var conn = _factory.OpenRead();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT Id, Uuid, Title, Content, Category, CreatedAt, UpdatedAt FROM notes ORDER BY UpdatedAt DESC;";

        var list = new List<Note>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new Note
            {
                Id = reader.GetInt64(0),
                Uuid = reader.GetString(1),
                Title = reader.GetString(2),
                Content = reader.GetString(3),
                Category = reader.GetString(4),
                CreatedAt = DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture, UtcStyles),
                UpdatedAt = DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture, UtcStyles),
            });
        }

        return list;
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
}
