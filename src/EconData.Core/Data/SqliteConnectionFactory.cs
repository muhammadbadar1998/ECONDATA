using Microsoft.Data.Sqlite;

namespace EconData.Core.Data;

/// <summary>
/// Creates and opens SQLite connections for a given database file, applying the
/// pragmas the pipeline relies on (foreign keys, WAL, busy timeout).
/// </summary>
public sealed class SqliteConnectionFactory
{
    private static readonly string[] Pragmas =
    {
        "foreign_keys = ON",
        "busy_timeout = 5000",
        "journal_mode = WAL",
        "synchronous = NORMAL",
    };

    private static readonly string[] ReadOnlyPragmas =
    {
        "busy_timeout = 5000",
        "query_only = ON",
    };

    private readonly string _connectionString;

    public string DatabasePath { get; }

    public SqliteConnectionFactory(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = databasePath;

        if (!IsInMemory(databasePath))
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();
    }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        foreach (var pragma in Pragmas)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA {pragma};";
            command.ExecuteNonQuery();
        }

        return connection;
    }

    /// <summary>
    /// Opens a connection guarded by <c>PRAGMA query_only</c> for read-only
    /// analytics queries. WAL readers proceed while a writer is active.
    /// </summary>
    public SqliteConnection OpenReadOnly()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        foreach (var pragma in ReadOnlyPragmas)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA {pragma};";
            command.ExecuteNonQuery();
        }

        return connection;
    }

    private static bool IsInMemory(string databasePath)
        => databasePath.Equals(":memory:", StringComparison.OrdinalIgnoreCase);
}
