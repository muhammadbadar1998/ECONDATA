using System.Globalization;
using EconData.Core.Data;
using Microsoft.Data.Sqlite;

namespace EconData.Core.Analytics;

/// <summary>
/// Read-only analytics over the normalized <c>dbe_*</c> tables,
/// backing the web dashboard's JSON endpoints.
/// </summary>
public sealed class AnalyticsService
{
    private readonly SqliteConnectionFactory _factory;

    public AnalyticsService(SqliteConnectionFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    public OverviewStats GetOverview()
    {
        using var connection = _factory.OpenReadOnly();

        string? lastStatus = null;
        string? lastTimestamp = null;
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT status, run_timestamp FROM dbe_pipeline_runs " +
                "ORDER BY run_id DESC LIMIT 1;";
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                lastStatus = reader.GetString(0);
                lastTimestamp = reader.GetString(1);
            }
        }

        return new OverviewStats(
            EntityCount: ScalarInt(connection, "SELECT COUNT(*) FROM dbe_entities;"),
            AliasCount: ScalarInt(connection, "SELECT COUNT(*) FROM dbe_entity_aliases;"),
            MetricCount: ScalarInt(connection, "SELECT COUNT(*) FROM dbe_metrics;"),
            SourceCount: ScalarInt(connection, "SELECT COUNT(*) FROM dbe_sources;"),
            ObservationCount: ScalarLong(connection, "SELECT COUNT(*) FROM dbe_observations;"),
            EmbeddingCount: ScalarInt(connection, "SELECT COUNT(*) FROM dbe_embeddings;"),
            MembershipCount: ScalarInt(connection, "SELECT COUNT(*) FROM dbe_entity_membership;"),
            LastRunStatus: lastStatus,
            LastRunTimestamp: lastTimestamp);
    }

    private static int ScalarInt(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static long ScalarLong(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
}
