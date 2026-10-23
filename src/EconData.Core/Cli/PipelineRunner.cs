using System.Diagnostics;
using EconData.Core.Aggregation;
using EconData.Core.Configuration;
using EconData.Core.Data;
using EconData.Core.Entities;
using EconData.Core.Extraction;
using EconData.Core.Lake;
using EconData.Core.Transform;
using Serilog;

namespace EconData.Core.Cli;

/// <summary>Result counters for one pipeline run.</summary>
public sealed record PipelineSummary(
    int TotalObservations,
    int ObservationsUpserted,
    double ExecutionSeconds,
    IReadOnlyList<MetricOutcome> Outcomes)
{
    public IReadOnlyList<string> FailedMetrics => Outcomes
        .Where(o => o.Outcome == PipelineOutcome.Failed)
        .Select(o => o.MetricCode)
        .ToList();
    public bool Succeeded => FailedMetrics.Count == 0
        && (Outcomes.Count == 0 || Outcomes.Any(o => o.Outcome == PipelineOutcome.Succeeded));
}

/// <summary>Runs the normalized extraction → transform → load pipeline (port of the Python orchestrator).</summary>
public sealed class PipelineRunner
{
    private readonly MetricsCatalog _catalog;
    private readonly EntitySeed _seed;
    private readonly SqliteConnectionFactory _factory;
    private readonly string _schemaPath;
    private readonly ISeriesSource _source;
    private readonly ILogger _logger;
    private readonly IDerivedAggregator? _aggregator;
    private readonly LakePaths? _lake;
    private readonly GoldTier? _gold;

    public PipelineRunner(
        MetricsCatalog catalog,
        EntitySeed seed,
        SqliteConnectionFactory factory,
        string schemaPath,
        ISeriesSource source,
        ILogger logger,
        IDerivedAggregator? aggregator = null,
        LakePaths? lake = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(seed);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaPath);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(logger);
        _catalog = catalog;
        _seed = seed;
        _factory = factory;
        _schemaPath = schemaPath;
        _source = source;
        _logger = logger;
        _aggregator = aggregator;
        _lake = lake;
        _gold = lake is null ? null : new GoldTier(lake.GoldPath, DuckDbSchemaPath(schemaPath), logger);
    }

    /// <summary>
    /// Resolves the DuckDB DDL from the SQLite schema path's sibling directory, so existing
    /// callers that only know about <c>sql/sqlite/schema.sql</c> still find <c>sql/duckdb</c>.
    /// </summary>
    private static string DuckDbSchemaPath(string sqliteSchemaPath)
    {
        var sqlDir = Path.GetDirectoryName(Path.GetDirectoryName(sqliteSchemaPath));
        return Path.Combine(sqlDir ?? "sql", "duckdb", "schema.sql");
    }

    public async Task<PipelineSummary> RunAsync(
        IReadOnlyList<string> metricsToRun,
        bool loadToDb,
        IProgress<PipelineProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => await RunAsync(metricsToRun, loadToDb, null, progress, cancellationToken).ConfigureAwait(false);

    public async Task<PipelineSummary> RunAsync(
        IReadOnlyList<string> metricsToRun,
        bool loadToDb,
        PipelineTiers? tiers,
        IProgress<PipelineProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metricsToRun);
        var started = Stopwatch.StartNew();

        // Without an explicit tier plan the run keeps the pre-lake contract: write SQLite
        // when loadToDb is set and never touch the lake files.
        var plan = tiers ?? PipelineTiers.None;
        var lake = tiers is null || _gold is null ? null : _lake;
        var writeGold = plan.Gold && _gold is not null;

        _logger.Information("=========================================================");
        _logger.Information(" Starting DBnomics Global Economic Data Extraction Infra ");
        _logger.Information("=========================================================");
        _logger.Information("Total metrics to extract: {Count} -> {Metrics}", metricsToRun.Count, string.Join(", ", metricsToRun));
        _logger.Information("Tiers: {Tiers}", plan.Label);

        progress?.Report(new PipelineProgress(
            PipelineProgressKind.RunStarted,
            null,
            0,
            metricsToRun.Count,
            0,
            0,
            started.Elapsed.TotalSeconds));

        // ---- Setup ----
        var resolver = new EntityResolver(_seed, loadToDb || writeGold ? _factory : null, _logger);
        DatabaseLoader? loader = null;
        IReadOnlyDictionary<string, int> metricIds = new Dictionary<string, int>();
        IReadOnlyDictionary<string, int> sourceIds = new Dictionary<string, int>();

        if (loadToDb)
        {
            loader = new DatabaseLoader(_factory, _schemaPath, _logger);
            _logger.Information("Verifying/Initializing SQLite 'dbe_' database schema...");
            if (!loader.VerifyOrInitSchema())
            {
                _logger.Warning("Database schema verification returned warnings; proceeding with extraction.");
            }

            loader.SeedEntities(_seed.Entities, _seed.Aliases);
            loader.SeedMemberships(_seed.Memberships);
        }

        if (writeGold)
        {
            _logger.Information("Verifying/Initializing DuckDB gold schema...");
            _gold!.EnsureSchema();
            _gold.SeedEntities(_seed.Entities, _seed.Aliases);
            _gold.SeedMemberships(_seed.Memberships);
        }

        var metricsRecords = DataTransformer.CreateMetricsRecords(_catalog);
        if (loader is not null)
        {
            loader.UpsertMetrics(metricsRecords);
            var (idsBeforeSources, _) = loader.GetMetricAndSourceIds();
            loader.UpsertSources(metricsRecords, idsBeforeSources);
            (metricIds, sourceIds) = loader.GetMetricAndSourceIds();
        }

        IReadOnlyDictionary<string, int> goldMetricIds = metricIds;
        IReadOnlyDictionary<string, int> goldSourceIds = sourceIds;
        if (writeGold)
        {
            _gold!.UpsertMetrics(metricsRecords);
            var (goldIdsBeforeSources, _) = _gold.GetMetricAndSourceIds();
            _gold.UpsertSources(metricsRecords, goldIdsBeforeSources);
            (goldMetricIds, goldSourceIds) = _gold.GetMetricAndSourceIds();
        }

        var bronze = lake is null ? null : new BronzeTier(lake, _logger);
        var silver = lake is null ? null : new SilverTier(lake, _logger);
        var runId = LakePaths.NewRunId();

        var totalObservations = 0;
        var observationsUpserted = 0;
        var providers = new HashSet<string>(StringComparer.Ordinal);
        var failedMetrics = new List<string>();
        var outcomes = new List<MetricOutcome>();
        var silvered = new List<(string Code, IReadOnlyList<ObservationRecord> Records)>();

        for (var index = 0; index < metricsToRun.Count; index++)
        {
            var metricCode = metricsToRun[index];
            var metric = _catalog.Get(metricCode);
            if (metric is null)
            {
                _logger.Warning("Metric code '{Metric}' not defined in the catalog. Skipping.", metricCode);
                outcomes.Add(new MetricOutcome(metricCode, PipelineOutcome.Skipped, 0, "not in catalog"));
                continue;
            }

            providers.Add(metric.Provider);
            var metricStarted = Stopwatch.StartNew();
            progress?.Report(new PipelineProgress(
                PipelineProgressKind.MetricStarted,
                metricCode,
                index + 1,
                metricsToRun.Count,
                0,
                0,
                metricStarted.Elapsed.TotalSeconds));
            try
            {
                // ---- Extract ----
                _logger.Information(
                    "--> [{Index}/{Count}] Extracting '{Metric}' ({Category}) from {Provider}/{Dataset}...",
                    index + 1,
                    metricsToRun.Count,
                    metricCode,
                    metric.Category,
                    metric.Provider,
                    metric.Dataset);

                var rawSeries = await _source.FetchMetricSeriesAsync(metricCode, _catalog, cancellationToken: cancellationToken).ConfigureAwait(false);

                // ---- Bronze ----
                if (plan.Bronze && bronze is not null)
                {
                    await bronze.AppendAsync(metricCode, rawSeries, _catalog, runId, cancellationToken).ConfigureAwait(false);
                }

                // ---- Silver ----
                var records = DataTransformer.TransformSeriesList(metricCode, rawSeries, _catalog, resolver, _logger);
                totalObservations += records.Count;
                if (plan.Silver && silver is not null)
                {
                    await silver.WriteAsync(metricCode, records, cancellationToken).ConfigureAwait(false);
                    silvered.Add((metricCode, records));
                }

                var upserted = 0;
                if (loadToDb && loader is not null)
                {
                    if (metricIds.TryGetValue(metricCode, out var metricId) && sourceIds.TryGetValue(metricCode, out var sourceId))
                    {
                        upserted = loader.UpsertObservations(metricCode, records, metricId, sourceId);
                        observationsUpserted += upserted;
                    }
                    else
                    {
                        _logger.Warning("No metric/source id for '{Metric}'; skipping DB load.", metricCode);
                    }
                }

                outcomes.Add(new MetricOutcome(metricCode, PipelineOutcome.Succeeded, records.Count, null));

                _logger.Information(
                    "--> [{Index}/{Count}] '{Metric}' done: {Records} observations{Upserted} in {Seconds:F2}s.",
                    index + 1,
                    metricsToRun.Count,
                    metricCode,
                    records.Count,
                    loader is not null ? $", {upserted} upserted" : string.Empty,
                    metricStarted.Elapsed.TotalSeconds);

                progress?.Report(new PipelineProgress(
                    PipelineProgressKind.MetricCompleted,
                    metricCode,
                    index + 1,
                    metricsToRun.Count,
                    records.Count,
                    upserted,
                    metricStarted.Elapsed.TotalSeconds));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Error(ex, "Error extracting metric '{Metric}'.", metricCode);
                failedMetrics.Add(metricCode);
                outcomes.Add(new MetricOutcome(metricCode, PipelineOutcome.Failed, 0, ex.Message));
                progress?.Report(new PipelineProgress(
                    PipelineProgressKind.MetricCompleted,
                    metricCode,
                    index + 1,
                    metricsToRun.Count,
                    0,
                    0,
                    metricStarted.Elapsed.TotalSeconds));
            }
        }

        // ---- Gold ----
        if (writeGold)
        {
            var goldUpserted = 0;
            foreach (var (code, records) in silvered)
            {
                if (goldMetricIds.TryGetValue(code, out var metricId) && goldSourceIds.TryGetValue(code, out var sourceId))
                {
                    goldUpserted += _gold!.UpsertObservations(code, records, metricId, sourceId);
                }
            }

            observationsUpserted += goldUpserted;
            LoadAggregates(goldMetricIds, useGold: true, failedMetrics, outcomes);
        }
        else if (loader is not null)
        {
            LoadAggregates(metricIds, useGold: false, failedMetrics, outcomes);
        }

        // ---- Audit ----
        var elapsed = started.Elapsed.TotalSeconds;
        var status = metricsToRun.Count > 0 && !outcomes.Any(o => o.Outcome == PipelineOutcome.Succeeded) ? "FAILED"
            : failedMetrics.Count == 0 ? "SUCCESS" : "PARTIAL";

        if (writeGold)
        {
            _gold!.RecordRun(
                "econdata_manual",
                "run_pipeline",
                outcomes.Count(o => o.Outcome == PipelineOutcome.Succeeded),
                observationsUpserted,
                Math.Max(0, totalObservations - observationsUpserted),
                providers.ToList(),
                goldSourceIds.Values.ToList(),
                status,
                elapsed,
                plan.Label);
        }

        if (loader is not null)
        {
            loader.LogPipelineRun(
                dagId: "econdata_manual",
                taskId: "run_pipeline",
                metricsExtracted: outcomes.Count(o => o.Outcome == PipelineOutcome.Succeeded),
                observationsUpserted: observationsUpserted,
                observationsSkipped: totalObservations - observationsUpserted,
                providers: providers.ToList(),
                sourceIds: sourceIds.Values.ToList(),
                status: status,
                executionSeconds: elapsed,
                tiersWritten: tiers is null ? null : plan.Label);
        }

        progress?.Report(new PipelineProgress(
            PipelineProgressKind.RunCompleted,
            null,
            metricsToRun.Count,
            metricsToRun.Count,
            totalObservations,
            observationsUpserted,
            elapsed));

        return new PipelineSummary(totalObservations, observationsUpserted, elapsed, outcomes);
    }

    /// <summary>Runs derived aggregation for opted-in metrics; failures surface as outcomes.</summary>
    private void LoadAggregates(
        IReadOnlyDictionary<string, int> metricIds,
        bool useGold,
        List<string> failedMetrics,
        List<MetricOutcome> outcomes)
    {
        var optIn = outcomes
            .Where(o => o.Outcome == PipelineOutcome.Succeeded)
            .Select(o => o.MetricCode)
            .Where(code =>
            {
                var m = _catalog.Get(code);
                return m is not null && (string.Equals(m.Aggregation, "sum", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(m.Aggregation, "mean", StringComparison.OrdinalIgnoreCase));
            })
            .ToList();
        if (optIn.Count == 0)
        {
            return;
        }

        try
        {
            if (useGold && _gold is not null && metricIds.Count > 0)
            {
                if (_aggregator is not null)
                {
                    var res = _aggregator.Aggregate(optIn, DataTransformer.CreateMetricsRecords(_catalog), metricIds);
                    _logger.Information("Derived aggregation wrote {Rows} rows for {Metrics}.", res.RowsWritten, string.Join(", ", res.Metrics));
                    return;
                }

                foreach (var code in optIn)
                {
                    var metric = _catalog.Get(code)!;
                    var rows = _gold.Aggregate(code, metric.Aggregation, metric.EffectiveBaseUnit, metricIds);
                    _logger.Information("Derived aggregation wrote {Rows} rows for '{Metric}'.", rows, code);
                }

                return;
            }

            if (_factory is not null)
            {
                var loader = new DatabaseLoader(_factory, _schemaPath, _logger);
                var agg = _aggregator ?? new Aggregation.DerivedAggregator(_factory, loader, _logger);
                var res = agg.Aggregate(optIn, DataTransformer.CreateMetricsRecords(_catalog), metricIds);
                _logger.Information("Derived aggregation wrote {Rows} rows for {Metrics}.", res.RowsWritten, string.Join(", ", res.Metrics));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Error(ex, "Derived aggregation failed.");
            foreach (var code in optIn)
            {
                failedMetrics.Add($"aggregate:{code}");
                outcomes.Add(new MetricOutcome($"aggregate:{code}", PipelineOutcome.Failed, 0, "aggregation failed"));
            }
        }
    }
}

