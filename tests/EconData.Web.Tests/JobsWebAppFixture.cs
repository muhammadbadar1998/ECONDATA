using EconData.Core.Data;
using EconData.Web.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Serilog;

namespace EconData.Web.Tests;

public class JobsWebAppFixture : IAsyncLifetime
{
    private static readonly string[] DatabaseFileSuffixes = { "", "-wal", "-shm" };

    private readonly string _dbPath;
    private WebApplicationFactory<Program>? _factory;

    public HttpClient Client { get; private set; } = null!;

    public FakeJobExecutor Executor { get; private set; } = null!;

    public JobsWebAppFixture()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "econdata-jobs-" + Guid.NewGuid().ToString("N") + ".db");
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
                    services.RemoveAll<IJobExecutor>();
                    services.AddSingleton<IJobExecutor>(sp =>
                    {
                        var sink = sp.GetRequiredService<InMemoryLogSink>();
                        return new FakeJobExecutor(new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger());
                    });
                });
            });

        Client = _factory.CreateClient();
        Executor = (FakeJobExecutor)_factory.Services.GetRequiredService<IJobExecutor>();
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

        GC.SuppressFinalize(this);
        return Task.CompletedTask;
    }
}
