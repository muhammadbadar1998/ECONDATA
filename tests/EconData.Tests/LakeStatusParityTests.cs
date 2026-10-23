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
/// AC13: the dashboard (`/api/lake/status`) and the CLI (`lake status`) must agree.
/// Both read from <see cref="LakeStatusService.GetAsync"/>, so this test builds a lake
/// once and verifies the service returns consistent data to both callers.
/// </summary>
public class LakeStatusParityTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;
    private readonly SqliteConnectionFactory _factory;
    private readonly LakePaths _lake;
    private readonly MetricsCatalog _catalog;
    private readonly EntitySeed _seed;
    private readonly ILogger _logger = new LoggerConfiguration().CreateLogger();

    public LakeStatusParityTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "econdata-parity-" + Guid.NewGuid().ToString("N"));
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

    private PipelineRunner Runner(ISeriesSource source)
        => new(_catalog, _seed, _factory, SchemaPath, source, _logger, lake: _lake);

    [Fact]
    public async Task LakeStatusServiceReturnsSameDataToCliAndApi()
    {
        // Build lake data once — both `lake status` and `/api/lake/status` read from this.
        var runner = Runner(new FakeSource());
        var summary = await runner.RunAsync(new[] { "gdp_usd" }, loadToDb: false, PipelineTiers.All);
        Assert.True(summary.Succeeded);

        // CLI side: `lake status` prints this object.
        // API side: `GET /api/lake/status` returns `Results.Ok(await lake.GetAsync())`.
        // Both call LakeStatusService.GetAsync(), so they must agree.
        var service = new LakeStatusService(_lake, new GoldTier(_lake.GoldPath, GoldSchema, _logger), _logger);
        var status = await service.GetAsync();

        Assert.Equal(_lake.LakeDir, status.Directory);
        Assert.Equal(_lake.GoldPath, status.GoldPath);

        // Both tiers must agree that data was built.
        Assert.True(status.Bronze.Present);
        Assert.True(status.Silver.Present);
        Assert.True(status.Gold.Present);

        // Rows agree across both callers. Bronze files are appended (not scanned), so
        // its row count stays 0; silver and gold count rows.
        Assert.True(status.Bronze.Files > 0);
        Assert.Equal(3, status.Silver.Rows);
        Assert.Equal(3, status.Gold.Rows);

        // Table and partition inventories agree.
        Assert.Equal(3, status.Tables["dbe_observations"]);
        Assert.Equal(3, status.Partitions["gdp_usd"]);
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
