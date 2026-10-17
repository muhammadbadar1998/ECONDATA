using EconData.Core.Data;
using EconData.Web.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EconData.Web.Tests;

public class DashboardWebAppFixture : IAsyncLifetime
{
    private static readonly string[] DatabaseFileSuffixes = { "", "-wal", "-shm" };

    private readonly string _dbPath;
    private WebApplicationFactory<Program>? _factory;

    public HttpClient Client { get; private set; } = null!;

    public string LayoutPath { get; } = Path.Combine(
        Path.GetTempPath(),
        "econdata-layout-" + Guid.NewGuid().ToString("N") + ".json");

    public DashboardWebAppFixture()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "econdata-dash-" + Guid.NewGuid().ToString("N") + ".db");
    }

    public Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<SqliteConnectionFactory>();
                    services.AddSingleton(new SqliteConnectionFactory(_dbPath));
                    services.RemoveAll<DashboardLayoutStore>();
                    services.AddSingleton(new DashboardLayoutStore(LayoutPath));
                });
            });

        Client = _factory.CreateClient();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        Client?.Dispose();
        _factory?.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var suffix in DatabaseFileSuffixes)
        {
            var path = _dbPath + suffix;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        if (File.Exists(LayoutPath))
        {
            File.Delete(LayoutPath);
        }

        GC.SuppressFinalize(this);
        return Task.CompletedTask;
    }
}
