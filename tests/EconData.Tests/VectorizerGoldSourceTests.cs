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

namespace EconData.Tests;

/// <summary>
/// AC18: once a lake exists, the vector catalog must build its documents from the DuckDB
/// gold tier, not from stale or empty SQLite dbe_* tables.
/// </summary>
public class VectorizerGoldSourceTests : IDisposable
{
    private readonly string _dir;
    private readonly SqliteConnectionFactory _sqlite;
    private readonly LakePaths _lake;
    private readonly ILogger _logger = new LoggerConfiguration().CreateLogger();

    public VectorizerGoldSourceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "econdata-vecgold-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _sqlite = new SqliteConnectionFactory(Path.Combine(_dir, "vectors.db"));
        _lake = new LakePaths(Path.Combine(_dir, "lake"));
        _lake.EnsureDirectories();
    }

    public void Dispose()
    {
        (_logger as IDisposable)?.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task CoverageDocumentsComeFromGoldWithoutSqliteData()
    {
        var catalog = MetricsCatalog.Load(TestPaths.ConfigFile("metrics.json"));
        var seed = EntitySeed.Load(TestPaths.ConfigFile("entities.json"));

        await new PipelineRunner(
            catalog,
            seed,
            _sqlite,
            Path.Combine(TestPaths.RepoRoot, "sql", "sqlite", "schema.sql"),
            new FakeSource(),
            _logger,
            lake: _lake)
            .RunAsync(new[] { "gdp_usd" }, loadToDb: false, PipelineTiers.All);

        var gold = new GoldTier(_lake.GoldPath, Path.Combine(TestPaths.RepoRoot, "sql", "duckdb", "schema.sql"), _logger);
        Assert.True(gold.Exists);
        Assert.True(new SilverTier(_lake, _logger).HasPartition("gdp_usd"));

        // SQLite holds vectors and nothing else: no dbe_observations rows were written.
        new SchemaInitializer(_sqlite, _logger).Initialize(Path.Combine(TestPaths.RepoRoot, "sql", "sqlite", "schema.sql"));
        Assert.False(HasObservations(_sqlite));

        var store = new VectorStore(_sqlite, Path.Combine(TestPaths.RepoRoot, "native", "sqlite-vec", "linux-x64", "vec0.so"), _logger);
        var vectorizer = new Vectorizer(
            catalog,
            seed,
            _sqlite,
            store,
            new StubEmbedder(),
            forceReembed: false,
            logger: _logger,
            gold: gold);

        var docs = vectorizer.BuildAllDocuments(recentPoints: 3);

        var coverage = docs.Where(d => d.DocType == "entity_metric").ToList();
        var doc = Assert.Single(coverage);
        Assert.Equal("USA|gdp_usd", doc.DocKey);
        Assert.Contains("United States", doc.Content, StringComparison.Ordinal);
        Assert.Contains("2024", doc.Content, StringComparison.Ordinal);
    }


    [Fact]
    public async Task CorruptGold_DegradesInsteadOfThrowing()
    {
        // Regression: the coverage read used to catch only SqliteException, so a
        // DuckDBException (corrupt or half-written gold file) escaped and aborted
        // the whole vectorization run instead of skipping entity_metric documents.
        var catalog = MetricsCatalog.Load(TestPaths.ConfigFile("metrics.json"));
        var seed = EntitySeed.Load(TestPaths.ConfigFile("entities.json"));
        _lake.EnsureDirectories();
        await File.WriteAllTextAsync(_lake.GoldPath, "not a duckdb database");

        var gold = new GoldTier(_lake.GoldPath, Path.Combine(TestPaths.RepoRoot, "sql", "duckdb", "schema.sql"), _logger);
        Assert.True(gold.Exists);

        var store = new VectorStore(_sqlite, Path.Combine(TestPaths.RepoRoot, "native", "sqlite-vec", "linux-x64", "vec0.so"), _logger);
        var vectorizer = new Vectorizer(
            catalog,
            seed,
            _sqlite,
            store,
            new StubEmbedder(),
            forceReembed: false,
            logger: _logger,
            gold: gold);

        var docs = vectorizer.BuildAllDocuments(recentPoints: 3);

        Assert.DoesNotContain(docs, d => d.DocType == "entity_metric");
        Assert.Contains(docs, d => d.DocType == "metric");
        Assert.Contains(docs, d => d.DocType == "entity");
    }

    private static bool HasObservations(SqliteConnectionFactory factory)
    {
        using var connection = factory.OpenReadOnly();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM dbe_observations";
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 0;
    }

    private sealed class FakeSource : ISeriesSource
    {
        public Task<IReadOnlyList<RawSeries>> FetchMetricSeriesAsync(
            string metricCode,
            MetricsCatalog catalog,
            int limit = 1000,
            CancellationToken cancellationToken = default)
        {
            var series = new RawSeries(
                new Dictionary<string, string> { ["country"] = "USA" },
                new[] { "2022", "2023", "2024" },
                new[] { "100", "200", "300" },
                "United States");
            return Task.FromResult<IReadOnlyList<RawSeries>>(new[] { series });
        }
    }

    private sealed class StubEmbedder : IEmbedder
    {
        public int BatchSize => 32;

        public Task<bool> VerifyConnectivityAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<float[]>>(texts.Select(_ => new float[VectorStore.EmbeddingDim]).ToList());

        public Task<float[]> EmbedQueryAsync(string text, CancellationToken cancellationToken = default)
            => Task.FromResult(new float[VectorStore.EmbeddingDim]);
    }
}
