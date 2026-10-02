using Microsoft.Data.Sqlite;

namespace QNote.Data;

/// <summary>
/// Creates SQLite connections to the single <c>qnote.db</c>. The database path is
/// injected (from <c>AppPaths</c> in production, a temp path in tests) — never
/// hardcode a connection path elsewhere (see database-guidelines).
/// </summary>
/// <remarks>
/// <paramref name="pooling"/> defaults to on (desktop app, hot reopen). Tests pass
/// <c>false</c>: parallel test classes share the process, and
/// <see cref="SqliteConnection.ClearAllPools"/> (anywhere — its own dispose, or the
/// backup service's overwrite path) disposes GLOBALLY pooled idle handles, racing
/// a neighbour's pool fetch into <c>ObjectDisposedException: SQLitePCL.sqlite3</c>
/// (the intermittent repository-test flake, 2026-09-26 → fixed 2026-10-03).
/// Non-pooled connections are immune, and closing them truly releases the WAL
/// files for the temp-dir cleanup deletes.
/// </remarks>
public sealed class DbConnectionFactory
{
    private readonly string _connectionString;

    public DbConnectionFactory(string databasePath, bool pooling = true)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = pooling,
        }.ToString();
    }

    /// <summary>Open a connection for reading.</summary>
    public SqliteConnection OpenRead() => Open();

    /// <summary>Open a connection for writing.</summary>
    public SqliteConnection OpenWrite() => Open();

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        // Required for note_images' ON DELETE CASCADE to fire: SQLite leaves foreign
        // keys OFF by default, per connection.
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();
        return conn;
    }
}
