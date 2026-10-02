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
            var factory = new DbConnectionFactory(dbPath, pooling: false);
            var schema = new SchemaInitializer(factory);

            schema.EnsureCreated();

            using var conn = factory.OpenRead();
            Assert.Equal(SchemaInitializer.CurrentVersion, GetUserVersion(conn));
            Assert.True(TableExists(conn, "notes"), "notes table missing");
            Assert.True(TableExists(conn, "categories"), "categories table missing");
            Assert.True(TableExists(conn, "settings"), "settings table missing");
            Assert.True(TableExists(conn, "notes_fts"), "notes_fts (FTS5) table missing");
            Assert.True(TableExists(conn, "note_images"), "note_images table missing");
        }
        finally
        {
            TryDelete(dbPath);
            TryDelete(dbPath + "-wal");
            TryDelete(dbPath + "-shm");
        }
    }

    [Fact]
    public void EnsureCreated_V5_PutsContentLast_AndPreservesRows()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"qnote-test-{Guid.NewGuid():N}.db");
        try
        {
            var factory = new DbConnectionFactory(dbPath, pooling: false);
            using (var conn = factory.OpenWrite())
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE notes (
                        Id        INTEGER PRIMARY KEY AUTOINCREMENT,
                        Uuid      TEXT    NOT NULL UNIQUE,
                        Title     TEXT    NOT NULL DEFAULT '',
                        Content   TEXT    NOT NULL DEFAULT '',
                        Category  TEXT    NOT NULL DEFAULT '',
                        CreatedAt TEXT    NOT NULL,
                        UpdatedAt TEXT    NOT NULL,
                        PlainText TEXT    NOT NULL DEFAULT ''
                    );
                    INSERT INTO notes (Uuid, Title, Content, PlainText, Category, CreatedAt, UpdatedAt)
                        VALUES ('uuid-1', '旧标题', 'RTF正文', '纯文本', '工作', '2026-01-01T00:00:00.0000000Z', '2026-01-02T00:00:00.0000000Z');
                    PRAGMA user_version = 4;
                    """;
                cmd.ExecuteNonQuery();
            }

            new SchemaInitializer(factory).EnsureCreated();

            using var check = factory.OpenRead();
            Assert.Equal(SchemaInitializer.CurrentVersion, GetUserVersion(check));
            Assert.Equal(8, ColumnCount(check, "notes"));
            Assert.Equal("Content", LastColumn(check, "notes")); // moved to the end
            Assert.Equal("旧标题", Scalar(check, "SELECT Title FROM notes WHERE Uuid = 'uuid-1';"));
            Assert.Equal("RTF正文", Scalar(check, "SELECT Content FROM notes WHERE Uuid = 'uuid-1';"));
            Assert.Equal("纯文本", Scalar(check, "SELECT PlainText FROM notes WHERE Uuid = 'uuid-1';"));
        }
        finally
        {
            TryDelete(dbPath);
            TryDelete(dbPath + "-wal");
            TryDelete(dbPath + "-shm");
        }
    }

    [Fact]
    public void EnsureCreated_V5_KeepsFtsRowidsAligned_AndCascadesNoteImages()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"qnote-test-{Guid.NewGuid():N}.db");
        try
        {
            // A v4 db with FTS rows already populated — the rebuild must not desync them.
            var factory = new DbConnectionFactory(dbPath, pooling: false);
            using (var conn = factory.OpenWrite())
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE notes (
                        Id        INTEGER PRIMARY KEY AUTOINCREMENT,
                        Uuid      TEXT    NOT NULL UNIQUE,
                        Title     TEXT    NOT NULL DEFAULT '',
                        Content   TEXT    NOT NULL DEFAULT '',
                        Category  TEXT    NOT NULL DEFAULT '',
                        CreatedAt TEXT    NOT NULL,
                        UpdatedAt TEXT    NOT NULL,
                        PlainText TEXT    NOT NULL DEFAULT ''
                    );
                    CREATE VIRTUAL TABLE notes_fts USING fts5(
                        title, body, content='', contentless_delete=1, tokenize='unicode61');
                    INSERT INTO notes (Uuid, Title, Content, Category, CreatedAt, UpdatedAt, PlainText)
                        VALUES ('uuid-1', '标题', 'RTF', '', '2026-01-01T00:00:00.0000000Z', '2026-01-01T00:00:00.0000000Z', '正文');
                    INSERT INTO notes_fts (rowid, title, body) VALUES (1, '标题', '正文');
                    PRAGMA user_version = 4;
                    """;
                cmd.ExecuteNonQuery();
            }

            new SchemaInitializer(factory).EnsureCreated();

            using var check = factory.OpenRead();

            // The FTS shadow is contentless: assert (rowid, match) alignment, not column reads.
            Assert.Equal(1L, ScalarLong(check, "SELECT COUNT(*) FROM notes_fts;"));
            Assert.Equal(1L, ScalarLong(check, "SELECT COUNT(*) FROM notes_fts WHERE notes_fts MATCH '\"正文\"';"));
            Assert.Equal(1L, ScalarLong(
                check, "SELECT COUNT(*) FROM notes WHERE Id IN (SELECT rowid FROM notes_fts);"));

            // note_images must cascade off the rebuilt notes table (FK + pragma).
            using (var conn = factory.OpenWrite())
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "INSERT INTO note_images (note_id, sha256, created_at) VALUES (1, 'abc', '2026-01-01T00:00:00.0000000Z');" +
                    "DELETE FROM notes WHERE Id = 1;" +
                    "SELECT COUNT(*) FROM note_images;";
                Assert.Equal(0L, Convert.ToInt64(cmd.ExecuteScalar()));
            }

            // Deleting the last note must not have broken the AUTOINCREMENT sequence.
            using (var conn = factory.OpenWrite())
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "INSERT INTO notes (Uuid, Title, CreatedAt, UpdatedAt) " +
                    "VALUES ('uuid-2', 't', '2026-01-02T00:00:00.0000000Z', '2026-01-02T00:00:00.0000000Z');" +
                    "SELECT last_insert_rowid();";
                Assert.Equal(2L, Convert.ToInt64(cmd.ExecuteScalar())); // ids not reused
            }
        }
        finally
        {
            TryDelete(dbPath);
            TryDelete(dbPath + "-wal");
            TryDelete(dbPath + "-shm");
        }
    }

    private static long ScalarLong(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }

    private static string Scalar(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return (string)(cmd.ExecuteScalar() ?? string.Empty);
    }

    private static string LastColumn(SqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";
        using var reader = cmd.ExecuteReader();
        var last = string.Empty;
        while (reader.Read())
            last = reader.GetString(1);
        return last;
    }

    private static int ColumnCount(SqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";
        using var reader = cmd.ExecuteReader();
        var count = 0;
        while (reader.Read())
            count++;
        return count;
    }

    [Fact]
    public void EnsureCreated_MigratesV1ToV2_AddingPlainTextColumn()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"qnote-test-{Guid.NewGuid():N}.db");
        try
        {
            // Simulate a v1 database: notes table without PlainText, user_version = 1.
            var factory = new DbConnectionFactory(dbPath, pooling: false);
            using (var conn = factory.OpenWrite())
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE notes (
                        Id        INTEGER PRIMARY KEY AUTOINCREMENT,
                        Uuid      TEXT    NOT NULL UNIQUE,
                        Title     TEXT    NOT NULL DEFAULT '',
                        Content   TEXT    NOT NULL DEFAULT '',
                        Category  TEXT    NOT NULL DEFAULT '',
                        CreatedAt TEXT    NOT NULL,
                        UpdatedAt TEXT    NOT NULL
                    );
                    PRAGMA user_version = 1;
                    """;
                cmd.ExecuteNonQuery();
            }

            new SchemaInitializer(factory).EnsureCreated();

            using var check = factory.OpenRead();
            Assert.Equal(SchemaInitializer.CurrentVersion, GetUserVersion(check));
            Assert.True(ColumnExists(check, "notes", "PlainText"), "PlainText column missing after v2 migration");
        }
        finally
        {
            TryDelete(dbPath);
            TryDelete(dbPath + "-wal");
            TryDelete(dbPath + "-shm");
        }
    }

    private static bool ColumnExists(SqliteConnection conn, string table, string column)
    {
        using var cmd = conn.CreateCommand();
        // PRAGMA table_info cannot be parameterized; names here are test constants.
        cmd.CommandText = $"PRAGMA table_info({table});";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.Ordinal))
                return true;
        }
        return false;
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
