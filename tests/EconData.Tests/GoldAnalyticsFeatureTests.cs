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

/// <summary>
/// The analytics features must work on the DuckDB gold tier, not only SQLite.
/// The plan requires both engines; the existing tests only exercise SQLite.
/// </summary>
public class GoldAnalyticsFeatureTests : IDisposable
{
    private readonly string _dir;
    private readonly LakePaths _lake;
    private readonly SqliteConnectionFactory _sqlite;
    private readonly GoldTier _gold;
    private readonly AnalyticsService _analytics;
    private readonly ILogger _logger = new LoggerConfiguration().CreateLogger();

    public GoldAnalyticsFeatureTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "econdata-goldfeat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _lake = new LakePaths(Path.Combine(_dir, "lake"));
        _lake.EnsureDirectories();
        _sqlite = new SqliteConnectionFactory(Path.Combine(_dir, "vectors.db"));
        _gold = new GoldTier(_lake.GoldPath, Path.Combine(TestPaths.RepoRoot, "sql", "duckdb", "schema.sql"), _logger);

        var catalog = MetricsCatalog.Load(TestPaths.ConfigFile("metrics.json"));
        var seed = EntitySeed.Load(TestPaths.ConfigFile("entities.json"));
        new PipelineRunner(
            catalog,
            seed,
            _sqlite,
            Path.Combine(TestPaths.RepoRoot, "sql", "sqlite", "schema.sql"),
            new FakeSource(),
            _logger,
            lake: _lake)
            .RunAsync(new[] { "gdp_usd" }, loadToDb: false, PipelineTiers.All)
            .GetAwaiter().GetResult();

        _analytics = new AnalyticsService(_sqlite, _gold);
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
    public void Growth_WorksOnGold()
    {
        var growth = _analytics.GetGrowth("gdp_usd", "USA", "yoy", 10);

        var point = Assert.Single(growth);
        Assert.Equal("2024-01-01", point.Period);
        Assert.Equal(300.0, point.Value);
        Assert.Equal(50.0, point.GrowthPct);
    }

    [Fact]
    public void Ranking_WorksOnGold()
    {
        var ranking = _analytics.GetRanking("gdp_usd", "2024-01-01", "desc", 10);

        var row = Assert.Single(ranking);
        Assert.Equal("USA", row.EntityCode);
        Assert.Equal(300.0, row.Value);
    }

    [Fact]
    public void Correlation_WorksOnGold()
    {
        var corr = _analytics.GetCorrelation("gdp_usd", "gdp_usd", "2024-01-01");

        var item = Assert.Single(corr);
        Assert.Equal(1, item.N);
        Assert.Equal(1.0, item.Correlation);
    }

    [Fact]
    public void Freshness_WorksOnGold()
    {
        var freshness = _analytics.GetFreshness();

        var gdp = Assert.Single(freshness, f => f.MetricCode == "gdp_usd");
        Assert.Equal("2024-01-01", gdp.LatestPeriod);
        Assert.Equal(3, gdp.RowCount);
        Assert.NotNull(gdp.LagYears);
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
