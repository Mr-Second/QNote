using Microsoft.Data.Sqlite;

namespace QNote.Data;

/// <summary>
/// Creates SQLite connections to the single <c>qnote.db</c>. The database path is
/// injected (from <c>AppPaths</c> in production, a temp path in tests) — never
/// hardcode a connection path elsewhere (see database-guidelines).
/// </summary>
public sealed class DbConnectionFactory
{
    private readonly string _connectionString;

    public DbConnectionFactory(string databasePath)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
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
        return conn;
    }
}
