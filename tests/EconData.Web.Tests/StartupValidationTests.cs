using EconData.Core.Analytics;
using EconData.Core.Configuration;
using EconData.Core.Data;
using EconData.Core.Lake;
using Serilog;
using Xunit;

namespace EconData.Web.Tests;

/// <summary>
/// The fail-fast rule for strict <c>gold</c> lives in <see cref="AnalyticsService"/>'s
/// constructor, which Program resolves before <c>app.Run()</c>. This asserts the rule
/// directly instead of mutating process environment variables, which would leak into
/// parallel test classes.
/// </summary>
public class StartupValidationTests : IDisposable
{
    private readonly string _dir;
    private readonly ILogger _logger = new LoggerConfiguration().CreateLogger();

    public StartupValidationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "econdata-startup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        (_logger as IDisposable)?.Dispose();
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private (GoldTier Gold, SqliteConnectionFactory Sqlite) CreateStores()
    {
        var goldPath = Path.Combine(_dir, "lake", "gold", "econdata.duckdb");
        var gold = new GoldTier(goldPath, Path.Combine(TestPaths.RepoRoot, "sql", "duckdb", "schema.sql"), _logger);
        return (gold, new SqliteConnectionFactory(Path.Combine(_dir, "vectors.db")));
    }

    [Fact]
    public void StrictGold_WithoutGoldFile_ThrowsBeforeServing()
    {
        var (gold, sqlite) = CreateStores();

        var error = Assert.Throws<InvalidOperationException>(
            () => new AnalyticsService(sqlite, gold, ServingStore.Gold));

        Assert.Contains("ECONDATA_SERVING_STORE=gold", error.Message, StringComparison.Ordinal);
        Assert.Contains("no gold database", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Auto_WithoutGoldFile_DoesNotThrow()
    {
        var (gold, sqlite) = CreateStores();

        var analytics = new AnalyticsService(sqlite, gold, ServingStore.Auto);

        Assert.False(analytics.UsesGold);
    }
}
