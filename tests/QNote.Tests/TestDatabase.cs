using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using QNote.Infrastructure;
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

        // Image storage is rooted in a per-test temp dir so original files never
        // touch the real user data directory.
        ImagesRoot = Path.Combine(Path.GetTempPath(), $"qnote-test-images-{Guid.NewGuid():N}");
        Paths = new AppPaths(ImagesRoot);
    }

    public DbConnectionFactory Factory { get; }

    public AppPaths Paths { get; }

    private string ImagesRoot { get; }

    public NoteRepository NewRepository() => new(Factory);

    public SearchService NewSearchService() => new(Factory, NewRepository(), NullLogger<SearchService>.Instance);

    public ImageService NewImageService() => new(Paths, NullLogger<ImageService>.Instance);

    public CategoryService NewCategoryService() =>
        new(NewRepository(), NewImageService(), NullLogger<CategoryService>.Instance);

    public NoteService NewNoteService() =>
        new(NewRepository(), NewImageService(), NullLogger<NoteService>.Instance);

    public SettingsService NewSettingsService() => new(Factory, NullLogger<SettingsService>.Instance);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        TryDelete(_dbPath);
        TryDelete(_dbPath + "-wal");
        TryDelete(_dbPath + "-shm");
        if (Directory.Exists(ImagesRoot))
            Directory.Delete(ImagesRoot, recursive: true);
    }

    private static void TryDelete(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }
}
