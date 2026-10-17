using EconData.Core.Cli;
using EconData.Core.Configuration;
using EconData.Core.Data;
using EconData.Core.Entities;
using EconData.Core.Extraction;
using Microsoft.Data.Sqlite;
using Serilog;

namespace EconData.Tests;

public class PipelineProgressTests : IDisposable
{
    private static readonly string[] DatabaseFileSuffixes = { "", "-wal", "-shm" };

    private readonly string _dbPath;
    private readonly SqliteConnectionFactory _factory;
    private readonly ILogger _logger = new LoggerConfiguration().CreateLogger();

    public PipelineProgressTests()
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
    public async Task RunAsync_ReportsRunAndMetricLifecycle()
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
        var reports = new CollectingProgress<PipelineProgress>();

        await runner.RunAsync(new[] { "gdp_usd" }, loadToDb: true, reports);

        Assert.Equal(PipelineProgressKind.RunStarted, reports.Items[0].Kind);
        Assert.Equal(1, reports.Items[0].Total);

        var started = Assert.Single(reports.Items, item => item.Kind == PipelineProgressKind.MetricStarted);
        Assert.Equal(1, started.Index);
        Assert.Equal(1, started.Total);
        Assert.Equal("gdp_usd", started.MetricCode);

        var completed = Assert.Single(reports.Items, item => item.Kind == PipelineProgressKind.MetricCompleted);
        Assert.Equal("gdp_usd", completed.MetricCode);
        Assert.Equal(3, completed.Records);
        Assert.Equal(3, completed.Upserted);

        var runCompleted = Assert.Single(reports.Items, item => item.Kind == PipelineProgressKind.RunCompleted);
        Assert.Equal(3, runCompleted.Records);
        Assert.Equal(3, runCompleted.Upserted);
    }

    private sealed class CollectingProgress<T> : IProgress<T>
    {
        public List<T> Items { get; } = new();

        public void Report(T value) => Items.Add(value);
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
