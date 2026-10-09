using EconData.Core.Ai;
using EconData.Core.Configuration;
using EconData.Core.Data;
using EconData.Core.Entities;
using Microsoft.Data.Sqlite;
using Serilog;
using Serilog.Core;

namespace EconData.Core.Catalog;

/// <summary>
/// Builds catalog documents from the normalized registry and observations, embeds
/// the changed ones via <see cref="IEmbedder"/>, persists them through
/// <see cref="VectorStore"/>, and serves semantic search.
/// </summary>
public sealed class Vectorizer
{
    public const int DefaultRecentPoints = 5;

    private static readonly (string Label, string ValueKey, string YearKey)[] SummaryFields =
    {
        ("Latest", "latest_val", "latest_year"),
        ("Min", "min_val", "min_val_year"),
        ("Max", "max_val", "max_val_year"),
    };

    private readonly MetricsCatalog _catalog;
    private readonly EntitySeed _seed;
    private readonly SqliteConnectionFactory _factory;
    private readonly VectorStore _store;
    private readonly IEmbedder _embedder;
    private readonly bool _forceReembed;
    private readonly ILogger _logger;

    public Vectorizer(
        MetricsCatalog catalog,
        EntitySeed seed,
        SqliteConnectionFactory factory,
        VectorStore store,
        IEmbedder embedder,
        bool forceReembed = false,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(seed);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(embedder);
        _catalog = catalog;
        _seed = seed;
        _factory = factory;
        _store = store;
        _embedder = embedder;
        _forceReembed = forceReembed;
        _logger = logger ?? Logger.None;
    }

    // ---- Document builders (pure, testable) ----
    public static IReadOnlyList<Document> BuildMetricDocuments(MetricsCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var documents = new List<Document>(catalog.ByCode.Count);
        foreach (var (code, info) in catalog.ByCode)
        {
            var parts = new List<string?>
            {
                info.MetricName,
                info.Synonyms.Count > 0 ? string.Join("\n", info.Synonyms) : null,
                info.Description,
            };
            var content = string.Join("\n", parts.Where(part => !string.IsNullOrEmpty(part))).Trim();
            documents.Add(new Document(code, "metric", content, new Dictionary<string, object?>
            {
                ["metric_code"] = code,
                ["category"] = info.Category,
                ["frequency"] = info.Frequency,
                ["base_unit"] = info.EffectiveBaseUnit,
                ["provider"] = info.Provider,
                ["dataset"] = info.Dataset,
                ["indicator_code"] = info.IndicatorCode,
            }));
        }

        return documents;
    }

    public static IReadOnlyList<Document> BuildEntityDocuments(IReadOnlyList<EntitySeedRow> entities)
    {
        ArgumentNullException.ThrowIfNull(entities);
        var documents = new List<Document>(entities.Count);
        foreach (var entity in entities)
        {
            var parts = new List<string> { entity.EntityName, $"Type: {entity.EntityType}", $"Aggregate: {entity.IsAggregate}" };
            if (!string.IsNullOrEmpty(entity.ParentEntityCode))
            {
                parts.Add($"Parent: {entity.ParentEntityCode}");
            }

            documents.Add(new Document(entity.EntityCode, "entity", string.Join("\n", parts).Trim(), new Dictionary<string, object?>
            {
                ["entity_code"] = entity.EntityCode,
                ["entity_type"] = entity.EntityType,
                ["is_aggregate"] = entity.IsAggregate,
            }));
        }

        return documents;
    }

    public static IReadOnlyList<Document> BuildEntityMetricDocuments(IEnumerable<IReadOnlyDictionary<string, object?>> coverageRows)
    {
        ArgumentNullException.ThrowIfNull(coverageRows);
        var documents = new List<Document>();
        foreach (var row in coverageRows)
        {
            var baseUnit = GetString(row, "base_unit");
            var recent = DocumentFormatting.ParseRecentPoints(GetValue(row, "recent_points"));

            var lines = new List<string>
            {
                GetString(row, "entity_name"),
                GetString(row, "metric_name"),
                $"Metric code: {GetString(row, "metric_code")}",
                $"Frequency: {GetString(row, "frequency")} Unit: {baseUnit}",
                $"Observations: {GetValue(row, "obs_count")}, range {GetValue(row, "min_year")}–{GetValue(row, "max_year")}",
            };

            foreach (var (label, valueKey, yearKey) in SummaryFields)
            {
                var value = GetValue(row, valueKey);
                if (value is not null)
                {
                    lines.Add($"{label}: {DocumentFormatting.FormatValue(value, baseUnit)} ({GetValue(row, yearKey)})");
                }
            }

            if (recent.Count > 0)
            {
                var recentText = string.Join(
                    "; ",
                    recent.Select(point => $"{point["year"]}={DocumentFormatting.FormatValue(point["value"], baseUnit)}"));
                lines.Add($"Recent: {recentText}");
            }

            var metadata = new Dictionary<string, object?>
            {
                ["entity_code"] = GetString(row, "entity_code"),
                ["entity_name"] = GetString(row, "entity_name"),
                ["metric_code"] = GetString(row, "metric_code"),
                ["metric_name"] = GetString(row, "metric_name"),
                ["frequency"] = GetString(row, "frequency"),
                ["base_unit"] = baseUnit,
                ["obs_count"] = GetValue(row, "obs_count"),
                ["min_year"] = GetValue(row, "min_year"),
                ["max_year"] = GetValue(row, "max_year"),
                ["latest_val"] = GetValue(row, "latest_val"),
                ["latest_year"] = GetValue(row, "latest_year"),
                ["min_val"] = GetValue(row, "min_val"),
                ["min_val_year"] = GetValue(row, "min_val_year"),
                ["max_val"] = GetValue(row, "max_val"),
                ["max_val_year"] = GetValue(row, "max_val_year"),
                ["recent_points"] = recent,
            };

            documents.Add(new Document(
                $"{GetString(row, "entity_code")}|{GetString(row, "metric_code")}",
                "entity_metric",
                string.Join("\n", lines).Trim(),
                metadata));
        }

        return documents;
    }

    // ---- Public API ----
    public IReadOnlyList<Document> BuildAllDocuments(int recentPoints = DefaultRecentPoints)
    {
        var metricDocuments = BuildMetricDocuments(_catalog);
        var entityDocuments = BuildEntityDocuments(_seed.Entities);
        var coverageDocuments = new List<Document>();
        try
        {
            coverageDocuments = BuildEntityMetricDocuments(FetchCoverage(recentPoints)).ToList();
        }
        catch (SqliteException ex)
        {
            _logger.Warning("Skipping entity_metric documents: {Message}", ex.Message);
        }

        var documents = new List<Document>(metricDocuments.Count + entityDocuments.Count + coverageDocuments.Count);
        documents.AddRange(metricDocuments);
        documents.AddRange(entityDocuments);
        documents.AddRange(coverageDocuments);

        _logger.Information(
            "Built {Total} documents ({Metric} metric, {Entity} entity, {Coverage} entity_metric).",
            documents.Count,
            metricDocuments.Count,
            entityDocuments.Count,
            coverageDocuments.Count);
        return documents;
    }

    public async Task<Dictionary<string, int>> VectorizeAllAsync(int recentPoints = DefaultRecentPoints, CancellationToken cancellationToken = default)
    {
        if (!await _embedder.VerifyConnectivityAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "Ollama is not reachable at the configured OLLAMA_HOST. " +
                "Run `ollama serve` and `ollama pull nomic-embed-text`.");
        }

        var documents = BuildAllDocuments(recentPoints);
        if (documents.Count == 0)
        {
            return Stats(0, 0, 0, 0);
        }

        var existing = _store.LoadExisting();
        var toEmbed = new List<Document>();
        foreach (var document in documents)
        {
            var changed = _forceReembed
                || !existing.TryGetValue((document.DocKey, document.DocType), out var stored)
                || stored.DocHash != document.DocHash;
            if (changed)
            {
                toEmbed.Add(document);
            }
        }

        _logger.Information(
            "Vectorization plan: {Total} document(s), {ToEmbed} to embed, {Current} already current.",
            documents.Count,
            toEmbed.Count,
            documents.Count - toEmbed.Count);

        var embedded = 0;
        if (toEmbed.Count > 0)
        {
            IReadOnlyList<float[]> vectors;
            try
            {
                vectors = await _embedder.EmbedAsync(toEmbed.Select(document => document.Content).ToList(), cancellationToken).ConfigureAwait(false);
            }
            catch (EmbeddingException ex)
            {
                _logger.Error("Embedding failed: {Message}", ex.Message);
                return Stats(0, 0, 0, toEmbed.Count);
            }

            _store.Upsert(toEmbed, vectors);
            embedded = toEmbed.Count;
        }

        var currentKeys = documents.Select(document => (document.DocKey, document.DocType)).ToHashSet();
        var orphans = existing
            .Where(entry => !currentKeys.Contains(entry.Key))
            .Select(entry => (entry.Key.DocKey, entry.Key.DocType, entry.Value.DocId))
            .ToList();
        if (orphans.Count > 0)
        {
            _store.DeleteOrphans(orphans);
        }

        return Stats(embedded, embedded, orphans.Count, 0);
    }

    public async Task<IReadOnlyList<SearchHit>> SearchDocumentsAsync(string query, int k = 10, CancellationToken cancellationToken = default)
    {
        var queryVector = await _embedder.EmbedQueryAsync(query, cancellationToken).ConfigureAwait(false);
        var hits = _store.Search(queryVector, k);
        if (hits.Count > 0)
        {
            _logger.Debug("Search returned {Count} document(s); top similarity={Similarity:F4}.", hits.Count, hits[0].Similarity);
        }

        return hits;
    }

    // ---- Coverage query ----
    private List<Dictionary<string, object?>> FetchCoverage(int recentPoints)
    {
        using var connection = _factory.Open();
        var metrics = LoadLookup(connection, "SELECT metric_id, metric_code, metric_name, frequency, base_unit FROM dbe_metrics");
        var entities = LoadLookup(connection, "SELECT entity_id, entity_code, entity_name FROM dbe_entities");

        var baseRows = new List<BaseObservation>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT o.metric_id, o.entity_id, o.start_date, CAST(strftime('%Y', o.start_date) AS INTEGER) AS year, o.val " +
                "FROM dbe_observations o JOIN dbe_entities e ON e.entity_id = o.entity_id " +
                "WHERE e.is_active = 1 AND o.val IS NOT NULL";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                baseRows.Add(new BaseObservation(reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2), reader.GetInt32(3), reader.GetDouble(4)));
            }
        }

        var take = Math.Max(1, recentPoints);
        var rows = new List<Dictionary<string, object?>>();
        foreach (var group in baseRows.GroupBy(row => (row.MetricId, row.EntityId)))
        {
            if (!metrics.TryGetValue(group.Key.MetricId, out var metric) || !entities.TryGetValue(group.Key.EntityId, out var entity))
            {
                continue;
            }

            var items = group.ToList();
            var ordered = items
                .OrderByDescending(item => item.StartDate, StringComparer.Ordinal)
                .ThenByDescending(item => item.Val)
                .ToList();
            var latest = ordered[0];
            var minimum = items.OrderBy(item => item.Val).ThenByDescending(item => item.Year).First();
            var maximum = items.OrderByDescending(item => item.Val).ThenByDescending(item => item.Year).First();
            var recent = ordered
                .Take(take)
                .Select(item => new Dictionary<string, object?> { ["year"] = item.Year, ["value"] = item.Val })
                .ToList();

            rows.Add(new Dictionary<string, object?>
            {
                ["entity_code"] = entity.Code,
                ["entity_name"] = entity.Name,
                ["metric_code"] = metric.Code,
                ["metric_name"] = metric.Name,
                ["frequency"] = metric.Frequency,
                ["base_unit"] = metric.BaseUnit,
                ["obs_count"] = items.Count,
                ["min_year"] = items.Min(item => item.Year),
                ["max_year"] = items.Max(item => item.Year),
                ["latest_val"] = latest.Val,
                ["latest_year"] = latest.Year,
                ["min_val"] = minimum.Val,
                ["min_val_year"] = minimum.Year,
                ["max_val"] = maximum.Val,
                ["max_val_year"] = maximum.Year,
                ["recent_points"] = recent,
            });
        }

        return rows
            .OrderBy(row => (string)row["entity_code"]!, StringComparer.Ordinal)
            .ThenBy(row => (string)row["metric_code"]!, StringComparer.Ordinal)
            .ToList();
    }

    private static Dictionary<int, NameLookup> LoadLookup(SqliteConnection connection, string sql)
    {
        var map = new Dictionary<int, NameLookup>();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            map[reader.GetInt32(0)] = new NameLookup(
                reader.GetString(1),
                reader.GetString(2),
                reader.FieldCount > 3 ? reader.GetString(3) : "Annual",
                reader.FieldCount > 4 ? reader.GetString(4) : string.Empty);
        }

        return map;
    }

    private static Dictionary<string, int> Stats(int embedded, int updated, int orphansRemoved, int errors)
        => new()
        {
            ["embedded"] = embedded,
            ["updated"] = updated,
            ["orphans_removed"] = orphansRemoved,
            ["errors"] = errors,
        };

    private static object? GetValue(IReadOnlyDictionary<string, object?> row, string key)
        => row.TryGetValue(key, out var value) ? value : null;

    private static string GetString(IReadOnlyDictionary<string, object?> row, string key)
        => row.TryGetValue(key, out var value) ? value as string ?? string.Empty : string.Empty;

    private sealed record BaseObservation(int MetricId, int EntityId, string StartDate, int Year, double Val);

    private sealed record NameLookup(string Code, string Name, string Frequency, string BaseUnit);
}
