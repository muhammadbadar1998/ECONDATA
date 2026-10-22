using EconData.Core.Ai;
using EconData.Core.Catalog;
using EconData.Core.Configuration;
using EconData.Core.Data;
using EconData.Core.Entities;
using EconData.Core.Extraction;
using EconData.Web.Services;
using Microsoft.Data.Sqlite;
using Serilog;

namespace EconData.Web.Tests;

public class PipelineJobExecutorCheckTests : IDisposable
{
    private static readonly string[] Suffixes = { "", "-wal", "-shm" };
    private readonly string _dbPath;
    private readonly SqliteConnectionFactory _factory;
    private readonly ILogger _logger = new LoggerConfiguration().CreateLogger();
    private readonly string _schemaPath = Path.Combine(TestPaths.RepoRoot, "sql", "sqlite", "schema.sql");

    public PipelineJobExecutorCheckTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "econdata-webcheck-" + Guid.NewGuid().ToString("N") + ".db");
        _factory = new SqliteConnectionFactory(_dbPath);
    }

    public void Dispose()
    {
        (_logger as IDisposable)?.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var suffix in Suffixes)
        {
            var path = _dbPath + suffix;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        GC.SuppressFinalize(this);
    }

    private PipelineJobExecutor Create()
    {
        var vecPath = Path.Combine(TestPaths.RepoRoot, "native", "sqlite-vec", "linux-x64", "vec0.so");
        return new PipelineJobExecutor(
            _factory,
            new SchemaInitializer(_factory, _logger),
            MetricsCatalog.Load(TestPaths.ConfigFile("metrics.json")),
            EntitySeed.Load(TestPaths.ConfigFile("entities.json")),
            new FakeSource(),
            new NoopEmbedder(),
            new VectorStore(_factory, vecPath, _logger),
            _schemaPath,
            _logger);
    }

    private static readonly JobRequest CheckRequest = new(JobKind.Check, null, false, false);

    [Fact]
    public async Task CheckSucceedsOnInitializedDatabase()
    {
        Assert.True(new SchemaInitializer(_factory, _logger).Initialize(_schemaPath));

        var result = await Create().ExecuteAsync(CheckRequest, new Progress<JobProgressUpdate>(), CancellationToken.None);

        Assert.Contains("passed", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckFailsWhenDatabaseMissing()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Create().ExecuteAsync(CheckRequest, new Progress<JobProgressUpdate>(), CancellationToken.None));

        Assert.Contains("not found", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(_dbPath), "check must not create the database");
    }

    [Fact]
    public async Task CheckFailsWhenDatabaseCorrupt()
    {
        File.WriteAllText(_dbPath, "garbage-not-sqlite");

        await Assert.ThrowsAnyAsync<Exception>(
            () => Create().ExecuteAsync(CheckRequest, new Progress<JobProgressUpdate>(), CancellationToken.None));

        Assert.Equal("garbage-not-sqlite", File.ReadAllText(_dbPath));
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
                new[] { "2022" },
                new[] { "100" },
                "United States");
            return Task.FromResult<IReadOnlyList<RawSeries>>(new[] { series });
        }
    }

    private sealed class NoopEmbedder : IEmbedder
    {
        public int BatchSize => 32;

        public Task<bool> VerifyConnectivityAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<float[]>>(texts.Select(_ => new float[768]).ToList());

        public Task<float[]> EmbedQueryAsync(string text, CancellationToken cancellationToken = default)
            => Task.FromResult(new float[768]);
    }
}
