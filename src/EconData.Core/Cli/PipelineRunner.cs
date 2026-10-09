using System.Diagnostics;
using EconData.Core.Configuration;
using EconData.Core.Data;
using EconData.Core.Entities;
using EconData.Core.Extraction;
using EconData.Core.Transform;
using Serilog;

namespace EconData.Core.Cli;

/// <summary>Result counters for one pipeline run.</summary>
public sealed record PipelineSummary(int TotalObservations, int ObservationsUpserted, double ExecutionSeconds);

/// <summary>Runs the normalized extraction → transform → load pipeline (port of the Python orchestrator).</summary>
public sealed class PipelineRunner
{
    private readonly MetricsCatalog _catalog;
    private readonly EntitySeed _seed;
    private readonly SqliteConnectionFactory _factory;
    private readonly string _schemaPath;
    private readonly ISeriesSource _source;
    private readonly ILogger _logger;

    public PipelineRunner(
        MetricsCatalog catalog,
        EntitySeed seed,
        SqliteConnectionFactory factory,
        string schemaPath,
        ISeriesSource source,
        ILogger logger)
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
    }

    public async Task<PipelineSummary> RunAsync(IReadOnlyList<string> metricsToRun, bool loadToDb, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metricsToRun);
        var started = Stopwatch.StartNew();

        _logger.Information("=========================================================");
        _logger.Information(" Starting DBnomics Global Economic Data Extraction Infra ");
        _logger.Information("=========================================================");
        _logger.Information("Total metrics to extract: {Count} -> {Metrics}", metricsToRun.Count, string.Join(", ", metricsToRun));
        _logger.Information("Target database: SQLite ({Status})", loadToDb ? "Enabled" : "Disabled");

        var resolver = new EntityResolver(_seed, loadToDb ? _factory : null, _logger);
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

        var metricsRecords = DataTransformer.CreateMetricsRecords(_catalog);
        if (loader is not null)
        {
            loader.UpsertMetrics(metricsRecords);
            var (idsBeforeSources, _) = loader.GetMetricAndSourceIds();
            loader.UpsertSources(metricsRecords, idsBeforeSources);
            (metricIds, sourceIds) = loader.GetMetricAndSourceIds();
        }

        var totalObservations = 0;
        var observationsUpserted = 0;
        var providers = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < metricsToRun.Count; index++)
        {
            var metricCode = metricsToRun[index];
            var metric = _catalog.Get(metricCode);
            if (metric is null)
            {
                _logger.Warning("Metric code '{Metric}' not defined in the catalog. Skipping.", metricCode);
                continue;
            }

            providers.Add(metric.Provider);
            var metricStarted = Stopwatch.StartNew();
            try
            {
                _logger.Information(
                    "--> [{Index}/{Count}] Extracting '{Metric}' ({Category}) from {Provider}/{Dataset}...",
                    index + 1,
                    metricsToRun.Count,
                    metricCode,
                    metric.Category,
                    metric.Provider,
                    metric.Dataset);

                var rawSeries = await _source.FetchMetricSeriesAsync(metricCode, _catalog, cancellationToken: cancellationToken).ConfigureAwait(false);
                var records = DataTransformer.TransformSeriesList(metricCode, rawSeries, _catalog, resolver, _logger);
                totalObservations += records.Count;

                var upserted = 0;
                if (loader is not null)
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

                _logger.Information(
                    "--> [{Index}/{Count}] '{Metric}' done: {Records} observations{Upserted} in {Seconds:F2}s.",
                    index + 1,
                    metricsToRun.Count,
                    metricCode,
                    records.Count,
                    loader is not null ? $", {upserted} upserted" : string.Empty,
                    metricStarted.Elapsed.TotalSeconds);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Error(ex, "Error extracting metric '{Metric}'.", metricCode);
            }
        }

        var elapsed = started.Elapsed.TotalSeconds;
        if (loader is not null)
        {
            loader.LogPipelineRun(
                dagId: "econdata_manual",
                taskId: "run_pipeline",
                metricsExtracted: metricsToRun.Count,
                observationsUpserted: observationsUpserted,
                observationsSkipped: totalObservations - observationsUpserted,
                providers: providers.ToList(),
                sourceIds: sourceIds.Values.ToList(),
                status: "SUCCESS",
                executionSeconds: elapsed);
        }

        _logger.Information("=========================================================");
        _logger.Information(" Pipeline Completed Successfully in {Elapsed:F2}s ", elapsed);
        _logger.Information(" Total Extracted Observations: {Total} ", totalObservations);
        _logger.Information("=========================================================");

        return new PipelineSummary(totalObservations, observationsUpserted, elapsed);
    }
}
