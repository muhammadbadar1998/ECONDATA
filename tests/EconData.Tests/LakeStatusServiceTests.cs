using System.Net.Http.Json;
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
/// Service-level portion of AC13: <c>LakeStatusService.GetAsync</c> (what
/// <c>lake status</c> prints) returns well-formed data for a built lake.
/// True CLI-vs-API parity lives in <see cref="EconData.Web.Tests.LakeStatusParityTests"/>.
/// </summary>
public class LakeStatusServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;
    private readonly LakePaths _lake;
    private readonly SqliteConnectionFactory _factory;
    private readonly MetricsCatalog _catalog;
    private readonly EntitySeed _seed;
    private readonly ILogger _logger = new LoggerConfiguration().CreateLogger();

    public LakeStatusServiceTests()
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
    public async Task LakeStatusServiceReturnsCompleteStatusForBuiltLake()
    {
        var runner = Runner(new FakeSource());
        var summary = await runner.RunAsync(new[] { "gdp_usd" }, loadToDb: false, PipelineTiers.All);
        Assert.True(summary.Succeeded);

        var service = new LakeStatusService(_lake, new GoldTier(_lake.GoldPath, GoldSchema, _logger), _logger);
        var status = await service.GetAsync();

        Assert.Equal(_lake.LakeDir, status.Directory);
        Assert.Equal(_lake.GoldPath, status.GoldPath);
        Assert.True(status.Bronze.Present);
        Assert.True(status.Silver.Present);
        Assert.True(status.Gold.Present);
        Assert.True(status.Bronze.Files > 0);
        Assert.Equal(3, status.Silver.Rows);
        Assert.Equal(3, status.Gold.Rows);
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
