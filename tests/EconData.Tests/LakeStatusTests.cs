using EconData.Core.Analytics;
using EconData.Core.Cli;
using EconData.Core.Configuration;
using EconData.Core.Data;
using EconData.Core.Entities;
using EconData.Core.Extraction;
using EconData.Core.Lake;
using Microsoft.Data.Sqlite;
using Serilog;

namespace EconData.Tests;

public class LakeStatusTests : IDisposable
{
    private readonly string _dir;
    private readonly LakePaths _lake;
    private readonly SqliteConnectionFactory _sqlite;
    private readonly GoldTier _gold;
    private readonly LakeStatusService _status;
    private readonly ILogger _logger = new LoggerConfiguration().CreateLogger();

    public LakeStatusTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "econdata-status-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _lake = new LakePaths(Path.Combine(_dir, "lake"));
        _lake.EnsureDirectories();
        _sqlite = new SqliteConnectionFactory(Path.Combine(_dir, "vectors.db"));
        _gold = new GoldTier(_lake.GoldPath, Path.Combine(TestPaths.RepoRoot, "sql", "duckdb", "schema.sql"), _logger);
        _status = new LakeStatusService(_lake, _gold, _logger);
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
    public async Task EmptyLake_ReportsEveryTierAsNotBuilt()
    {
        var status = await _status.GetAsync();

        Assert.False(status.Bronze.Present);
        Assert.False(status.Silver.Present);
        Assert.False(status.Gold.Present);
        Assert.Equal("not built yet", status.Bronze.Detail);
        Assert.Equal("not built yet", status.Silver.Detail);
        Assert.Equal("not built yet", status.Gold.Detail);
        Assert.Empty(status.Tables);
        Assert.Empty(status.Partitions);
        Assert.Equal(_lake.LakeDir, status.Directory);
    }

    [Fact]
    public async Task PopulatedLake_ReportsCountsForEveryTier()
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

        var status = await _status.GetAsync();

        Assert.True(status.Bronze.Present);
        Assert.Equal(1, status.Bronze.Files);
        Assert.Equal("1 metrics", status.Bronze.Detail);

        Assert.True(status.Silver.Present);
        Assert.Equal(3, status.Silver.Rows);
        Assert.Equal(3, status.Partitions["gdp_usd"]);

        Assert.True(status.Gold.Present);
        Assert.Equal(3, status.Gold.Rows);
        Assert.Equal(3, status.Tables["dbe_observations"]);
        Assert.Equal(1, status.Tables["dbe_pipeline_runs"]);
        Assert.NotNull(status.Gold.Newest);
        Assert.NotNull(status.Bronze.Newest);
    }

    [Fact]
    public async Task Status_IsStableAcrossCalls()
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

        var first = await _status.GetAsync();
        var second = await _status.GetAsync();

        Assert.Equal(first.Gold.Rows, second.Gold.Rows);
        Assert.Equal(first.Silver.Rows, second.Silver.Rows);
        Assert.Equal(first.Bronze.Files, second.Bronze.Files);
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
}
