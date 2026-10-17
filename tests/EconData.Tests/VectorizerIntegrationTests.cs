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

public class VectorizerIntegrationTests : IDisposable
{
    private static readonly string[] DatabaseFileSuffixes = { "", "-wal", "-shm" };

    private readonly string _dbPath;
    private readonly SqliteConnectionFactory _factory;
    private readonly DatabaseLoader _loader;
    private readonly ILogger _logger = new LoggerConfiguration().CreateLogger();

    public VectorizerIntegrationTests()
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
    public async Task VectorizeSearchAndIdempotency()
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

        var observations = new List<ObservationRecord>
        {
            Observation("USA", "United States", 2022, 25000000.0),
            Observation("USA", "United States", 2023, 26000000.0),
            Observation("USA", "United States", 2024, 27000000.0),
        };
        _loader.UpsertObservations("gdp_usd", observations, metricIds["gdp_usd"], sourceIds["gdp_usd"]);

        var vecPath = Path.Combine(TestPaths.RepoRoot, AppConfig.DefaultSqliteVecExtensionPath);
        var store = new VectorStore(_factory, vecPath);

        var vectorizer = new Vectorizer(catalog, seed, _factory, store, new FakeEmbedder(), forceReembed: true);
        var stats = await vectorizer.VectorizeAllAsync();

        Assert.True(stats["embedded"] > 0, "expected documents to be embedded");
        Assert.Equal(0, stats["errors"]);

        var hits = await vectorizer.SearchDocumentsAsync("GDP", 5);
        Assert.NotEmpty(hits);
        Assert.Contains(hits[0].DocType, new[] { "metric", "entity", "entity_metric" });
        Assert.True(double.IsFinite(hits[0].Similarity));

        using (var connection = _factory.Open())
        {
            Assert.True(ScalarLong(connection, "SELECT count(*) FROM dbe_embeddings") > 0);
        }

        // Idempotency: unchanged content embeds nothing new.
        var secondRun = new Vectorizer(catalog, seed, _factory, store, new FakeEmbedder(), forceReembed: false);
        var stats2 = await secondRun.VectorizeAllAsync();
        Assert.Equal(0, stats2["embedded"]);
        Assert.Equal(0, stats2["orphans_removed"]);
    }

    private static ObservationRecord Observation(string code, string name, int year, double value)
        => new(
            code,
            name,
            new DateOnly(year, 1, 1),
            new DateOnly(year, 12, 31),
            value,
            value,
            "Current US$",
            year.ToString(CultureInfo.InvariantCulture),
            DateTimeOffset.UtcNow);

    private static long ScalarLong(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private sealed class FakeEmbedder : IEmbedder
    {
        public int BatchSize => 32;

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
