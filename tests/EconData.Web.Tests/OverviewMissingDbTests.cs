using System.Net;
using EconData.Core.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace EconData.Web.Tests;

public class OverviewMissingDbTests
{
    [Fact]
    public async Task GetOverview_MissingDatabase_Returns503AndDoesNotCreateFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "econdata-missing-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
                {
                    services.RemoveAll<SqliteConnectionFactory>();
                    services.AddSingleton(new SqliteConnectionFactory(path));
                }));

            using var client = factory.CreateClient();
            var response = await client.GetAsync("/api/overview");

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.False(File.Exists(path), "request should not create an empty database file");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                if (File.Exists(path + suffix))
                {
                    File.Delete(path + suffix);
                }
            }
        }
    }
}
