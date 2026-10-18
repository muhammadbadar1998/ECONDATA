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

    public IReadOnlyList<MetricCoverage> GetMetricCoverage()
    {
        using var connection = _factory.OpenReadOnly();
        const string sql = """
            SELECT
                m.metric_code,
                m.metric_name,
                m.category,
                m.frequency,
                m.base_unit,
                COUNT(DISTINCT o.entity_id) AS entity_count,
                COUNT(o.id) AS observation_count,
                MIN(o.start_date) AS min_period,
                MAX(o.start_date) AS max_period
            FROM dbe_metrics m
            LEFT JOIN dbe_observations o ON m.metric_id = o.metric_id
            GROUP BY m.metric_id, m.metric_code, m.metric_name, m.category, m.frequency, m.base_unit
            ORDER BY m.metric_code;
            """;

        var list = new List<MetricCoverage>();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new MetricCoverage(
                MetricCode: reader.GetString(0),
                MetricName: reader.GetString(1),
                Category: reader.GetString(2),
                Frequency: reader.GetString(3),
                BaseUnit: reader.GetString(4),
                EntityCount: reader.GetInt32(5),
                ObservationCount: reader.GetInt32(6),
                MinPeriod: reader.IsDBNull(7) ? null : reader.GetString(7),
                MaxPeriod: reader.IsDBNull(8) ? null : reader.GetString(8)));
        }
        return list;
    }

    public IReadOnlyList<SeriesPoint> GetSeries(string entity, string metric)
    {
        using var connection = _factory.OpenReadOnly();
        const string sql = """
            SELECT
                e.entity_code,
                m.metric_code,
                o.start_date,
                o.val,
                o.unit,
                o.period_str
            FROM dbe_observations o
            JOIN dbe_entities e ON o.entity_id = e.entity_id
            JOIN dbe_metrics m ON o.metric_id = m.metric_id
            WHERE e.entity_code = @entity AND m.metric_code = @metric
            ORDER BY o.start_date;
            """;

        var list = new List<SeriesPoint>();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@entity", entity);
        command.Parameters.AddWithValue("@metric", metric);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new SeriesPoint(
                EntityCode: reader.GetString(0),
                MetricCode: reader.GetString(1),
                Period: reader.GetString(2),
                Value: reader.GetDouble(3),
                Unit: reader.IsDBNull(4) ? null : reader.GetString(4),
                PeriodStr: reader.IsDBNull(5) ? null : reader.GetString(5)));
        }
        return list;
    }

    public IReadOnlyList<SeriesPoint> GetSeriesForEntities(string metric, IReadOnlyList<string> entities)
    {
        var list = new List<SeriesPoint>();
        using var connection = _factory.OpenReadOnly();
        const string sql = """
            SELECT
                e.entity_code,
                m.metric_code,
                o.start_date,
                o.val,
                o.unit,
                o.period_str
            FROM dbe_observations o
            JOIN dbe_entities e ON o.entity_id = e.entity_id
            JOIN dbe_metrics m ON o.metric_id = m.metric_id
            WHERE m.metric_code = @metric AND e.entity_code = @entity
            ORDER BY e.entity_code, o.start_date;
            """;

        foreach (var entity in entities)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("@metric", metric);
            command.Parameters.AddWithValue("@entity", entity);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new SeriesPoint(
                    EntityCode: reader.GetString(0),
                    MetricCode: reader.GetString(1),
                    Period: reader.GetString(2),
                    Value: reader.GetDouble(3),
                    Unit: reader.IsDBNull(4) ? null : reader.GetString(4),
                    PeriodStr: reader.IsDBNull(5) ? null : reader.GetString(5)));
            }
        }
        return list;
    }

    public IReadOnlyList<LatestSnapshotRow> GetLatestSnapshot(string? entity, string? metric, string? category, int limit)
    {
        limit = Math.Clamp(limit, 1, 1000);

        using var connection = _factory.OpenReadOnly();
        var sqlBuilder = new System.Text.StringBuilder("""
            SELECT
                country_code,
                country_name,
                metric_code,
                metric_name,
                category,
                frequency,
                base_unit,
                value,
                period
            FROM v_latest_metrics_snapshot
            WHERE 1=1
            """);

        if (!string.IsNullOrEmpty(entity))
        {
            sqlBuilder.Append(" AND country_code = @entity");
        }
        if (!string.IsNullOrEmpty(metric))
        {
            sqlBuilder.Append(" AND metric_code = @metric");
        }
        if (!string.IsNullOrEmpty(category))
        {
            sqlBuilder.Append(" AND category = @category");
        }

        sqlBuilder.Append(" ORDER BY country_code, metric_code LIMIT @limit;");

        var list = new List<LatestSnapshotRow>();
        using var command = connection.CreateCommand();
        command.CommandText = sqlBuilder.ToString();
        if (!string.IsNullOrEmpty(entity))
        {
            command.Parameters.AddWithValue("@entity", entity);
        }
        if (!string.IsNullOrEmpty(metric))
        {
            command.Parameters.AddWithValue("@metric", metric);
        }
        if (!string.IsNullOrEmpty(category))
        {
            command.Parameters.AddWithValue("@category", category);
        }
        command.Parameters.AddWithValue("@limit", limit);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new LatestSnapshotRow(
                CountryCode: reader.GetString(0),
                CountryName: reader.GetString(1),
                MetricCode: reader.GetString(2),
                MetricName: reader.GetString(3),
                Category: reader.GetString(4),
                Frequency: reader.GetString(5),
                BaseUnit: reader.GetString(6),
                Value: reader.GetDouble(7),
                Period: reader.GetString(8)));
        }
        return list;
    }

    public IReadOnlyList<LatestSnapshotRow> RankEntities(string metric, string direction, int limit)
    {
        limit = Math.Clamp(limit, 1, 1000);
        var desc = string.Equals(direction, "desc", StringComparison.OrdinalIgnoreCase);
        using var connection = _factory.OpenReadOnly();
        var sqlBuilder = new System.Text.StringBuilder($"""
            SELECT
                country_code,
                country_name,
                metric_code,
                metric_name,
                category,
                frequency,
                base_unit,
                value,
                period
            FROM v_latest_metrics_snapshot
            WHERE metric_code = @metric
            ORDER BY value {(desc ? "DESC" : "ASC")}
            LIMIT @limit;
            """);

        var list = new List<LatestSnapshotRow>();
        using var command = connection.CreateCommand();
        command.CommandText = sqlBuilder.ToString();
        command.Parameters.AddWithValue("@metric", metric);
        command.Parameters.AddWithValue("@limit", limit);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new LatestSnapshotRow(
                CountryCode: reader.GetString(0),
                CountryName: reader.GetString(1),
                MetricCode: reader.GetString(2),
                MetricName: reader.GetString(3),
                Category: reader.GetString(4),
                Frequency: reader.GetString(5),
                BaseUnit: reader.GetString(6),
                Value: reader.GetDouble(7),
                Period: reader.GetString(8)));
        }
        return list;
    }

    public IReadOnlyList<EntitySummary> GetEntities(string? q, string? type, int limit)
    {
        limit = Math.Clamp(limit, 1, 1000);

        using var connection = _factory.OpenReadOnly();
        var sqlBuilder = new System.Text.StringBuilder("""
            SELECT
                e.entity_code,
                e.entity_name,
                e.entity_type,
                e.is_aggregate,
                (SELECT COUNT(*) FROM dbe_entity_aliases a WHERE a.entity_id = e.entity_id) AS alias_count,
                (SELECT COUNT(*) FROM dbe_observations o WHERE o.entity_id = e.entity_id) AS observation_count
            FROM dbe_entities e
            WHERE e.is_active = 1
            """);

        if (!string.IsNullOrEmpty(q))
        {
            sqlBuilder.Append(" AND (e.entity_code LIKE '%' || @q || '%' OR e.entity_name LIKE '%' || @q || '%')");
        }
        if (!string.IsNullOrEmpty(type))
        {
            sqlBuilder.Append(" AND e.entity_type = @type");
        }

        sqlBuilder.Append(" ORDER BY e.entity_code LIMIT @limit;");

        var list = new List<EntitySummary>();
        using var command = connection.CreateCommand();
        command.CommandText = sqlBuilder.ToString();
        if (!string.IsNullOrEmpty(q))
        {
            command.Parameters.AddWithValue("@q", q);
        }
        if (!string.IsNullOrEmpty(type))
        {
            command.Parameters.AddWithValue("@type", type);
        }
        command.Parameters.AddWithValue("@limit", limit);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new EntitySummary(
                EntityCode: reader.GetString(0),
                EntityName: reader.GetString(1),
                EntityType: reader.GetString(2),
                IsAggregate: reader.GetInt32(3),
                AliasCount: reader.GetInt32(4),
                ObservationCount: reader.GetInt32(5)));
        }
        return list;
    }

    public IReadOnlyList<RunSummary> GetRuns(int limit)
    {
        limit = Math.Clamp(limit, 1, 1000);

        using var connection = _factory.OpenReadOnly();
        const string sql = """
            SELECT
                run_id,
                run_timestamp,
                status,
                execution_seconds,
                metrics_extracted,
                observations_upserted,
                observations_skipped
            FROM dbe_pipeline_runs
            ORDER BY run_id DESC
            LIMIT @limit;
            """;

        var list = new List<RunSummary>();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@limit", limit);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new RunSummary(
                RunId: reader.GetInt64(0),
                RunTimestamp: reader.GetString(1),
                Status: reader.GetString(2),
                ExecutionSeconds: reader.GetDouble(3),
                MetricsExtracted: reader.GetInt32(4),
                ObservationsUpserted: reader.GetInt32(5),
                ObservationsSkipped: reader.GetInt32(6)));
        }
        return list;
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
