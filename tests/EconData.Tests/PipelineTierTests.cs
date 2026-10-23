using EconData.Core.Aggregation;
using EconData.Core.Cli;
using EconData.Core.Configuration;
using EconData.Core.Data;
using EconData.Core.Entities;
using EconData.Core.Extraction;
using EconData.Core.Lake;
using Microsoft.Data.Sqlite;
using Serilog;

namespace EconData.Tests;

public class PipelineTierTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;
    private readonly SqliteConnectionFactory _factory;
    private readonly LakePaths _lake;
    private readonly MetricsCatalog _catalog;
    private readonly EntitySeed _seed;
    private readonly ILogger _logger = new LoggerConfiguration().CreateLogger();

    public PipelineTierTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "econdata-tiers-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "econdata.db");
        _factory = new SqliteConnectionFactory(_dbPath);
        _lake = new LakePaths(Path.Combine(_dir, "lake"));
        _lake.EnsureDirectories();
        _catalog = MetricsCatalog.Load(TestPaths.ConfigFile("metrics.json"));
        _seed = EntitySeed.Load(TestPaths.ConfigFile("entities.json"));
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

    private static string SchemaPath => Path.Combine(TestPaths.RepoRoot, "sql", "sqlite", "schema.sql");
    private static string GoldSchema => Path.Combine(TestPaths.RepoRoot, "sql", "duckdb", "schema.sql");

    private PipelineRunner Runner(ISeriesSource source, IDerivedAggregator? aggregator = null)
        => new(_catalog, _seed, _factory, SchemaPath, source, _logger, aggregator, _lake);

    [Fact]
    public async Task AllTiers_RunWritesBronzeSilverAndGold()
    {
        var runner = Runner(new FakeSource());

        var summary = await runner.RunAsync(new[] { "gdp_usd" }, loadToDb: false, PipelineTiers.All);

        Assert.True(summary.Succeeded);
        var bronze = new BronzeTier(_lake, _logger);
        var silver = new SilverTier(_lake, _logger);
        Assert.NotEmpty(bronze.BronzeFiles("gdp_usd"));
        Assert.True(silver.HasPartition("gdp_usd"));
        Assert.True(File.Exists(_lake.GoldPath));

        var gold = new GoldTier(_lake.GoldPath, GoldSchema, _logger);
        using var db = gold.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM dbe_observations";
        Assert.Equal(3, Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task AllTiers_TwiceIsIdempotent()
    {
        var first = await Runner(new FakeSource()).RunAsync(new[] { "gdp_usd" }, loadToDb: false, PipelineTiers.All);
        Assert.True(first.Succeeded);

        var gold = new GoldTier(_lake.GoldPath, GoldSchema, _logger);
        long GoldRows()
        {
            using var db = gold.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM dbe_observations";
            return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }

        var before = GoldRows();
        var silverBefore = await new SilverTier(_lake, _logger).RowCountsAsync();

        var second = await Runner(new FakeSource()).RunAsync(new[] { "gdp_usd" }, loadToDb: false, PipelineTiers.All);
        Assert.True(second.Succeeded);

        Assert.Equal(before, GoldRows());
        Assert.Equal(silverBefore["gdp_usd"], (await new SilverTier(_lake, _logger).RowCountsAsync())["gdp_usd"]);
        Assert.Equal(silverBefore.Count, (await new SilverTier(_lake, _logger).RowCountsAsync()).Count);
    }

    [Fact]
    public async Task BronzeOnly_DoesNotWriteSilverOrGold()
    {
        var summary = await Runner(new FakeSource())
            .RunAsync(new[] { "gdp_usd" }, loadToDb: false, new PipelineTiers(true, false, false));

        Assert.True(summary.Succeeded);
        Assert.NotEmpty(new BronzeTier(_lake, _logger).BronzeFiles("gdp_usd"));
        Assert.False(new SilverTier(_lake, _logger).HasPartition("gdp_usd"));
        Assert.False(File.Exists(_lake.GoldPath));
    }

    [Fact]
    public async Task GoldTierOnly_DoesNotTouchBronzeOrSilver()
    {
        var summary = await Runner(new FakeSource())
            .RunAsync(new[] { "gdp_usd" }, loadToDb: false, new PipelineTiers(false, false, true));

        Assert.True(summary.Succeeded);
        Assert.Empty(new BronzeTier(_lake, _logger).BronzeFiles("gdp_usd"));
        Assert.False(new SilverTier(_lake, _logger).HasPartition("gdp_usd"));
        Assert.True(File.Exists(_lake.GoldPath));
    }

    [Fact]
    public async Task AggregationFailure_IsReportedWhenGoldTierRuns()
    {
        var runner = Runner(new FakeSource(), new ThrowingAggregator());

        var summary = await runner.RunAsync(new[] { "gdp_usd" }, loadToDb: false, PipelineTiers.All);

        Assert.Contains("aggregate:gdp_usd", summary.FailedMetrics);
        Assert.False(summary.Succeeded);
    }

    [Fact]
    public async Task LegacyPath_StillWritesSqliteAndNotLake()
    {
        var summary = await Runner(new FakeSource()).RunAsync(new[] { "gdp_usd" }, loadToDb: true);

        Assert.True(summary.Succeeded);
        using var db = _factory.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM dbe_observations";
        Assert.Equal(3, Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
        Assert.False(File.Exists(_lake.GoldPath));
        Assert.Empty(new BronzeTier(_lake, _logger).BronzeFiles("gdp_usd"));
    }

    [Fact]
    public void DefaultTiers_AreNoneSoLegacyRunsNeverTouchTheLake()
    {
        Assert.Equal(new PipelineTiers(false, false, false), PipelineTiers.None);
        Assert.Equal("B/S/G", PipelineTiers.All.Label);
        Assert.Equal("B", new PipelineTiers(true, false, false).Label);
        Assert.Equal("", PipelineTiers.None.Label);
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

    private sealed class ThrowingAggregator : IDerivedAggregator
    {
        public DerivedResult Aggregate(
            IReadOnlyList<string> metricCodes,
            IReadOnlyList<MetricRecord> metrics,
            IReadOnlyDictionary<string, int> metricIds,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("boom");
    }
}
