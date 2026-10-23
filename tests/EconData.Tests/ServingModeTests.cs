using EconData.Core.Analytics;
using EconData.Core.Configuration;
using EconData.Core.Data;
using EconData.Core.Lake;
using Microsoft.Data.Sqlite;
using Serilog;

namespace EconData.Tests;

public class ServingModeTests : IDisposable
{
    private readonly string _dir;
    private readonly string _lakeDir;
    private readonly SqliteConnectionFactory _sqlite;
    private readonly GoldTier _gold;
    private readonly ILogger _logger = new LoggerConfiguration().CreateLogger();

    public ServingModeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "econdata-serving-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _lakeDir = Path.Combine(_dir, "lake");
        _sqlite = new SqliteConnectionFactory(Path.Combine(_dir, "serving.db"));
        _gold = new GoldTier(Path.Combine(_lakeDir, "econdata.duckdb"), Path.Combine(TestPaths.RepoRoot, "sql", "duckdb", "schema.sql"), _logger);
        _gold.EnsureSchema();

        new SchemaInitializer(_sqlite, _logger).Initialize(Path.Combine(TestPaths.RepoRoot, "sql/sqlite/schema.sql"));
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
    public void AutoMode_UsesGoldWhenGoldExists()
    {
        var analytics = new AnalyticsService(_sqlite, _gold, ServingStore.Auto);
        Assert.True(analytics.UsesGold);
    }

    [Fact]
    public void AutoMode_UsesSqliteWhenNoGold()
    {
        var noGoldDir = Path.Combine(Path.GetTempPath(), "econdata-nogold-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(noGoldDir);
        try
        {
            var noGoldLake = new LakePaths(Path.Combine(noGoldDir, "lake"));
            noGoldLake.EnsureDirectories();
            var noGoldFactory = new SqliteConnectionFactory(Path.Combine(noGoldDir, "serving.db"));
            new SchemaInitializer(noGoldFactory, _logger).Initialize(Path.Combine(TestPaths.RepoRoot, "sql/sqlite/schema.sql"));

            var analytics = new AnalyticsService(noGoldFactory, null, ServingStore.Auto);
            Assert.False(analytics.UsesGold);
        }
        finally
        {
            if (Directory.Exists(noGoldDir)) Directory.Delete(noGoldDir, recursive: true);
        }
    }

    [Fact]
    public void GoldMode_UsesGoldWhenGoldExists()
    {
        var analytics = new AnalyticsService(_sqlite, _gold, ServingStore.Gold);
        Assert.True(analytics.UsesGold);
    }

    [Fact]
    public void GoldMode_ThrowsWhenGoldMissing()
    {
        var noGoldDir = Path.Combine(Path.GetTempPath(), "econdata-nogold-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(noGoldDir);
        try
        {
            var noGoldLake = new LakePaths(Path.Combine(noGoldDir, "lake"));
            noGoldLake.EnsureDirectories();
            var noGold = new GoldTier(noGoldLake.GoldPath, Path.Combine(TestPaths.RepoRoot, "sql", "duckdb", "schema.sql"), _logger);
            Assert.Throws<InvalidOperationException>(() =>
                new AnalyticsService(_sqlite, noGold, ServingStore.Gold));
        }
        finally
        {
            if (Directory.Exists(noGoldDir)) Directory.Delete(noGoldDir, recursive: true);
        }
    }

    [Fact]
    public void SqliteMode_UsesSqliteWhenGoldExists()
    {
        var analytics = new AnalyticsService(_sqlite, _gold, ServingStore.Sqlite);
        Assert.False(analytics.UsesGold);
    }
}
