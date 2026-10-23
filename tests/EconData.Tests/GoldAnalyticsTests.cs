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
/// Proves the analytics endpoints return the same shapes when served from the DuckDB
/// gold tier instead of SQLite (AC8/AC12 contract, gold-backed).
/// </summary>
public class GoldAnalyticsTests : IDisposable
{
    private readonly string _dir;
    private readonly LakePaths _lake;
    private readonly SqliteConnectionFactory _sqlite;
    private readonly GoldTier _gold;
    private readonly AnalyticsService _analytics;
    private readonly ILogger _logger = new LoggerConfiguration().CreateLogger();

    public GoldAnalyticsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "econdata-goldana-" + Guid.NewGuid().ToString("N"));
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
    public void UsesGold_WhenGoldExists()
    {
        Assert.True(_analytics.UsesGold);
    }

    [Fact]
    public void Overview_ReportsCountsFromGold()
    {
        var overview = _analytics.GetOverview();

        Assert.Equal(3, overview.ObservationCount);
        Assert.Equal(26, overview.MetricCount);
        Assert.True(overview.EntityCount >= 3);
        Assert.Equal(0, overview.EmbeddingCount);
        Assert.Equal("SUCCESS", overview.LastRunStatus);
    }

    [Fact]
    public void Series_ReturnsRawPointsFromGold()
    {
        var series = _analytics.GetSeries("USA", "gdp_usd");

        Assert.Equal(3, series.Count);
        Assert.Equal(300.0, series[^1].Value);
        Assert.Equal("2024-01-01", series[^1].Period);
        Assert.All(series, p => Assert.Equal("raw", p.SourceKind));
    }

    [Fact]
    public void LatestSnapshot_ReturnsLatestValueFromGold()
    {
        var rows = _analytics.GetLatestSnapshot("USA", "gdp_usd", null, 100);

        var row = Assert.Single(rows);
        Assert.Equal(300.0, row.Value);
        Assert.Equal("USA", row.CountryCode);
        Assert.Equal("raw", row.SourceKind);
    }

    [Fact]
    public void Coverage_ReportsObservationsFromGold()
    {
        var coverage = _analytics.GetMetricCoverage();

        var gdp = coverage.Single(c => c.MetricCode == "gdp_usd");
        Assert.Equal(3, gdp.ObservationCount);
        Assert.Equal(1, gdp.EntityCount);
        Assert.Equal("2022-01-01", gdp.MinPeriod);
        Assert.Equal("2024-01-01", gdp.MaxPeriod);
    }

    [Fact]
    public void Entities_ListsRegisteredEntitiesFromGold()
    {
        var entities = _analytics.GetEntities(null, null, 500);

        Assert.Contains(entities, e => e.EntityCode == "USA" && e.ObservationCount == 3);
    }

    [Fact]
    public void Runs_ReturnsAuditRowsWithTiers()
    {
        var runs = _analytics.GetRuns(10);

        var run = Assert.Single(runs);
        Assert.Equal("SUCCESS", run.Status);
        Assert.Equal(1, run.MetricsExtracted);
        Assert.Equal(3, run.ObservationsUpserted);
        Assert.Equal("B/S/G", run.TiersWritten);
    }

    [Fact]
    public void RankEntities_ExcludesAggregates()
    {
        var ranked = _analytics.RankEntities("gdp_usd", "desc", 10);

        var top = Assert.Single(ranked);
        Assert.Equal("USA", top.CountryCode);
        Assert.Equal(300.0, top.Value);
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
