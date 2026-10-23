using System.Globalization;
using System.Text;
using System.Text.Json;
using DuckDB.NET.Data;
using EconData.Core.Configuration;
using EconData.Core.Data;
using EconData.Core.Entities;
using EconData.Core.Transform;
using Microsoft.Data.Sqlite;
using Serilog;

namespace EconData.Core.Lake;

/// <summary>
/// Gold tier: the DuckDB serving database. Owns schema bootstrap, idempotent upserts,
/// derived-aggregation SQL, and the run audit log. Read-only access goes through
/// <see cref="OpenReadOnly"/> so analytics and the vector catalog share one read path.
/// </summary>
public sealed class GoldTier
{
    // DuckDB caps bound parameters per statement; stay well under it.
    private const int MaxParametersPerStatement = 900;

    private readonly string _dbPath;
    private readonly string _schemaPath;
    private readonly ILogger _logger;

    public GoldTier(string dbPath, string schemaPath, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaPath);
        ArgumentNullException.ThrowIfNull(logger);
        _dbPath = Path.GetFullPath(dbPath);
        _schemaPath = schemaPath;
        _logger = logger;
    }

    public string DatabasePath => _dbPath;

    public bool Exists => File.Exists(_dbPath);

    public DuckDBConnection Open()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        var connection = new DuckDBConnection($"Data Source={_dbPath}");
        connection.Open();
        return connection;
    }

    /// <summary>Read-only connection for analytics and the vector catalog.</summary>
    public DuckDBConnection OpenReadOnly()
    {
        var connection = new DuckDBConnection($"Data Source={_dbPath};ACCESS_MODE=READ_ONLY");
        connection.Open();
        return connection;
    }

    public void EnsureSchema()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = File.ReadAllText(_schemaPath);
        command.ExecuteNonQuery();
    }

    // ---- Registry seeding ----

    public void SeedEntities(IReadOnlyList<EntitySeedRow> entities, IReadOnlyList<EntitySeedAlias> aliases)
    {
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(aliases);

        Upsert(
            "dbe_entities",
            new[] { "entity_code", "entity_name", "entity_type", "is_aggregate" },
            entities.Select(e => new object?[] { e.EntityCode, e.EntityName, e.EntityType, e.IsAggregate }).ToList(),
            new[] { "entity_code" },
            new[] { "entity_name", "entity_type", "is_aggregate" });

        var parents = entities.Where(e => !string.IsNullOrEmpty(e.ParentEntityCode)).ToList();
        if (parents.Count > 0)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            foreach (var entity in parents)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    "UPDATE dbe_entities SET parent_entity_id = (SELECT p.entity_id FROM dbe_entities p WHERE p.entity_code = ?) " +
                    "WHERE entity_code = ?";
                Bind(command, entity.ParentEntityCode);
                Bind(command, entity.EntityCode);
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        var ids = LoadEntityCodeToId();
        var aliasRows = aliases
            .Where(a => ids.ContainsKey(a.EntityCode))
            .Select(a => new object?[] { ids[a.EntityCode], a.Provider, a.ProviderCode })
            .ToList();
        Upsert(
            "dbe_entity_aliases",
            new[] { "entity_id", "provider", "provider_code" },
            aliasRows,
            new[] { "provider", "provider_code" },
            new[] { "entity_id" });

        _logger.Information("Seeded {Entities} entities and {Aliases} aliases into gold.", entities.Count, aliasRows.Count);
    }

    public void SeedMemberships(IReadOnlyDictionary<string, IReadOnlyList<string>> memberships)
    {
        ArgumentNullException.ThrowIfNull(memberships);
        var ids = LoadEntityCodeToId();
        var rows = new List<object?[]>();
        foreach (var (aggregate, members) in memberships)
        {
            if (!ids.TryGetValue(aggregate, out var aggregateId))
            {
                continue;
            }

            foreach (var member in members)
            {
                if (ids.TryGetValue(member, out var memberId))
                {
                    rows.Add(new object?[] { aggregateId, memberId, "1900-01-01" });
                }
            }
        }

        if (rows.Count == 0)
        {
            return;
        }

        Upsert(
            "dbe_entity_membership",
            new[] { "aggregate_entity_id", "member_entity_id", "start_date" },
            rows,
            new[] { "aggregate_entity_id", "member_entity_id", "start_date" },
            Array.Empty<string>());
        _logger.Information("Seeded {Count} aggregate memberships into gold.", rows.Count);
    }

    public void UpsertMetrics(IReadOnlyList<MetricRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        Upsert(
            "dbe_metrics",
            new[] { "metric_code", "metric_name", "category", "frequency", "base_unit", "unit_normalization", "description", "is_aggregatable", "aggregation_method" },
            records.Select(r => new object?[]
            {
                r.MetricCode, r.MetricName, r.Category, r.Frequency, r.BaseUnit,
                r.UnitNormalization, r.Description, r.IsAggregatable ? 1 : 0, r.AggregationMethod,
            }).ToList(),
            new[] { "metric_code" },
            new[] { "metric_name", "category", "frequency", "base_unit", "unit_normalization", "description", "is_aggregatable", "aggregation_method" });

        var ids = LoadMetricCodeToId();
        var synonymRows = new List<object?[]>();
        foreach (var record in records)
        {
            if (!ids.TryGetValue(record.MetricCode, out var metricId))
            {
                continue;
            }

            synonymRows.AddRange(record.Synonyms.Select(s => new object?[] { metricId, s }));
        }

        if (synonymRows.Count > 0)
        {
            Upsert(
                "dbe_metric_synonyms",
                new[] { "metric_id", "synonym" },
                synonymRows,
                new[] { "metric_id", "synonym" },
                Array.Empty<string>());
        }
    }

    public void UpsertSources(IReadOnlyList<MetricRecord> records, IReadOnlyDictionary<string, int> metricIds)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(metricIds);

        var rows = records
            .Where(r => metricIds.ContainsKey(r.MetricCode))
            .Select(r => new object?[] { r.Provider, r.Dataset, r.IndicatorCode, metricIds[r.MetricCode], r.DimensionKey, r.EntityScope })
            .ToList();

        Upsert(
            "dbe_sources",
            new[] { "provider", "dataset", "indicator_code", "metric_id", "dimension_key", "entity_scope" },
            rows,
            new[] { "provider", "dataset", "indicator_code" },
            new[] { "metric_id", "dimension_key", "entity_scope" });
    }

    public (IReadOnlyDictionary<string, int> MetricIds, IReadOnlyDictionary<string, int> SourceIds) GetMetricAndSourceIds()
    {
        var metricIds = LoadMetricCodeToId();
        var sourceIds = new Dictionary<string, int>(StringComparer.Ordinal);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT s.source_id, m.metric_code FROM dbe_sources s JOIN dbe_metrics m ON m.metric_id = s.metric_id";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            sourceIds[reader.GetString(1)] = reader.GetInt32(0);
        }

        return (metricIds, sourceIds);
    }

    // ---- Observations ----

    public int UpsertObservations(string metricCode, IReadOnlyList<ObservationRecord> observations, int metricId, int sourceId)
    {
        ArgumentNullException.ThrowIfNull(observations);
        if (observations.Count == 0)
        {
            return 0;
        }

        var codeToId = LoadEntityCodeToId();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var missing = new List<object?[]>();
        foreach (var observation in observations)
        {
            if (!codeToId.ContainsKey(observation.EntityCode) && seen.Add(observation.EntityCode))
            {
                missing.Add(new object?[]
                {
                    observation.EntityCode,
                    string.IsNullOrEmpty(observation.EntityName) ? observation.EntityCode : observation.EntityName,
                    "country",
                    0,
                });
            }
        }

        if (missing.Count > 0)
        {
            Upsert(
                "dbe_entities",
                new[] { "entity_code", "entity_name", "entity_type", "is_aggregate" },
                missing,
                new[] { "entity_code" },
                new[] { "entity_name", "entity_type", "is_aggregate" });
            codeToId = LoadEntityCodeToId();
            _logger.Information("Auto-registered {Count} new entities in gold for '{Metric}'.", missing.Count, metricCode);
        }

        var rows = new List<object?[]>();
        foreach (var observation in observations)
        {
            if (!codeToId.TryGetValue(observation.EntityCode, out var entityId))
            {
                continue;
            }

            rows.Add(new object?[]
            {
                metricId,
                entityId,
                sourceId,
                Date(observation.StartDate),
                Date(observation.EndDate),
                observation.Val,
                observation.RawVal,
                observation.Unit,
                observation.PeriodStr,
                Timestamp(observation.ExtractedAt),
            });
        }

        var written = Upsert(
            "dbe_observations",
            new[] { "metric_id", "entity_id", "source_id", "start_date", "end_date", "val", "raw_val", "unit", "period_str", "extracted_at" },
            rows,
            new[] { "metric_id", "entity_id", "start_date", "end_date" },
            new[] { "val", "raw_val", "unit", "period_str", "extracted_at" });

        _logger.Information("Upserted {Count} observations for '{Metric}' into gold.", written, metricCode);
        return written;
    }

    public int UpsertDerivedObservations(int metricId, IReadOnlyList<object?[]> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
        {
            return 0;
        }

        return Upsert(
            "dbe_derived_observations",
            new[] { "metric_id", "entity_id", "start_date", "end_date", "val", "unit", "aggregation_method", "member_count", "covered_count" },
            rows.Select(r => new object?[] { metricId, r[0], r[1], r[2], r[3], r[4], r[5], r[6], r[7] }).ToList(),
            new[] { "metric_id", "entity_id", "start_date", "end_date" },
            new[] { "val", "unit", "aggregation_method", "member_count", "covered_count", "computed_at" });
    }

    // ---- Import from SQLite (one-way backfill into gold) ----


    private void ImportEntities(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT entity_code, entity_name, entity_type, is_aggregate FROM dbe_entities";
        using var reader = command.ExecuteReader();

        var rows = new List<object?[]>();
        while (reader.Read())
        {
            rows.Add(new object?[]
            {
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
            });
        }

        Upsert("dbe_entities",
            new[] { "entity_code", "entity_name", "entity_type", "is_aggregate" },
            rows,
            new[] { "entity_code" },
            new[] { "entity_name", "entity_type", "is_aggregate" });
    }

    private Dictionary<string, int> ImportMetrics(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT metric_code, metric_name, category, frequency, base_unit, unit_normalization, description, is_aggregatable, aggregation_method FROM dbe_metrics";
        using var reader = command.ExecuteReader();

        var rows = new List<object?[]>();
        while (reader.Read())
        {
            rows.Add(new object?[]
            {
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetInt32(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
            });
        }

        Upsert("dbe_metrics",
            new[] { "metric_code", "metric_name", "category", "frequency", "base_unit", "unit_normalization", "description", "is_aggregatable", "aggregation_method" },
            rows,
            new[] { "metric_code" },
            new[] { "metric_name", "category", "frequency", "base_unit", "unit_normalization", "description", "is_aggregatable", "aggregation_method" });

        return LoadMetricCodeToId();
    }

    private void ImportSources(
        SqliteConnection connection,
        IReadOnlyDictionary<int, string> sqliteMetricCodes,
        IReadOnlyDictionary<string, int> goldMetricIds)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT provider, dataset, indicator_code, metric_id, dimension_key, entity_scope FROM dbe_sources";
        using var reader = command.ExecuteReader();

        var rows = new List<object?[]>();
        while (reader.Read())
        {
            if (!sqliteMetricCodes.TryGetValue(reader.GetInt32(3), out var metricCode)) continue;
            if (!goldMetricIds.TryGetValue(metricCode, out var goldMetricId)) continue;

            rows.Add(new object?[]
            {
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                goldMetricId,
                reader.GetString(4),
                reader.GetString(5),
            });
        }

        Upsert("dbe_sources",
            new[] { "provider", "dataset", "indicator_code", "metric_id", "dimension_key", "entity_scope" },
            rows,
            new[] { "provider", "dataset", "indicator_code" },
            new[] { "metric_id", "dimension_key", "entity_scope" });
    }

    private void ImportObservations(
        SqliteConnection connection,
        IReadOnlyDictionary<int, string> sqliteMetricCodes,
        IReadOnlyDictionary<string, int> goldMetricIds,
        IReadOnlyDictionary<int, string> sqliteEntityCodes,
        IReadOnlyDictionary<string, int> goldEntityIds,
        IReadOnlyDictionary<int, string> sqliteSourceKeys,
        IReadOnlyDictionary<string, int> goldSourceIds)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT metric_id, entity_id, source_id, start_date, end_date, val, raw_val, unit, period_str, extracted_at FROM dbe_observations";
        using var reader = command.ExecuteReader();

        var rows = new List<object?[]>();
        while (reader.Read())
        {
            if (!sqliteMetricCodes.TryGetValue(reader.GetInt32(0), out var metricCode)) continue;
            if (!goldMetricIds.TryGetValue(metricCode, out var metricId)) continue;
            if (!sqliteEntityCodes.TryGetValue(reader.GetInt32(1), out var entityCode)) continue;
            if (!goldEntityIds.TryGetValue(entityCode, out var entityId)) continue;
            if (!sqliteSourceKeys.TryGetValue(reader.GetInt32(2), out var sourceKey)) continue;
            if (!goldSourceIds.TryGetValue(sourceKey, out var sourceId)) continue;

            rows.Add(new object?[]
            {
                metricId,
                entityId,
                sourceId,
                reader.GetString(3),
                reader.GetString(4),
                reader.GetDouble(5),
                reader.IsDBNull(6) ? null : reader.GetDouble(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.GetString(9),
            });
        }

        Upsert("dbe_observations",
            new[] { "metric_id", "entity_id", "source_id", "start_date", "end_date", "val", "raw_val", "unit", "period_str", "extracted_at" },
            rows,
            new[] { "metric_id", "entity_id", "start_date", "end_date" },
            new[] { "val", "raw_val", "unit", "period_str", "extracted_at" });
    }

    private long CountObservations(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM dbe_observations";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    // ---- Internal ----

    public void RecordRun(
        string dagId,
        string taskId,
        int metricsExtracted,
        int observationsUpserted,
        int observationsSkipped,
        IReadOnlyList<string> providers,
        IReadOnlyList<int> sourceIds,
        string status,
        double executionSeconds,
        string tiersWritten)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(sourceIds);

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO dbe_pipeline_runs (dag_id, task_id, metrics_extracted, observations_upserted, observations_skipped, " +
            "providers, source_ids, status, execution_seconds, tiers_written) " +
            "VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)";
        Bind(command, dagId);
        Bind(command, taskId);
        Bind(command, metricsExtracted);
        Bind(command, observationsUpserted);
        Bind(command, observationsSkipped);
        Bind(command, JsonSerializer.Serialize(providers));
        Bind(command, JsonSerializer.Serialize(sourceIds));
        Bind(command, status);
        Bind(command, executionSeconds);
        Bind(command, tiersWritten);
        command.ExecuteNonQuery();
    }

    // ---- Derived aggregation (sum/mean over member entities, >=50% coverage) ----

    /// <summary>
    /// Recomputes derived aggregate rows for one metric. Mirrors the SQLite
    /// <c>DerivedAggregator</c> semantics exactly: unweighted sum/mean, a row is emitted
    /// only when covering members are at least half of members-with-data, and a member
    /// counts once per (start,end) bucket.
    /// </summary>
    public int Aggregate(string metricCode, string method, string unit, IReadOnlyDictionary<string, int> metricIds)
    {
        ArgumentNullException.ThrowIfNull(metricIds);
        if (!metricIds.TryGetValue(metricCode, out var metricId))
        {
            return 0;
        }

        var function = string.Equals(method, "mean", StringComparison.OrdinalIgnoreCase) ? "avg" : "sum";

        using var connection = Open();

        // Members with data for this metric, and the memberships they belong to.
        var rows = new List<object?[]>();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            WITH members_with_data AS (
                SELECT DISTINCT o.entity_id AS entity_id
                FROM dbe_observations o
                WHERE o.metric_id = ?
            ),
            pairs AS (
                SELECT
                    mem.aggregate_entity_id AS agg_id,
                    mem.member_entity_id AS member_id,
                    o.start_date AS start_date,
                    o.end_date AS end_date,
                    o.val AS val
                FROM dbe_entity_membership mem
                JOIN dbe_observations o ON o.entity_id = mem.member_entity_id
                JOIN members_with_data mwd ON mwd.entity_id = mem.member_entity_id
                WHERE o.metric_id = ?
            ),
            totals AS (
                SELECT p.agg_id AS agg_id,
                       COUNT(DISTINCT p.member_id) AS member_count
                FROM pairs p
                GROUP BY p.agg_id
            ),
            buckets AS (
                SELECT p.agg_id AS agg_id,
                       p.start_date AS start_date,
                       p.end_date AS end_date,
                       {function}(p.val) AS val,
                       COUNT(DISTINCT p.member_id) AS covered
                FROM pairs p
                GROUP BY p.agg_id, p.start_date, p.end_date
            )
            SELECT b.agg_id, b.start_date, b.end_date, b.val, b.covered, t.member_count
            FROM buckets b
            JOIN totals t ON t.agg_id = b.agg_id
            WHERE b.covered * 2 >= t.member_count
            """;
        Bind(command, metricId);
        Bind(command, metricId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new object?[]
            {
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetDouble(3),
                unit,
                function,
                reader.GetInt32(5),
                reader.GetInt32(4),
            });
        }

        var written = UpsertDerivedObservations(metricId, rows);
        _logger.Information("Derived aggregation for '{Metric}' ({Method}): {Rows} rows.", metricCode, function, written);
        return written;
    }

    /// <summary>Rebuilds gold from silver partitions into this instance's database file.</summary>
    public async Task<IReadOnlyDictionary<string, long>> RebuildFromSilverAsync(
        EntitySeed seed,
        MetricsCatalog catalog,
        SilverTier silver,
        IReadOnlyList<string> metricCodes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(seed);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(silver);
        ArgumentNullException.ThrowIfNull(metricCodes);

        EnsureSchema();
        SeedEntities(seed.Entities, seed.Aliases);
        SeedMemberships(seed.Memberships);

        var records = DataTransformer.CreateMetricsRecords(catalog);
        UpsertMetrics(records);
        var (metricIds, _) = GetMetricAndSourceIds();
        UpsertSources(records, metricIds);
        (metricIds, var sourceIds) = GetMetricAndSourceIds();

        var counts = new SortedDictionary<string, long>(StringComparer.Ordinal);
        var total = 0;
        var providers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var code in metricCodes)
        {
            var metric = catalog.Get(code);
            if (metric is null) continue;
            providers.Add(metric.Provider);

            if (!metricIds.TryGetValue(code, out var metricId)
                || !sourceIds.TryGetValue(code, out var sourceId))
            {
                continue;
            }

            var rows = await silver.ReadAsync(code, cancellationToken).ConfigureAwait(false);
            var written = UpsertObservations(code, rows, metricId, sourceId);
            counts[code] = written;
            total += written;

            if (string.Equals(metric.Aggregation, "sum", StringComparison.OrdinalIgnoreCase)
                || string.Equals(metric.Aggregation, "mean", StringComparison.OrdinalIgnoreCase))
            {
                Aggregate(code, metric.Aggregation, metric.EffectiveBaseUnit, metricIds);
            }
        }

        RecordRun(
            "econdata_manual",
            "rebuild_gold",
            counts.Count,
            total,
            0,
            providers.ToList(),
            sourceIds.Values.ToList(),
            "SUCCESS",
            0,
            "G");

        _logger.Information("Gold rebuilt from {Partitions} silver partitions: {Total} observations.", counts.Count, total);
        return counts;
    }

    // ---- Import from SQLite (one-way backfill into gold) ----

    public void ImportFromSqlite(SqliteConnectionFactory sqliteFactory)
    {
        ArgumentNullException.ThrowIfNull(sqliteFactory);

        using var connection = sqliteFactory.OpenReadOnly();

        // Natural-key maps: SQLite surrogate ids are never copied into gold.
        var sqliteMetricCodes = ReadIdMap(connection, "SELECT metric_id, metric_code FROM dbe_metrics");
        var sqliteEntityCodes = ReadIdMap(connection, "SELECT entity_id, entity_code FROM dbe_entities");
        var sqliteSourceKeys = ReadSourceKeyMap(connection);

        ImportEntities(connection);
        ImportMetrics(connection);

        var goldMetricIds = LoadMetricCodeToId();
        ImportSources(connection, sqliteMetricCodes, goldMetricIds);

        var goldEntityIds = LoadEntityCodeToId();
        var goldSourceIds = LoadSourceKeyToId();
        ImportObservations(connection, sqliteMetricCodes, goldMetricIds, sqliteEntityCodes, goldEntityIds, sqliteSourceKeys, goldSourceIds);

        _logger.Information(
            "Import from SQLite complete: {MetricCount} metrics, {ObservationCount} observations. " +
            "Derived aggregates are recomputed by the normal pipeline (extract --gold / lake build-gold).",
            goldMetricIds.Count, CountObservations(connection));
    }

    private static Dictionary<int, string> ReadIdMap(SqliteConnection connection, string sql)
    {
        var map = new Dictionary<int, string>();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            map[reader.GetInt32(0)] = reader.GetString(1);
        }

        return map;
    }

    private static Dictionary<int, string> ReadSourceKeyMap(SqliteConnection connection)
    {
        var map = new Dictionary<int, string>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT source_id, provider, dataset, indicator_code FROM dbe_sources";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            map[reader.GetInt32(0)] = SourceKey(reader.GetString(1), reader.GetString(2), reader.GetString(3));
        }

        return map;
    }

    private static string SourceKey(string provider, string dataset, string indicator)
        => provider + "\u001f" + dataset + "\u001f" + indicator;

    private Dictionary<string, int> LoadSourceKeyToId()
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT source_id, provider, dataset, indicator_code FROM dbe_sources";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            map[SourceKey(reader.GetString(1), reader.GetString(2), reader.GetString(3))] = reader.GetInt32(0);
        }

        return map;
    }


    private static string Date(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Timestamp(DateTimeOffset value)
        => value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private Dictionary<string, int> LoadEntityCodeToId() => LoadCodeToId("SELECT entity_id, entity_code FROM dbe_entities");

    private Dictionary<string, int> LoadMetricCodeToId() => LoadCodeToId("SELECT metric_id, metric_code FROM dbe_metrics");

    private Dictionary<string, int> LoadCodeToId(string sql)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            map[reader.GetString(1)] = reader.GetInt32(0);
        }

        return map;
    }

    private int Upsert(string table, string[] columns, IReadOnlyList<object?[]> rows, string[] conflictColumns, string[] updateColumns)
    {
        if (rows.Count == 0)
        {
            return 0;
        }

        var conflict = string.Join(", ", conflictColumns);
        var action = updateColumns.Length == 0
            ? "DO NOTHING"
            : "DO UPDATE SET " + string.Join(", ", updateColumns.Select(c => $"{c} = excluded.{c}"));
        var header = $"INSERT INTO {table} ({string.Join(", ", columns)}) VALUES ";
        var chunkSize = Math.Max(1, MaxParametersPerStatement / columns.Length);

        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var total = 0;
        for (var offset = 0; offset < rows.Count; offset += chunkSize)
        {
            var count = Math.Min(chunkSize, rows.Count - offset);
            var sql = new StringBuilder(header);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            for (var i = 0; i < count; i++)
            {
                if (i > 0)
                {
                    sql.Append(", ");
                }

                sql.Append('(');
                for (var c = 0; c < columns.Length; c++)
                {
                    if (c > 0)
                    {
                        sql.Append(", ");
                    }

                    // DuckDB.NET only binds positional '?' placeholders, in order.
                    sql.Append('?');
                    Bind(command, rows[offset + i][c]);
                }

                sql.Append(')');
            }

            sql.Append($" ON CONFLICT ({conflict}) {action};");
            command.CommandText = sql.ToString();
            command.ExecuteNonQuery();
            total += count;
        }

        transaction.Commit();
        return total;
    }
    // ---- Internal ----

    private static void Bind(DuckDBCommand command, string name, object? value)
        => command.Parameters.Add(new DuckDBParameter { ParameterName = name, Value = value ?? DBNull.Value });

    /// <summary>Positional bind for the positional '?' placeholders DuckDB.NET requires.</summary>
    private static void Bind(DuckDBCommand command, object? value)
        => command.Parameters.Add(new DuckDBParameter { Value = value ?? DBNull.Value });

}
