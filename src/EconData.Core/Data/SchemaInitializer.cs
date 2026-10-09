using System.Globalization;
using Microsoft.Data.Sqlite;
using Serilog;

namespace EconData.Core.Data;

/// <summary>Applies the SQLite DDL script and verifies the resulting database.</summary>
public sealed class SchemaInitializer
{
    private readonly SqliteConnectionFactory _factory;
    private readonly ILogger _logger;

    public SchemaInitializer(SqliteConnectionFactory factory, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(logger);
        _factory = factory;
        _logger = logger;
    }

    public bool Initialize(string schemaScriptPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaScriptPath);
        if (!File.Exists(schemaScriptPath))
        {
            _logger.Error("DDL schema script not found at path: {Path}", schemaScriptPath);
            return false;
        }

        var script = File.ReadAllText(schemaScriptPath);
        _logger.Information("Initializing dbe schema from {Path}...", schemaScriptPath);
        try
        {
            using var connection = _factory.Open();
            using var command = connection.CreateCommand();
            command.CommandText = script;
            command.ExecuteNonQuery();
            _logger.Information("Database schema successfully initialized with dbe_* tables.");
            return true;
        }
        catch (SqliteException ex)
        {
            _logger.Error(ex, "Failed to initialize database schema.");
            return false;
        }
    }

    public bool Verify(out string message)
    {
        try
        {
            using var connection = _factory.Open();
            using var command = connection.CreateCommand();

            command.CommandText = "PRAGMA integrity_check;";
            var integrity = Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture);

            command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name LIKE 'dbe_%';";
            var tables = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);

            message = $"integrity_check={integrity}; dbe_tables={tables}";
            return string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase);
        }
        catch (SqliteException ex)
        {
            message = ex.Message;
            return false;
        }
    }
}
