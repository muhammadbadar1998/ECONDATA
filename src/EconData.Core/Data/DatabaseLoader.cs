using System.Globalization;
using System.Text;
using System.Text.Json;
using EconData.Core.Entities;
using Microsoft.Data.Sqlite;
using Serilog;

namespace EconData.Core.Data;

/// <summary>
/// Loads the normalized dbe_* tables into SQLite. Faithful port of the former
/// PostgreSQL loader: idempotent UPSERTs, self-seeding entities, and an audit log.
/// </summary>
public sealed class DatabaseLoader
{
    // SQLite caps bound parameters per statement (32766 in modern builds here);
    // stay well under it by splitting large UPSERTs into chunks.
    private const int MaxParametersPerStatement = 900;

    private readonly SqliteConnectionFactory _factory;
    private readonly SchemaInitializer _schema;
    private readonly string _schemaScriptPath;
    private readonly ILogger _logger;

    public DatabaseLoader(SqliteConnectionFactory factory, string schemaScriptPath, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaScriptPath);
        ArgumentNullException.ThrowIfNull(logger);
        _factory = factory;
        _schemaScriptPath = schemaScriptPath;
        _logger = logger;
        _schema = new SchemaInitializer(factory, logger);
    }

    public bool VerifyOrInitSchema() => _schema.Initialize(_schemaScriptPath);

    public bool CheckSchema(out string message) => _schema.Verify(out message);

    // ---- Entities / aliases ----
    public void SeedEntities(IReadOnlyList<EntitySeedRow> entities, IReadOnlyList<EntitySeedAlias> aliases)
    {
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(aliases);

        var entityRows = entities
            .Select(e => new object?[] { e.EntityCode, e.EntityName, e.EntityType, e.IsAggregate })
            .ToList();
        Upsert(
            "dbe_entities",
            new[] { "entity_code", "entity_name", "entity_type", "is_aggregate" },
            entityRows,
            new[] { "entity_code" },
            new[] { "entity_name", "entity_type", "is_aggregate" });

        var parents = entities.Where(e => !string.IsNullOrEmpty(e.ParentEntityCode)).ToList();
        if (parents.Count > 0)
        {
            using var connection = _factory.Open();
            using var transaction = connection.BeginTransaction();
            foreach (var entity in parents)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    "UPDATE dbe_entities SET parent_entity_id = " +
                    "(SELECT entity_id FROM dbe_entities WHERE entity_code = @parent) WHERE entity_code = @child";
                command.Parameters.AddWithValue("@parent", entity.ParentEntityCode!);
                command.Parameters.AddWithValue("@child", entity.EntityCode);
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        Dictionary<string, int> codeToId;
        using (var connection = _factory.Open())
        {
            codeToId = LoadEntityCodeToId(connection);
        }

        var aliasRows = aliases
            .Where(alias => codeToId.ContainsKey(alias.EntityCode))
            .Select(alias => new object?[] { codeToId[alias.EntityCode], alias.Provider, alias.ProviderCode })
            .ToList();
        Upsert(
            "dbe_entity_aliases",
            new[] { "entity_id", "provider", "provider_code" },
            aliasRows,
            new[] { "provider", "provider_code" },
            new[] { "entity_id" });

        _logger.Information("Seeded {Entities} entities and {Aliases} aliases.", entities.Count, aliasRows.Count);
    }

    public void SeedMemberships(IReadOnlyDictionary<string, IReadOnlyList<string>> memberships)
    {
        ArgumentNullException.ThrowIfNull(memberships);

        Dictionary<string, int> codeToId;
        using (var connection = _factory.Open())
        {
            codeToId = LoadEntityCodeToId(connection);
        }

        var rows = new List<object?[]>();
        foreach (var (aggregate, members) in memberships)
        {
            if (!codeToId.TryGetValue(aggregate, out var aggregateId))
            {
                continue;
            }

            foreach (var member in members)
            {
                if (codeToId.TryGetValue(member, out var memberId))
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
        _logger.Information("Seeded {Count} aggregate memberships.", rows.Count);
    }

    // ---- Metrics + synonyms + sources ----
    public void UpsertMetrics(IReadOnlyList<MetricRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        var metricRows = records
            .Select(r => new object?[]
            {
                r.MetricCode, r.MetricName, r.Category, r.Frequency, r.BaseUnit,
                r.UnitNormalization, r.Description, r.IsAggregatable, r.AggregationMethod,
            })
            .ToList();
        Upsert(
            "dbe_metrics",
            new[] { "metric_code", "metric_name", "category", "frequency", "base_unit", "unit_normalization", "description", "is_aggregatable", "aggregation_method" },
            metricRows,
            new[] { "metric_code" },
            new[] { "metric_name", "category", "frequency", "base_unit", "unit_normalization", "description", "is_aggregatable", "aggregation_method" });

        Dictionary<string, int> codeToId;
        using (var connection = _factory.Open())
        {
            codeToId = LoadMetricCodeToId(connection);
        }

        var synonymRows = new List<object?[]>();
        foreach (var record in records)
        {
            if (!codeToId.TryGetValue(record.MetricCode, out var metricId))
            {
                continue;
            }

            foreach (var synonym in record.Synonyms)
            {
                synonymRows.Add(new object?[] { metricId, synonym });
            }
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

        _logger.Information("Upserted {Metrics} metrics and {Synonyms} synonyms.", records.Count, synonymRows.Count);
    }

    public void UpsertSources(IReadOnlyList<MetricRecord> records, IReadOnlyDictionary<string, int> metricIdByCode)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(metricIdByCode);

        var rows = new List<object?[]>();
        foreach (var record in records)
        {
            if (metricIdByCode.TryGetValue(record.MetricCode, out var metricId))
            {
                rows.Add(new object?[] { record.Provider, record.Dataset, record.IndicatorCode, metricId, record.DimensionKey, record.EntityScope });
            }
        }

        Upsert(
            "dbe_sources",
            new[] { "provider", "dataset", "indicator_code", "metric_id", "dimension_key", "entity_scope" },
            rows,
            new[] { "provider", "dataset", "indicator_code" },
            new[] { "metric_id", "dimension_key", "entity_scope" });
        _logger.Information("Upserted {Count} sources.", rows.Count);
    }

    public (IReadOnlyDictionary<string, int> MetricIds, IReadOnlyDictionary<string, int> SourceIds) GetMetricAndSourceIds()
    {
        using var connection = _factory.Open();
        var metricIds = LoadMetricCodeToId(connection);

        var sourceIds = new Dictionary<string, int>(StringComparer.Ordinal);
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT s.source_id, m.metric_code FROM dbe_sources s JOIN dbe_metrics m ON m.metric_id = s.metric_id";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            sourceIds[reader.GetString(1)] = reader.GetInt32(0);
        }

        _logger.Debug("Resolved {Metrics} metric ids and {Sources} source ids.", metricIds.Count, sourceIds.Count);
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

        Dictionary<string, int> codeToId;
        using (var connection = _factory.Open())
        {
            codeToId = LoadEntityCodeToId(connection);
        }

        // Auto-register entities not yet present (self-seeding universe).
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
                    false,
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
            using var connection = _factory.Open();
            codeToId = LoadEntityCodeToId(connection);
            _logger.Information("Auto-registered {Count} new entities for '{Metric}'.", missing.Count, metricCode);
        }

        var finalRows = new List<object?[]>();
        foreach (var observation in observations)
        {
            if (!codeToId.TryGetValue(observation.EntityCode, out var entityId))
            {
                continue;
            }

            finalRows.Add(new object?[]
            {
                metricId,
                entityId,
                sourceId,
                FormatDate(observation.StartDate),
                FormatDate(observation.EndDate),
                observation.Val,
                observation.RawVal,
                observation.Unit,
                observation.PeriodStr,
                FormatTimestamp(observation.ExtractedAt),
            });
        }

        Upsert(
            "dbe_observations",
            new[] { "metric_id", "entity_id", "source_id", "start_date", "end_date", "val", "raw_val", "unit", "period_str", "extracted_at" },
            finalRows,
            new[] { "metric_id", "entity_id", "start_date", "end_date" },
            new[] { "val", "raw_val", "unit", "period_str", "extracted_at" });

        _logger.Information("Upserted {Count} observations for '{Metric}'.", finalRows.Count, metricCode);
        return finalRows.Count;
    }

    // ---- Audit ----
    public void LogPipelineRun(
        string dagId,
        string taskId,
        int metricsExtracted,
        int observationsUpserted,
        int observationsSkipped,
        IReadOnlyList<string> providers,
        IReadOnlyList<int> sourceIds,
        string status,
        double executionSeconds)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(sourceIds);

        try
        {
            using var connection = _factory.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO dbe_pipeline_runs (dag_id, task_id, metrics_extracted, observations_upserted, observations_skipped, providers, source_ids, status, execution_seconds) " +
                "VALUES (@dag, @task, @metrics, @upserted, @skipped, @providers, @sources, @status, @seconds)";
            command.Parameters.AddWithValue("@dag", dagId);
            command.Parameters.AddWithValue("@task", taskId);
            command.Parameters.AddWithValue("@metrics", metricsExtracted);
            command.Parameters.AddWithValue("@upserted", observationsUpserted);
            command.Parameters.AddWithValue("@skipped", observationsSkipped);
            command.Parameters.AddWithValue("@providers", JsonSerializer.Serialize(providers));
            command.Parameters.AddWithValue("@sources", JsonSerializer.Serialize(sourceIds));
            command.Parameters.AddWithValue("@status", status);
            command.Parameters.AddWithValue("@seconds", executionSeconds);
            command.ExecuteNonQuery();
            _logger.Information("Pipeline run audit logged into dbe_pipeline_runs.");
        }
        catch (SqliteException ex)
        {
            _logger.Error(ex, "Failed to log pipeline run.");
        }
    }

    // ---- Internals ----
    private static string FormatDate(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string FormatTimestamp(DateTimeOffset value)
        => value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    private static Dictionary<string, int> LoadEntityCodeToId(SqliteConnection connection)
        => LoadCodeToId(connection, "SELECT entity_id, entity_code FROM dbe_entities");

    private static Dictionary<string, int> LoadMetricCodeToId(SqliteConnection connection)
        => LoadCodeToId(connection, "SELECT metric_id, metric_code FROM dbe_metrics");

    private static Dictionary<string, int> LoadCodeToId(SqliteConnection connection, string sql)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            map[reader.GetString(1)] = reader.GetInt32(0);
        }

        return map;
    }

    private int Upsert(
        string table,
        string[] columns,
        IReadOnlyList<object?[]> rows,
        string[] conflictColumns,
        string[] updateColumns)
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

        using var connection = _factory.Open();
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

                    var parameterName = $"@p{i}_{c}";
                    sql.Append(parameterName);
                    command.Parameters.AddWithValue(parameterName, rows[offset + i][c] ?? DBNull.Value);
                }

                sql.Append(')');
            }

            sql.Append($" ON CONFLICT ({conflict}) {action};");
            command.CommandText = sql.ToString();
            command.ExecuteNonQuery();
            total += count;
        }

        transaction.Commit();
        _logger.Debug("Upserted {Count} row(s) into {Table} (conflict: {Conflict}).", total, table, conflict);
        return total;
    }
}
