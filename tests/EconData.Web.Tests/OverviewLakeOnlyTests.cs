using System.Net;
using System.Net.Http.Json;
using EconData.Core.Analytics;
using EconData.Core.Data;
using EconData.Core.Lake;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Serilog;
using Xunit;

namespace EconData.Web.Tests;

/// <summary>
/// Regression: <c>/api/overview</c> used to guard only on the SQLite file, so a
/// lake-only deployment (gold present, no SQLite database yet) answered 503
/// "database not initialized" even though analytics could serve from gold.
/// </summary>
public class OverviewLakeOnlyTests
{
    [Fact]
    public async Task GetOverview_GoldPresentSqliteAbsent_Returns200()
    {
        var dir = Path.Combine(Path.GetTempPath(), "econdata-lakeonly-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var absentSqlite = Path.Combine(dir, "absent.db");
        var lake = new LakePaths(Path.Combine(dir, "lake"));
        lake.EnsureDirectories();

        using var logger = new LoggerConfiguration().CreateLogger();
        var gold = new GoldTier(lake.GoldPath, Path.Combine(TestPaths.RepoRoot, "sql", "duckdb", "schema.sql"), logger);
        gold.EnsureSchema();

        try
        {
            using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
                {
                    services.RemoveAll<SqliteConnectionFactory>();
                    services.AddSingleton(new SqliteConnectionFactory(absentSqlite));
                    services.RemoveAll<AnalyticsService>();
                    services.AddSingleton(sp => new AnalyticsService(
                        sp.GetRequiredService<SqliteConnectionFactory>(),
                        gold));
                }));

            using var client = factory.CreateClient();
            var response = await client.GetAsync("/api/overview");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var overview = await response.Content.ReadFromJsonAsync<OverviewStats>();
            Assert.NotNull(overview);
            Assert.Equal(0, overview!.EmbeddingCount);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
