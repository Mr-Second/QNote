using System.IO;
using Microsoft.Data.Sqlite;
using QNote.Data;
using QNote.Data.Schema;

namespace QNote.Tests;

/// <summary>
/// Smoke test for the greenfield schema: proves the data layer builds a valid
/// <c>qnote.db</c> (all tables + FTS5) and stamps the schema version.
/// </summary>
public sealed class SchemaInitializerTests
{
    [Fact]
    public void EnsureCreated_CreatesTablesAndStampsUserVersion()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"qnote-test-{Guid.NewGuid():N}.db");
        try
        {
            var factory = new DbConnectionFactory(dbPath);
            var schema = new SchemaInitializer(factory);

            schema.EnsureCreated();

            using var conn = factory.OpenRead();
            Assert.Equal(SchemaInitializer.CurrentVersion, GetUserVersion(conn));
            Assert.True(TableExists(conn, "notes"), "notes table missing");
            Assert.True(TableExists(conn, "categories"), "categories table missing");
            Assert.True(TableExists(conn, "settings"), "settings table missing");
            Assert.True(TableExists(conn, "notes_fts"), "notes_fts (FTS5) table missing");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDelete(dbPath);
            TryDelete(dbPath + "-wal");
            TryDelete(dbPath + "-shm");
        }
    }

    private static long GetUserVersion(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }

    private static bool TableExists(SqliteConnection conn, string name)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name = $name;";
        cmd.Parameters.AddWithValue("$name", name);
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) > 0;
    }

    private static void TryDelete(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }
}
