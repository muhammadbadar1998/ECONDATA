using System.Globalization;
using EconData.Core.Analytics;
using EconData.Core.Cli;
using EconData.Core.Configuration;
using EconData.Core.Data;
using EconData.Core.Entities;
using EconData.Core.Extraction;
using Microsoft.Data.Sqlite;
using Serilog;
using Xunit;

namespace EconData.Tests;

public class AnalyticsServiceTests : IDisposable
{
    private static readonly string[] DatabaseFileSuffixes = { "", "-wal", "-shm" };

    private readonly string _dbPath;
    private readonly SqliteConnectionFactory _factory;
    private readonly AnalyticsService _analytics;
    private readonly ILogger _logger = new LoggerConfiguration().CreateLogger();

    public AnalyticsServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "econdata-run-" + Guid.NewGuid().ToString("N") + ".db");
        _factory = new SqliteConnectionFactory(_dbPath);
        _analytics = new AnalyticsService(_factory);

        var catalog = MetricsCatalog.Load(TestPaths.ConfigFile("metrics.json"));
        var seed = EntitySeed.Load(TestPaths.ConfigFile("entities.json"));
        var source = new FakeSource();
        var runner = new PipelineRunner(
            catalog,
            seed,
            _factory,
            Path.Combine(TestPaths.RepoRoot, "sql", "sqlite", "schema.sql"),
            source,
            _logger);

        runner.RunAsync(new[] { "gdp_usd" }, loadToDb: true).GetAwaiter().GetResult();
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
    public void GetMetricCoverage_ReturnsNineMetrics_GdpUsdHasYearRange()
    {
        var coverage = _analytics.GetMetricCoverage();

        Assert.Equal(9, coverage.Count);
        var gdpUsd = coverage.FirstOrDefault(c => c.MetricCode == "gdp_usd");
        Assert.NotNull(gdpUsd);
        Assert.NotNull(gdpUsd!.MinPeriod);
        Assert.NotNull(gdpUsd.MaxPeriod);
        Assert.Equal("2022-01-01", gdpUsd.MinPeriod);
        Assert.Equal("2024-01-01", gdpUsd.MaxPeriod);
        Assert.Equal(1, gdpUsd.EntityCount);
        Assert.Equal(3, gdpUsd.ObservationCount);

        // Other metrics have no observations
        foreach (var row in coverage.Where(c => c.MetricCode != "gdp_usd"))
        {
            Assert.Null(row.MinPeriod);
            Assert.Null(row.MaxPeriod);
            Assert.Equal(0, row.EntityCount);
            Assert.Equal(0, row.ObservationCount);
        }
    }

    [Fact]
    public void GetSeries_UsaGdpUsd_ReturnsThreePoints()
    {
        var series = _analytics.GetSeries("USA", "gdp_usd");

        Assert.Equal(3, series.Count);
        Assert.Equal(100.0, series[0].Value);
        Assert.Equal(200.0, series[1].Value);
        Assert.Equal(300.0, series[2].Value);
        Assert.Equal("2022-01-01", series[0].Period);
        Assert.Equal("2023-01-01", series[1].Period);
        Assert.Equal("2024-01-01", series[2].Period);
    }

    [Fact]
    public void GetSeriesForEntities_ReturnsPointsForEachEntity()
    {
        var series = _analytics.GetSeriesForEntities("gdp_usd", new[] { "USA", "DEU" });

        Assert.Equal(3, series.Count);
        Assert.All(series, point => Assert.Equal("USA", point.EntityCode));
    }

    [Fact]
    public void RankEntities_OrdersByValueExcludingAggregatesAndPlaceholders()
    {
        InsertEntityWithObservation("ZZAGG", "Test Aggregate", "aggregate", isAggregate: true, 9999);
        // Unnamed placeholder rows (name == code) in the seed are World Bank aggregates.
        InsertEntityWithObservation("ZPH", "ZPH", "country", isAggregate: false, 8888);

        var descending = _analytics.RankEntities("gdp_usd", "desc", 10);
        var ascending = _analytics.RankEntities("gdp_usd", "asc", 10);

        Assert.Single(descending);
        Assert.Equal("USA", descending[0].CountryCode);
        Assert.Equal(300.0, descending[0].Value);
        Assert.Equal("USA", ascending[0].CountryCode);
        Assert.DoesNotContain(descending, row => row.CountryCode is "ZZAGG" or "ZPH");
    }

    private void InsertEntityWithObservation(string entityCode, string entityName, string entityType, bool isAggregate, double value)
    {
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO dbe_entities (entity_code, entity_name, entity_type, is_aggregate)
            VALUES (@code, @name, @type, @aggregate);
            INSERT INTO dbe_observations (metric_id, entity_id, source_id, start_date, end_date, val)
            SELECT m.metric_id, e.entity_id, s.source_id, '2024-01-01', '2024-12-31', @value
            FROM dbe_metrics m, dbe_entities e, dbe_sources s
            WHERE m.metric_code = 'gdp_usd' AND e.entity_code = @code AND s.metric_id = m.metric_id;
            """;
        command.Parameters.AddWithValue("@code", entityCode);
        command.Parameters.AddWithValue("@name", entityName);
        command.Parameters.AddWithValue("@type", entityType);
        command.Parameters.AddWithValue("@aggregate", isAggregate ? 1 : 0);
        command.Parameters.AddWithValue("@value", value);
        command.ExecuteNonQuery();
    }

    [Fact]
    public void GetLatestSnapshot_UsaGdpUsd_ReturnsLatestValue300()
    {
        var snapshot = _analytics.GetLatestSnapshot("USA", "gdp_usd", null, 10);

        Assert.Single(snapshot);
        Assert.Equal(300.0, snapshot[0].Value);
        Assert.Equal("USA", snapshot[0].CountryCode);
        Assert.Equal("gdp_usd", snapshot[0].MetricCode);
    }

    [Fact]
    public void GetEntities_ReturnsSeedEntitiesAndFilters()
    {
        var all = _analytics.GetEntities(null, null, 1000);
        Assert.Equal(80, all.Count);

        var usa = _analytics.GetEntities("USA", null, 10);
        Assert.Single(usa);
        Assert.Equal("USA", usa[0].EntityCode);

        var countryType = _analytics.GetEntities(null, "country", 1000);
        Assert.All(countryType, e => Assert.Equal("country", e.EntityType));
    }

    [Fact]
    public void GetRuns_ReturnsOneSuccessRun()
    {
        var runs = _analytics.GetRuns(50);

        Assert.Single(runs);
        Assert.Equal("SUCCESS", runs[0].Status);
        Assert.Equal(1, runs[0].MetricsExtracted);
        Assert.Equal(3, runs[0].ObservationsUpserted);
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
