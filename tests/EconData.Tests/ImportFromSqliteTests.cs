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
/// One-way backfill from an existing SQLite database into DuckDB gold.
/// Uses natural keys only; SQLite surrogate ids must never leak into gold.
/// </summary>
public class ImportFromSqliteTests : IDisposable
{
    private readonly string _dir;
    private readonly SqliteConnectionFactory _sqlite;
    private readonly LakePaths _lake;
    private readonly GoldTier _gold;
    private readonly ILogger _logger = new LoggerConfiguration().CreateLogger();

    public ImportFromSqliteTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "econdata-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _sqlite = new SqliteConnectionFactory(Path.Combine(_dir, "econdata.db"));
        _lake = new LakePaths(Path.Combine(_dir, "lake"));
        _lake.EnsureDirectories();
        _gold = new GoldTier(_lake.GoldPath, Path.Combine(TestPaths.RepoRoot, "sql", "duckdb", "schema.sql"), _logger);
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

    private void SeedSqlite()
    {
        var catalog = MetricsCatalog.Load(TestPaths.ConfigFile("metrics.json"));
        var seed = EntitySeed.Load(TestPaths.ConfigFile("entities.json"));
        new PipelineRunner(
            catalog,
            seed,
            _sqlite,
            Path.Combine(TestPaths.RepoRoot, "sql", "sqlite", "schema.sql"),
            new FakeSource(),
            _logger)
            .RunAsync(new[] { "gdp_usd" }, loadToDb: true)
            .GetAwaiter().GetResult();
    }

    private long Scalar(string sql)
    {
        using var connection = _gold.OpenReadOnly();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public void ImportsRegistriesAndObservationsAndIsIdempotent()
    {
        SeedSqlite();
        _gold.EnsureSchema();

        _gold.ImportFromSqlite(_sqlite);

        Assert.Equal(85, Scalar("SELECT COUNT(*) FROM dbe_entities"));
        Assert.Equal(26, Scalar("SELECT COUNT(*) FROM dbe_metrics"));
        Assert.Equal(26, Scalar("SELECT COUNT(*) FROM dbe_sources"));
        Assert.Equal(3, Scalar("SELECT COUNT(*) FROM dbe_observations"));

        // Values and natural-key joins survive the id remapping.
        Assert.Equal(300.0, Scalar(
            "SELECT o.val FROM dbe_observations o " +
            "JOIN dbe_entities e ON e.entity_id = o.entity_id " +
            "JOIN dbe_metrics m ON m.metric_id = o.metric_id " +
            "WHERE e.entity_code = 'USA' AND m.metric_code = 'gdp_usd' AND o.start_date = '2024-01-01'"));

        // Second import changes nothing.
        _gold.ImportFromSqlite(_sqlite);
        Assert.Equal(85, Scalar("SELECT COUNT(*) FROM dbe_entities"));
        Assert.Equal(26, Scalar("SELECT COUNT(*) FROM dbe_metrics"));
        Assert.Equal(26, Scalar("SELECT COUNT(*) FROM dbe_sources"));
        Assert.Equal(3, Scalar("SELECT COUNT(*) FROM dbe_observations"));
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
