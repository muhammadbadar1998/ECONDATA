using System.Globalization;
using System.Text;
using EconData.Core.Ai;
using EconData.Core.Catalog;
using EconData.Core.Configuration;
using EconData.Core.Data;
using EconData.Core.Entities;
using EconData.Core.Transform;
using Microsoft.Data.Sqlite;
using Serilog;

namespace EconData.Tests;

public class VectorizeProgressTests : IDisposable
{
    private static readonly string[] DatabaseFileSuffixes = { "", "-wal", "-shm" };

    private readonly string _dbPath;
    private readonly SqliteConnectionFactory _factory;
    private readonly DatabaseLoader _loader;
    private readonly ILogger _logger = new LoggerConfiguration().CreateLogger();

    public VectorizeProgressTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "econdata-vec-" + Guid.NewGuid().ToString("N") + ".db");
        _factory = new SqliteConnectionFactory(_dbPath);
        _loader = new DatabaseLoader(
            _factory,
            Path.Combine(TestPaths.RepoRoot, "sql", "sqlite", "schema.sql"),
            _logger);
    }

    public void Dispose()
    {
        (_logger as IDisposable)?.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var suffix in DatabaseFileSuffixes)
        {
            var path = _dbPath + suffix;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task VectorizeAllAsync_ReportsBatchProgress()
    {
        Assert.True(_loader.VerifyOrInitSchema());

        var seed = EntitySeed.Load(TestPaths.ConfigFile("entities.json"));
        _loader.SeedEntities(seed.Entities, seed.Aliases);
        _loader.SeedMemberships(seed.Memberships);

        var catalog = MetricsCatalog.Load(TestPaths.ConfigFile("metrics.json"));
        var metrics = DataTransformer.CreateMetricsRecords(catalog);
        _loader.UpsertMetrics(metrics);
        var (metricIdsBeforeSources, _) = _loader.GetMetricAndSourceIds();
        _loader.UpsertSources(metrics, metricIdsBeforeSources);
        var (metricIds, sourceIds) = _loader.GetMetricAndSourceIds();
        _loader.UpsertObservations(
            "gdp_usd",
            new[]
            {
                Observation(2022, 25000000.0),
                Observation(2023, 26000000.0),
                Observation(2024, 27000000.0),
            },
            metricIds["gdp_usd"],
            sourceIds["gdp_usd"]);

        var vecPath = Path.Combine(TestPaths.RepoRoot, AppConfig.DefaultSqliteVecExtensionPath);
        var store = new VectorStore(_factory, vecPath);
        var vectorizer = new Vectorizer(catalog, seed, _factory, store, new FakeEmbedder(batchSize: 2), forceReembed: true);
        var reports = new CollectingProgress<VectorizeProgress>();

        var stats = await vectorizer.VectorizeAllAsync(5, reports);

        Assert.Equal(VectorizeProgressKind.BuildStarted, reports.Items[0].Kind);

        var buildCompleted = Assert.Single(reports.Items, item => item.Kind == VectorizeProgressKind.BuildCompleted);
        Assert.True(buildCompleted.TotalDocuments > 0);
        Assert.True(buildCompleted.DocumentsToEmbed > 0);

        var embedStarted = Assert.Single(reports.Items, item => item.Kind == VectorizeProgressKind.EmbedStarted);
        Assert.True(embedStarted.TotalBatches > 1);

        var batches = reports.Items.Where(item => item.Kind == VectorizeProgressKind.BatchCompleted).ToList();
        Assert.True(batches.Count > 1);
        for (var index = 1; index < batches.Count; index++)
        {
            Assert.True(batches[index].CurrentBatch > batches[index - 1].CurrentBatch);
            Assert.True(batches[index].Embedded > batches[index - 1].Embedded);
        }

        var completed = Assert.Single(reports.Items, item => item.Kind == VectorizeProgressKind.Completed);
        Assert.True(completed.Embedded > 0);
        Assert.Equal(stats["embedded"], completed.Embedded);
        Assert.Equal(0, completed.OrphansRemoved);
    }

    private static ObservationRecord Observation(int year, double value)
        => new(
            "USA",
            "United States",
            new DateOnly(year, 1, 1),
            new DateOnly(year, 12, 31),
            value,
            value,
            "Current US$",
            year.ToString(CultureInfo.InvariantCulture),
            DateTimeOffset.UtcNow);

    private sealed class CollectingProgress<T> : IProgress<T>
    {
        public List<T> Items { get; } = new();

        public void Report(T value) => Items.Add(value);
    }

    private sealed class FakeEmbedder : IEmbedder
    {
        public FakeEmbedder(int batchSize) => BatchSize = batchSize;

        public int BatchSize { get; }

        public Task<bool> VerifyConnectivityAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<float[]>>(texts.Select(Vector).ToList());

        public Task<float[]> EmbedQueryAsync(string text, CancellationToken cancellationToken = default)
            => Task.FromResult(Vector(text));

        private static float[] Vector(string text)
        {
            var hash = 17;
            foreach (var value in Encoding.UTF8.GetBytes(text))
            {
                hash = ((hash * 31) + value) & 0x7fffffff;
            }

            var vector = new float[VectorStore.EmbeddingDim];
            for (var i = 0; i < vector.Length; i++)
            {
                vector[i] = (((hash >> (i % 7)) + i) % 1000 + 1) / 1000f;
            }

            return vector;
        }
    }
}
