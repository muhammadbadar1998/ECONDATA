using EconData.Core.Ai;
using EconData.Core.Catalog;
using EconData.Core.Cli;
using EconData.Core.Configuration;
using EconData.Core.Data;
using EconData.Core.Entities;
using EconData.Core.Extraction;
using EconData.Core.Lake;
using Microsoft.Data.Sqlite;
using Serilog;

namespace EconData.Web.Services;

public sealed class PipelineJobExecutor : IJobExecutor
{
    private readonly SqliteConnectionFactory _factory;
    private readonly SchemaInitializer _schemaInitializer;
    private readonly MetricsCatalog _catalog;
    private readonly EntitySeed _seed;
    private readonly ISeriesSource _source;
    private readonly IEmbedder _embedder;
    private readonly VectorStore _vectorStore;
    private readonly string _schemaPath;
    private readonly Serilog.ILogger _logger;
    private readonly LakePaths? _lake;
    private readonly GoldTier? _gold;

    public PipelineJobExecutor(
        SqliteConnectionFactory factory,
        SchemaInitializer schemaInitializer,
        MetricsCatalog catalog,
        EntitySeed seed,
        ISeriesSource source,
        IEmbedder embedder,
        VectorStore vectorStore,
        string schemaPath,
        Serilog.ILogger logger,
        LakePaths? lake = null,
        GoldTier? gold = null)
    {
        _factory = factory;
        _schemaInitializer = schemaInitializer;
        _catalog = catalog;
        _seed = seed;
        _source = source;
        _embedder = embedder;
        _vectorStore = vectorStore;
        _schemaPath = schemaPath;
        _logger = logger;
        _lake = lake;
        _gold = gold;
    }

    public async Task<JobResult> ExecuteAsync(
        JobRequest request,
        IProgress<JobProgressUpdate> progress,
        CancellationToken cancellationToken)
    {
        return request.Kind switch
        {
            JobKind.Check => await CheckAsync(progress, cancellationToken).ConfigureAwait(false),
            JobKind.Init => await InitAsync(progress, cancellationToken).ConfigureAwait(false),
            JobKind.Extract => await ExtractAsyncWithAdapter(request, progress, cancellationToken).ConfigureAwait(false),
            JobKind.Vectorize => await VectorizeAsync(request, progress, cancellationToken).ConfigureAwait(false),
            JobKind.RunAll => await RunAllAsync(request, progress, cancellationToken).ConfigureAwait(false),
            JobKind.Rebuild => await RebuildAsync(progress, cancellationToken).ConfigureAwait(false),
            _ => new JobResult("Unknown job kind")
        };
    }

    private Task<JobResult> CheckAsync(IProgress<JobProgressUpdate> progress, CancellationToken cancellationToken)
    {
        progress.Report(new JobProgressUpdate("check", "Verifying database schema", 0, 1));
        try
        {
            var ok = _schemaInitializer.Verify(out var msg);
            if (!ok)
            {
                progress.Report(new JobProgressUpdate("check", $"Schema verification failed: {msg}", 1, 1));
                throw new InvalidOperationException(msg);
            }

            progress.Report(new JobProgressUpdate("check", "Schema verification passed", 1, 1));
            return Task.FromResult(new JobResult("Schema verification passed"));
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            progress.Report(new JobProgressUpdate("check", $"Schema verification failed: {ex.Message}", 1, 1));
            throw;
        }
    }

    private async Task<JobResult> InitAsync(IProgress<JobProgressUpdate> progress, CancellationToken cancellationToken)
    {
        progress.Report(new JobProgressUpdate("init", "Initializing database", 0, 2));
        _schemaInitializer.Initialize(_schemaPath);
        progress.Report(new JobProgressUpdate("init", "Database schema initialized", 1, 2));
        try
        {
            _vectorStore.EnsureVectorTable();
            progress.Report(new JobProgressUpdate("init", "Vector table ensured", 2, 2));
            return new JobResult("Database initialized");
        }
        catch (Exception ex) when (ex is SqliteException or DllNotFoundException or BadImageFormatException)
        {
            _logger.Warning(ex, "Vector table setup failed; continuing without vector store features.");
            progress.Report(new JobProgressUpdate("init", "Vector table setup skipped (vec extension unavailable)", 2, 2));
            return new JobResult("Database initialized (vector store unavailable)");
        }
    }
    private IReadOnlyList<string> ResolveMetrics(JobRequest request)
    {
        if (request.Metrics is null || request.Metrics.Count == 0)
        {
            return _catalog.Codes;
        }
        return request.Metrics
            .Where(code => _catalog.Get(code) is not null)
            .ToList();
    }

    private async Task<JobResult> ExtractAsyncWithAdapter(JobRequest request, IProgress<JobProgressUpdate> progress, CancellationToken cancellationToken)
    {
        var metrics = ResolveMetrics(request);
        if (metrics.Count == 0)
        {
            return new JobResult("No valid metrics requested");
        }
        var runner = new PipelineRunner(
            _catalog,
            _seed,
            _factory,
            _schemaPath,
            _source,
            _logger,
            lake: _lake);
        var adapter = new Progress<PipelineProgress>(p =>
        {
            progress.Report(new JobProgressUpdate(
                "extract",
                p.Kind == PipelineProgressKind.MetricCompleted || p.Kind == PipelineProgressKind.MetricStarted
                    ? $"Metric {p.MetricCode} ({p.Index}/{p.Total})"
                    : p.Kind.ToString(),
                p.Index,
                p.Total,
                p.MetricCode,
                p.Records,
                p.Upserted,
                p.ElapsedSeconds,
                p.Kind == PipelineProgressKind.MetricStarted ? "running" : "done"));
        });
        var tiers = request.Bronze || request.Silver || request.Gold
            ? new PipelineTiers(request.Bronze, request.Silver, request.Gold)
            : null;
        var summary = tiers is null
            ? await runner.RunAsync(metrics, request.LoadToDb, adapter, cancellationToken).ConfigureAwait(false)
            : await runner.RunAsync(metrics, request.LoadToDb, tiers, adapter, cancellationToken).ConfigureAwait(false);
        if (summary.FailedMetrics.Count > 0)
        {
            throw new InvalidOperationException($"Extraction failed for: {string.Join(", ", summary.FailedMetrics)}");
        }
        return new JobResult($"Extraction completed for {metrics.Count} metric(s)");
    }

    private async Task<JobResult> VectorizeAsync(JobRequest request, IProgress<JobProgressUpdate> progress, CancellationToken cancellationToken)
    {
        var vectorizer = new Vectorizer(_catalog, _seed, _factory, _vectorStore, _embedder, forceReembed: request.Reembed, _logger, _gold);
        var adapter = new Progress<VectorizeProgress>(p =>
        {
            progress.Report(new JobProgressUpdate(
                "vectorize",
                p.Kind.ToString(),
                p.CurrentBatch,
                p.TotalBatches));
        });
        await vectorizer.VectorizeAllAsync(5, adapter, cancellationToken).ConfigureAwait(false);
        return new JobResult("Vectorization completed");
    }

    private async Task<JobResult> RunAllAsync(JobRequest request, IProgress<JobProgressUpdate> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await InitAsync(progress, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var extractRequest = request with { Kind = JobKind.Extract, Metrics = null, LoadToDb = true };
        await ExtractAsyncWithAdapter(extractRequest, progress, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await VectorizeAsync(request, progress, cancellationToken).ConfigureAwait(false);
        return new JobResult("Run all completed");
    }

    private async Task<JobResult> RebuildAsync(IProgress<JobProgressUpdate> progress, CancellationToken cancellationToken)
    {
        progress.Report(new JobProgressUpdate("rebuild", "Rebuilding gold from silver", 0, 1));
        if (_lake is null)
        {
            progress.Report(new JobProgressUpdate("rebuild", "Lake not configured", 1, 1));
            return new JobResult("Lake not configured");
        }

        var silver = new SilverTier(_lake, _logger);
        var partitions = silver.Partitions();
        if (partitions.Count == 0)
        {
            progress.Report(new JobProgressUpdate("rebuild", "No silver partitions to rebuild", 1, 1));
            return new JobResult("No silver partitions to rebuild");
        }

        var rebuildPath = Path.Combine(_lake.GoldDir, "econdata.rebuild.duckdb");
        if (File.Exists(rebuildPath)) File.Delete(rebuildPath);
        var rebuild = new GoldTier(rebuildPath, DuckDbSchemaPath(_schemaPath), _logger);

        var counts = await rebuild.RebuildFromSilverAsync(_seed, _catalog, silver, partitions).ConfigureAwait(false);

        var silverCounts = await silver.RowCountsAsync().ConfigureAwait(false);
        var ok = counts.Keys.All(k => silverCounts.TryGetValue(k, out var s) && s == counts[k])
            && partitions.All(p => counts.ContainsKey(p));
        if (!ok)
        {
            progress.Report(new JobProgressUpdate("rebuild", "Rebuild validation failed; not swapped", 1, 1));
            return new JobResult("Rebuild validation failed; not swapped");
        }

        File.Move(rebuildPath, _lake.GoldPath, overwrite: true);
        progress.Report(new JobProgressUpdate("rebuild", "Gold rebuilt and swapped", 1, 1));
        return new JobResult($"Gold rebuilt from {counts.Count} partitions");
    }

    private static string DuckDbSchemaPath(string sqliteSchemaPath)
    {
        var sqlDir = Path.GetDirectoryName(Path.GetDirectoryName(sqliteSchemaPath));
        return Path.Combine(sqlDir ?? "sql", "duckdb", "schema.sql");
    }
}
