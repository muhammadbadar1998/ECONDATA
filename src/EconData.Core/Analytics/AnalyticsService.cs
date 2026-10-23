using System.Globalization;
using EconData.Core.Data;
using EconData.Core.Lake;

namespace EconData.Core.Analytics;

/// <summary>
/// Read-only analytics over the normalized <c>dbe_*</c> tables, backing the web
/// dashboard's JSON endpoints. Reads the DuckDB gold tier when one is configured and
/// falls back to the SQLite database, so both the lake-era and vector-era layouts work.
/// </summary>
public sealed class AnalyticsService
{
    private readonly SqliteConnectionFactory _factory;
    private readonly GoldTier? _gold;

    public AnalyticsService(SqliteConnectionFactory factory, GoldTier? gold = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
        _gold = gold;
    }

    /// <summary>True when analytics read the DuckDB gold tier rather than SQLite.</summary>
    public bool UsesGold => _gold is not null && _gold.Exists;

    private AnalyticsConnection Open()
        => UsesGold
            ? AnalyticsConnection.Gold(_gold!.OpenReadOnly())
            : AnalyticsConnection.Sqlite(_factory.OpenReadOnly());

    public OverviewStats GetOverview()
    {
        using var connection = Open();

        string? lastStatus = null;
        string? lastTimestamp = null;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT status, run_timestamp FROM dbe_pipeline_runs ORDER BY run_id DESC LIMIT 1;";
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
            EmbeddingCount: UsesGold ? 0 : ScalarInt(connection, "SELECT COUNT(*) FROM dbe_embeddings;"),
            MembershipCount: ScalarInt(connection, "SELECT COUNT(*) FROM dbe_entity_membership;"),
            LastRunStatus: lastStatus,
            LastRunTimestamp: lastTimestamp);
    }

    public IReadOnlyList<MetricCoverage> GetMetricCoverage()
    {
        using var connection = Open();
        const string sql = """
            SELECT
                m.metric_code,
                m.metric_name,
                m.category,
                m.frequency,
                m.base_unit,
                COUNT(DISTINCT o.entity_id) AS entity_count,
                COUNT(o.id) AS observation_count,
                (SELECT COALESCE(COUNT(DISTINCT d.entity_id),0) FROM dbe_derived_observations d WHERE d.metric_id = m.metric_id) AS derived_entity_count,
                (SELECT COALESCE(COUNT(*),0) FROM dbe_derived_observations d WHERE d.metric_id = m.metric_id) AS derived_observation_count,
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
                EntityCount: Int(reader, 5),
                ObservationCount: Int(reader, 6),
                DerivedEntityCount: reader.IsDBNull(7) ? 0 : Int(reader, 7),
                DerivedObservationCount: reader.IsDBNull(8) ? 0 : Int(reader, 8),
                MinPeriod: reader.IsDBNull(9) ? null : reader.GetString(9),
                MaxPeriod: reader.IsDBNull(10) ? null : reader.GetString(10)));
        }

        return list;
    }

    public IReadOnlyList<SeriesPoint> GetSeries(string entity, string metric)
    {
        using var connection = Open();
        const string sql = """
            SELECT e.entity_code, m.metric_code, o.start_date, o.val, o.unit, o.period_str, o.source_kind
            FROM v_observations_all o
            JOIN dbe_entities e ON o.entity_id = e.entity_id
            JOIN dbe_metrics m ON o.metric_id = m.metric_id
            WHERE e.entity_code = @entity AND m.metric_code = @metric
            ORDER BY o.start_date;
            """;

        var list = new List<SeriesPoint>();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Bind("@entity", entity);
        command.Bind("@metric", metric);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            list.Add(Series(reader, withSourceKind: true));
        }

        return list;
    }

    public IReadOnlyList<SeriesPoint> GetSeriesForEntities(string metric, IReadOnlyList<string> entities)
    {
        var list = new List<SeriesPoint>();
        using var connection = Open();
        const string sql = """
            SELECT e.entity_code, m.metric_code, o.start_date, o.val, o.unit, o.period_str, o.source_kind
            FROM v_observations_all o
            JOIN dbe_entities e ON o.entity_id = e.entity_id
            JOIN dbe_metrics m ON o.metric_id = m.metric_id
            WHERE m.metric_code = @metric AND e.entity_code = @entity
            ORDER BY e.entity_code, o.start_date;
            """;

        foreach (var entity in entities)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Bind("@metric", metric);
            command.Bind("@entity", entity);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                list.Add(Series(reader, withSourceKind: false));
            }
        }

        return list;
    }

    public IReadOnlyList<LatestSnapshotRow> GetLatestSnapshot(string? entity, string? metric, string? category, int limit)
    {
        limit = Math.Clamp(limit, 1, 1000);
        using var connection = Open();
        var sql = new System.Text.StringBuilder("""
            SELECT country_code, country_name, metric_code, metric_name, category, frequency, base_unit, value, period, source_kind
            FROM v_latest_metrics_snapshot
            WHERE 1=1
            """);

        if (!string.IsNullOrEmpty(entity))
        {
            sql.Append(" AND country_code = @entity");
        }

        if (!string.IsNullOrEmpty(metric))
        {
            sql.Append(" AND metric_code = @metric");
        }

        if (!string.IsNullOrEmpty(category))
        {
            sql.Append(" AND category = @category");
        }

        sql.Append(" ORDER BY country_code, metric_code LIMIT @limit;");

        var list = new List<LatestSnapshotRow>();
        using var command = connection.CreateCommand();
        command.CommandText = sql.ToString();
        if (!string.IsNullOrEmpty(entity))
        {
            command.Bind("@entity", entity);
        }

        if (!string.IsNullOrEmpty(metric))
        {
            command.Bind("@metric", metric);
        }

        if (!string.IsNullOrEmpty(category))
        {
            command.Bind("@category", category);
        }

        command.Bind("@limit", limit);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            list.Add(Snapshot(reader));
        }

        return list;
    }

    public IReadOnlyList<LatestSnapshotRow> RankEntities(string metric, string direction, int limit)
    {
        limit = Math.Clamp(limit, 1, 1000);
        var order = string.Equals(direction, "desc", StringComparison.OrdinalIgnoreCase) ? "DESC" : "ASC";
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT v.country_code, v.country_name, v.metric_code, v.metric_name, v.category, v.frequency,
                   v.base_unit, v.value, v.period, v.source_kind
            FROM v_latest_metrics_snapshot v
            JOIN dbe_entities e ON e.entity_code = v.country_code
            WHERE v.metric_code = @metric
              AND e.is_aggregate = 0
              AND v.country_name <> v.country_code
            ORDER BY v.value {order}
            LIMIT @limit;
            """;
        command.Bind("@metric", metric);
        command.Bind("@limit", limit);

        var list = new List<LatestSnapshotRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            list.Add(Snapshot(reader));
        }

        return list;
    }

    public IReadOnlyList<EntitySummary> GetEntities(string? q, string? type, int limit)
    {
        limit = Math.Clamp(limit, 1, 1000);
        using var connection = Open();
        var sql = new System.Text.StringBuilder("""
            SELECT
                e.entity_code,
                e.entity_name,
                e.entity_type,
                e.is_aggregate,
                (SELECT COUNT(*) FROM dbe_entity_aliases a WHERE a.entity_id = e.entity_id) AS alias_count,
                (SELECT COUNT(*) FROM v_observations_all o WHERE o.entity_id = e.entity_id) AS observation_count
            FROM dbe_entities e
            WHERE e.is_active = 1
            """);

        if (!string.IsNullOrEmpty(q))
        {
            sql.Append(" AND (e.entity_code LIKE '%' || @q || '%' OR e.entity_name LIKE '%' || @q || '%')");
        }

        if (!string.IsNullOrEmpty(type))
        {
            sql.Append(" AND e.entity_type = @type");
        }

        sql.Append(" ORDER BY e.entity_code LIMIT @limit;");

        var list = new List<EntitySummary>();
        using var command = connection.CreateCommand();
        command.CommandText = sql.ToString();
        if (!string.IsNullOrEmpty(q))
        {
            command.Bind("@q", q);
        }

        if (!string.IsNullOrEmpty(type))
        {
            command.Bind("@type", type);
        }

        command.Bind("@limit", limit);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new EntitySummary(
                EntityCode: reader.GetString(0),
                EntityName: reader.GetString(1),
                EntityType: reader.GetString(2),
                IsAggregate: Int(reader, 3),
                AliasCount: Int(reader, 4),
                ObservationCount: Int(reader, 5)));
        }

        return list;
    }

    public IReadOnlyList<RunSummary> GetRuns(int limit)
    {
        limit = Math.Clamp(limit, 1, 1000);
        using var connection = Open();
        const string sql = """
            SELECT run_id, run_timestamp, status, execution_seconds, metrics_extracted, observations_upserted, observations_skipped, tiers_written
            FROM dbe_pipeline_runs
            ORDER BY run_id DESC
            LIMIT @limit;
            """;

        var list = new List<RunSummary>();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Bind("@limit", limit);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new RunSummary(
                RunId: Long(reader, 0),
                RunTimestamp: reader.GetString(1),
                Status: reader.GetString(2),
                ExecutionSeconds: reader.GetDouble(3),
                MetricsExtracted: Int(reader, 4),
                ObservationsUpserted: Int(reader, 5),
                ObservationsSkipped: Int(reader, 6),
                TiersWritten: reader.IsDBNull(7) ? null : reader.GetString(7)));
        }

        return list;
    }

    private static SeriesPoint Series(System.Data.Common.DbDataReader reader, bool withSourceKind)
        => new(
            EntityCode: reader.GetString(0),
            MetricCode: reader.GetString(1),
            Period: reader.GetString(2),
            Value: reader.GetDouble(3),
            Unit: reader.IsDBNull(4) ? null : reader.GetString(4),
            PeriodStr: reader.IsDBNull(5) ? null : reader.GetString(5),
            SourceKind: withSourceKind ? reader.IsDBNull(6) ? "raw" : reader.GetString(6) : "raw");

    private static LatestSnapshotRow Snapshot(System.Data.Common.DbDataReader reader)
        => new(
            CountryCode: reader.GetString(0),
            CountryName: reader.GetString(1),
            MetricCode: reader.GetString(2),
            MetricName: reader.GetString(3),
            Category: reader.GetString(4),
            Frequency: reader.GetString(5),
            BaseUnit: reader.GetString(6),
            Value: reader.GetDouble(7),
            Period: reader.GetString(8),
            SourceKind: reader.IsDBNull(9) ? "raw" : reader.GetString(9));

    // DuckDB surfaces counts as int while SQLite may surface them as long.
    private static int Int(System.Data.Common.DbDataReader reader, int ordinal)
        => Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static long Long(System.Data.Common.DbDataReader reader, int ordinal)
        => Convert.ToInt64(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static int ScalarInt(AnalyticsConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static long ScalarLong(AnalyticsConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
}
