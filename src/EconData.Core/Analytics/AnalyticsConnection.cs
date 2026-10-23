using System.Data.Common;
using DuckDB.NET.Data;
using Microsoft.Data.Sqlite;

namespace EconData.Core.Analytics;

/// <summary>
/// Minimal read-only connection seam so the analytics queries are written once and run
/// against either SQLite (vector-era databases) or DuckDB (the gold tier). DuckDB binds
/// positional <c>?</c> placeholders only, so named parameters are rewritten before binding.
/// </summary>
internal sealed class AnalyticsConnection : IDisposable
{
    private readonly DbConnection _connection;
    private readonly bool _positional;

    private AnalyticsConnection(DbConnection connection, bool positional)
    {
        _connection = connection;
        _positional = positional;
    }

    public static AnalyticsConnection Sqlite(SqliteConnection connection)
        => new(connection, positional: false);

    public static AnalyticsConnection Gold(DuckDBConnection connection)
        => new(connection, positional: true);

    public Command CreateCommand()
    {
        var command = _connection.CreateCommand();
        return new Command(command, _positional);
    }

    public void Dispose() => _connection.Dispose();

    /// <summary>Creates a command and binds named parameters, translating for DuckDB.</summary>
    internal sealed class Command : IDisposable
    {
        private readonly DbCommand _command;
        private readonly bool _positional;
        private readonly List<(string Name, object Value)> _values = new();

        public Command(DbCommand command, bool positional)
        {
            _command = command;
            _positional = positional;
        }

        public string CommandText
        {
            set => _command.CommandText = value;
        }

        public void Bind(string name, object value) => _values.Add((name, value));

        public System.Data.Common.DbDataReader ExecuteReader()
        {
            Apply();
            return _command.ExecuteReader();
        }

        public object? ExecuteScalar()
        {
            Apply();
            return _command.ExecuteScalar();
        }

        public void Dispose() => _command.Dispose();

        private void Apply()
        {
            if (!_positional)
            {
                foreach (var (name, value) in _values)
                {
                    _command.Parameters.Add(new SqliteParameter(name, value));
                }

                return;
            }

            var sql = _command.CommandText;
            foreach (var (name, _) in _values.OrderByDescending(v => v.Name.Length))
            {
                sql = sql.Replace(name, "?", StringComparison.Ordinal);
            }

            _command.CommandText = sql;
            foreach (var (_, value) in _values)
            {
                _command.Parameters.Add(new DuckDBParameter { Value = value });
            }
        }
    }
}
