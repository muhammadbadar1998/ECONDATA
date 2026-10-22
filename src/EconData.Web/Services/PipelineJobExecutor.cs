using EconData.Core.Ai;
using EconData.Core.Catalog;
using EconData.Core.Cli;
using EconData.Core.Configuration;
using EconData.Core.Data;
using EconData.Core.Entities;
using EconData.Core.Extraction;
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

    public PipelineJobExecutor(
        SqliteConnectionFactory factory,
        SchemaInitializer schemaInitializer,
        MetricsCatalog catalog,
        EntitySeed seed,
        ISeriesSource source,
        IEmbedder embedder,
        VectorStore vectorStore,
        string schemaPath,
        Serilog.ILogger logger)
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
            _logger);
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
        var summary = await runner.RunAsync(metrics, request.LoadToDb, adapter, cancellationToken).ConfigureAwait(false);
        if (summary.FailedMetrics.Count > 0)
        {
            throw new InvalidOperationException($"Extraction failed for: {string.Join(", ", summary.FailedMetrics)}");
        }
        return new JobResult($"Extraction completed for {metrics.Count} metric(s)");
    }

    private async Task<JobResult> VectorizeAsync(JobRequest request, IProgress<JobProgressUpdate> progress, CancellationToken cancellationToken)
    {
        var vectorizer = new Vectorizer(_catalog, _seed, _factory, _vectorStore, _embedder, forceReembed: request.Reembed, _logger);
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
}
