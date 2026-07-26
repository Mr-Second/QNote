using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using QNote.Data;
using QNote.Data.Schema;
using QNote.Services;

namespace QNote.Tests;

/// <summary>
/// A throwaway <c>qnote.db</c> on a temp path with the schema created, for
/// repository/service tests. <see cref="Dispose"/> clears the connection pool and
/// deletes the db files (mirrors <see cref="SchemaInitializerTests"/> cleanup).
/// </summary>
internal sealed class TestDatabase : IDisposable
{
    private readonly string _dbPath;

    public TestDatabase()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"qnote-test-{Guid.NewGuid():N}.db");
        Factory = new DbConnectionFactory(_dbPath);
        new SchemaInitializer(Factory).EnsureCreated();
    }

    public DbConnectionFactory Factory { get; }

    public NoteRepository NewRepository() => new(Factory);

    public SearchService NewSearchService() => new(Factory, NewRepository(), NullLogger<SearchService>.Instance);

    public CategoryService NewCategoryService() => new(NewRepository(), NullLogger<CategoryService>.Instance);

    public SettingsService NewSettingsService() => new(Factory, NullLogger<SettingsService>.Instance);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        TryDelete(_dbPath);
        TryDelete(_dbPath + "-wal");
        TryDelete(_dbPath + "-shm");
    }

    private static void TryDelete(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }
}
