using EconData.Core.Data;
using Microsoft.Data.Sqlite;

namespace EconData.Tests;

public class SqliteConnectionFactoryTests : IDisposable
{
    private static readonly string[] DatabaseFileSuffixes = { "", "-wal", "-shm" };

    private readonly string _dbPath;
    private readonly SqliteConnectionFactory _factory;

    public SqliteConnectionFactoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "econdata-factory-" + Guid.NewGuid().ToString("N") + ".db");
        _factory = new SqliteConnectionFactory(_dbPath);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in DatabaseFileSuffixes)
        {
            var path = _dbPath + suffix;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Open_AfterReadOnlyConnection_RemainsWritable()
    {
        using (var connection = _factory.Open())
        {
            Execute(connection, "CREATE TABLE t (id INTEGER PRIMARY KEY AUTOINCREMENT, value TEXT);");
        }

        using (var readOnly = _factory.OpenReadOnly())
        {
            Assert.Throws<SqliteException>(() => Execute(readOnly, "INSERT INTO t (value) VALUES ('readonly');"));
        }

        using var writer = _factory.Open();
        Execute(writer, "INSERT INTO t (value) VALUES ('written');");
        using var command = writer.CreateCommand();
        command.CommandText = "SELECT count(*) FROM t;";
        Assert.Equal(1L, Convert.ToInt64(command.ExecuteScalar()));
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
