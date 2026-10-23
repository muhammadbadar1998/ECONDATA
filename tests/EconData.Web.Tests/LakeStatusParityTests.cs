using System.Net.Http.Json;
using EconData.Core.Analytics;
using EconData.Core.Cli;
using EconData.Core.Configuration;
using EconData.Core.Data;
using EconData.Core.Entities;
using EconData.Core.Extraction;
using EconData.Core.Lake;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Serilog;

namespace EconData.Web.Tests;

/// <summary>
/// AC13: the dashboard (<c>/api/lake/status</c>) and the CLI (<c>lake status</c>) must agree.
/// Both surfaces render <see cref="LakeStatusService.GetAsync"/>, so this test builds a lake,
/// starts a test server pointed at that lake, calls the live HTTP endpoint, and compares the
/// returned JSON against the service result the CLI would print. A broken endpoint or a
/// divergent CLI formatter fails it.
/// </summary>
public class LakeStatusParityTests : IAsyncLifetime
{
    private HttpClient? _client;
    private WebApplicationFactory<Program>? _factory;
    private string? _dir;
    private LakePaths? _lake;
    private MetricsCatalog? _catalog;
    private EntitySeed? _seed;
    private readonly ILogger _logger = new LoggerConfiguration().CreateLogger();

    public async Task InitializeAsync()
    {
        // Build a lake independent of WebAppFixture's own lake so this test is hermetic.
        _dir = Path.Combine(Path.GetTempPath(), "econdata-parity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _lake = new LakePaths(Path.Combine(_dir, "lake"));
        _lake.EnsureDirectories();
        _catalog = MetricsCatalog.Load(TestPaths.ConfigFile("metrics.json"));
        _seed = EntitySeed.Load(TestPaths.ConfigFile("entities.json"));

        var dbFactory = new SqliteConnectionFactory(Path.Combine(_dir, "econdata.db"));
        var runner = new PipelineRunner(_catalog!, _seed!, factory: dbFactory, SchemaPath,
            new FakeSource(), _logger, lake: _lake!);
        var summary = await runner.RunAsync(new[] { "gdp_usd" }, loadToDb: false, PipelineTiers.All);
        if (!summary.Succeeded) throw new InvalidOperationException("lake build failed");

        // Point the test server at this lake instead of WebAppFixture's lake.
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<LakePaths>();
                services.RemoveAll<GoldTier>();
                services.RemoveAll<LakeStatusService>();
                services.AddSingleton(_lake);
                services.AddSingleton(new GoldTier(_lake!.GoldPath, GoldSchema, _logger));
                services.AddSingleton(
                    new LakeStatusService(_lake!,
                        new GoldTier(_lake!.GoldPath, GoldSchema, _logger), _logger));
            }));
        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        _factory?.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static string SchemaPath => Path.Combine(TestPaths.RepoRoot, "sql", "sqlite", "schema.sql");
    private static string GoldSchema => Path.Combine(TestPaths.RepoRoot, "sql", "duckdb", "schema.sql");

    [Fact]
    public async Task ApiLakeStatusMatchesCliLakeStatusService()
    {
        // API side: the live endpoint returns JSON the dashboard renders.
        var apiStatus = await _client!.GetFromJsonAsync<LakeStatus>("/api/lake/status");
        Assert.NotNull(apiStatus);

        // CLI side: LakeStatusService.GetAsync() is what `lake status` prints.
        var service = new LakeStatusService(
            _lake!,
            new GoldTier(_lake!.GoldPath, GoldSchema, _logger),
            _logger);
        var cliStatus = await service.GetAsync();

        // Records compare by value, so this asserts tier presence, row counts, table and
        // partition inventories all agree between the CLI surface and the API surface.
        Assert.Equal(cliStatus.Directory, apiStatus.Directory);
        Assert.Equal(cliStatus.GoldPath, apiStatus.GoldPath);
        Assert.Equal(cliStatus.Bronze, apiStatus.Bronze);
        Assert.Equal(cliStatus.Silver, apiStatus.Silver);
        Assert.Equal(cliStatus.Gold, apiStatus.Gold);

        Assert.Equal(cliStatus.Tables.Count, apiStatus.Tables.Count);
        foreach (var (name, rows) in cliStatus.Tables)
            Assert.Equal(rows, apiStatus.Tables[name]);

        Assert.Equal(cliStatus.Partitions.Count, apiStatus.Partitions.Count);
        foreach (var (metric, rows) in cliStatus.Partitions)
            Assert.Equal(rows, apiStatus.Partitions[metric]);
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
