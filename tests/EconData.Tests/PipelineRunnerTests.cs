using System.Globalization;
using EconData.Core.Aggregation;
using EconData.Core.Cli;
using EconData.Core.Configuration;
using EconData.Core.Data;
using EconData.Core.Entities;
using EconData.Core.Extraction;
using Microsoft.Data.Sqlite;
using Serilog;

namespace EconData.Tests;

public class PipelineRunnerTests : IDisposable
{
    private static readonly string[] DatabaseFileSuffixes = { "", "-wal", "-shm" };

    private readonly string _dbPath;
    private readonly SqliteConnectionFactory _factory;
    private readonly ILogger _logger = new LoggerConfiguration().CreateLogger();

    public PipelineRunnerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "econdata-run-" + Guid.NewGuid().ToString("N") + ".db");
        _factory = new SqliteConnectionFactory(_dbPath);
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
    public async Task RunsExtractTransformLoadAndAudits()
    {
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

        var summary = await runner.RunAsync(new[] { "gdp_usd" }, loadToDb: true);

        Assert.Equal(3, summary.TotalObservations);
        Assert.Equal(3, summary.ObservationsUpserted);

        using var connection = _factory.Open();
        Assert.Equal(3, ScalarLong(connection, "SELECT count(*) FROM dbe_observations"));
        Assert.Equal(1, ScalarLong(connection, "SELECT count(*) FROM dbe_pipeline_runs"));
        Assert.Equal(26, ScalarLong(connection, "SELECT count(*) FROM dbe_metrics"));

        using var latest = connection.CreateCommand();
        latest.CommandText = "SELECT value FROM v_latest_metrics_snapshot WHERE country_code = 'USA' AND metric_code = 'gdp_usd'";
        Assert.Equal(300.0, Convert.ToDouble(latest.ExecuteScalar(), CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task RunningPipelineTwiceIsIdempotent()
    {
        var catalog = MetricsCatalog.Load(TestPaths.ConfigFile("metrics.json"));
        var seed = EntitySeed.Load(TestPaths.ConfigFile("entities.json"));
        var runner = new PipelineRunner(
            catalog,
            seed,
            _factory,
            Path.Combine(TestPaths.RepoRoot, "sql", "sqlite", "schema.sql"),
            new FakeSource(),
            _logger);

        await runner.RunAsync(new[] { "gdp_usd" }, loadToDb: true);

        long obsBefore, entBefore, metBefore, srcBefore, derivedBefore;
        string keysBefore;
        using (var connection = _factory.Open())
        {
            obsBefore = ScalarLong(connection, "SELECT count(*) FROM dbe_observations");
            entBefore = ScalarLong(connection, "SELECT count(*) FROM dbe_entities");
            metBefore = ScalarLong(connection, "SELECT count(*) FROM dbe_metrics");
            srcBefore = ScalarLong(connection, "SELECT count(*) FROM dbe_sources");
            derivedBefore = ScalarLong(connection, "SELECT count(*) FROM dbe_derived_observations");
            keysBefore = ObservationKeys(connection);
        }

        await runner.RunAsync(new[] { "gdp_usd" }, loadToDb: true);

        using (var connection = _factory.Open())
        {
            Assert.Equal(obsBefore, ScalarLong(connection, "SELECT count(*) FROM dbe_observations"));
            Assert.Equal(entBefore, ScalarLong(connection, "SELECT count(*) FROM dbe_entities"));
            Assert.Equal(metBefore, ScalarLong(connection, "SELECT count(*) FROM dbe_metrics"));
            Assert.Equal(srcBefore, ScalarLong(connection, "SELECT count(*) FROM dbe_sources"));
            Assert.Equal(derivedBefore, ScalarLong(connection, "SELECT count(*) FROM dbe_derived_observations"));
            Assert.Equal(keysBefore, ObservationKeys(connection));

            // The audit log grows once per run; that is expected, not duplication.
            Assert.Equal(2, ScalarLong(connection, "SELECT count(*) FROM dbe_pipeline_runs"));
            Assert.Equal(2, ScalarLong(connection, "SELECT count(*) FROM dbe_pipeline_runs WHERE status = 'SUCCESS'"));
        }
    }

    private static string ObservationKeys(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT metric_id || '|' || entity_id || '|' || start_date || '|' || end_date " +
            "FROM dbe_observations ORDER BY 1";
        using var reader = command.ExecuteReader();
        var keys = new List<string>();
        while (reader.Read())
        {
            keys.Add(reader.GetString(0));
        }

        return string.Join("\n", keys);
    }

    private static long ScalarLong(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
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

    [Fact]
    public async Task AggregationFailure_IsReportedInFailedMetrics()
    {
        var catalog = MetricsCatalog.Load(TestPaths.ConfigFile("metrics.json"));
        var seed = EntitySeed.Load(TestPaths.ConfigFile("entities.json"));
        var source = new FakeSource();
        var runner = new PipelineRunner(
            catalog,
            seed,
            _factory,
            Path.Combine(TestPaths.RepoRoot, "sql", "sqlite", "schema.sql"),
            source,
            _logger,
            new ThrowingAggregator());

        var summary = await runner.RunAsync(new[] { "gdp_usd" }, loadToDb: true);

        Assert.Contains("aggregate:gdp_usd", summary.FailedMetrics);
        Assert.False(summary.Succeeded);

        using var connection = _factory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT status FROM dbe_pipeline_runs ORDER BY run_id DESC LIMIT 1;";
        var status = Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        Assert.True(status == "PARTIAL" || status == "FAILED", $"unexpected status {status}");
    }

    private sealed class ThrowingAggregator : IDerivedAggregator
    {
        public DerivedResult Aggregate(IReadOnlyList<string> metricCodes, IReadOnlyList<MetricRecord> metrics, IReadOnlyDictionary<string, int> metricIds, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("boom");
    }


    [Fact]
    public async Task MixedOutcomes_ProducePartialStatus()
    {
        var catalog = MetricsCatalog.Load(TestPaths.ConfigFile("metrics.json"));
        var seed = EntitySeed.Load(TestPaths.ConfigFile("entities.json"));
        var source = new FailingSource("gdp_growth");
        var runner = new PipelineRunner(
            catalog,
            seed,
            _factory,
            Path.Combine(TestPaths.RepoRoot, "sql", "sqlite", "schema.sql"),
            source,
            _logger);

        var summary = await runner.RunAsync(new[] { "gdp_usd", "gdp_growth" }, loadToDb: true);

        Assert.False(summary.Succeeded);
        Assert.Contains("gdp_growth", summary.FailedMetrics);
        Assert.Equal(2, summary.Outcomes.Count);
        Assert.Contains(summary.Outcomes, o => o.MetricCode == "gdp_usd" && o.Outcome == PipelineOutcome.Succeeded);
        Assert.Contains(summary.Outcomes, o => o.MetricCode == "gdp_growth" && o.Outcome == PipelineOutcome.Failed);
        Assert.Equal(1, summary.Outcomes.Count(o => o.Outcome == PipelineOutcome.Succeeded));

        using var connection = _factory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT status, metrics_extracted FROM dbe_pipeline_runs ORDER BY run_id DESC LIMIT 1;";
        using var r = cmd.ExecuteReader(); r.Read();
        Assert.Equal("PARTIAL", r.GetString(0));
        Assert.Equal(1, r.GetInt32(1));
    }

    private sealed class FailingSource : ISeriesSource
    {
        private readonly string _failCode;
        public FailingSource(string failCode) { _failCode = failCode; }
        public Task<IReadOnlyList<RawSeries>> FetchMetricSeriesAsync(
            string metricCode,
            MetricsCatalog catalog,
            int limit = 1000,
            CancellationToken cancellationToken = default)
        {
            if (metricCode == _failCode)
            {
                throw new InvalidOperationException("boom");
            }
            var series = new RawSeries(
                new Dictionary<string, string> { ["country"] = "USA" },
                new[] { "2022", "2023", "2024" },
                new[] { "100", "200", "300" },
                "United States");
            return Task.FromResult<IReadOnlyList<RawSeries>>(new[] { series });
        }
    }


    [Fact]
    public async Task AllSkipped_ReportsFailed()
    {
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

        var summary = await runner.RunAsync(new[] { "bogus_a", "bogus_b" }, loadToDb: true);

        Assert.False(summary.Succeeded);
        Assert.Equal(0, summary.Outcomes.Count(o => o.Outcome == PipelineOutcome.Succeeded));
        Assert.Equal(2, summary.Outcomes.Count(o => o.Outcome == PipelineOutcome.Skipped));
        Assert.Equal(0, summary.TotalObservations);

        using var connection = _factory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT status, metrics_extracted FROM dbe_pipeline_runs ORDER BY run_id DESC LIMIT 1;";
        using var r = cmd.ExecuteReader(); r.Read();
        Assert.Equal("FAILED", r.GetString(0));
        Assert.Equal(0, r.GetInt32(1));
    }

    [Fact]
    public async Task EmptyMetricList_IsNoOp()
    {
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

        var summary = await runner.RunAsync(Array.Empty<string>(), loadToDb: true);

        Assert.True(summary.Succeeded);
        Assert.Empty(summary.Outcomes);
        Assert.Equal(0, summary.TotalObservations);
    }

}
