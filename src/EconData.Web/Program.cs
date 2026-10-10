using EconData.Core.Analytics;
using EconData.Core.Catalog;
using EconData.Core.Cli;
using EconData.Core.Configuration;
using EconData.Core.Data;
using EconData.Core.Entities;
using EconData.Core.Logging;
using EconData.Web.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

var root = CliPaths.Root();
var configuration = new ConfigurationBuilder()
    .SetBasePath(root)
    .AddJsonFile(Path.Combine("config", "appsettings.json"), optional: true, reloadOnChange: false)
    .AddEnvironmentVariables()
    .Build();

var config = AppConfig.FromConfiguration(configuration);
var logger = LoggingSetup.Configure(
    CliPaths.Resolve(root, config.OutputDir),
    basename: "web",
    console: true);

builder.Logging.AddSerilog(logger, dispose: true);
builder.WebHost.UseUrls("http://127.0.0.1:5000");

builder.Services.AddSingleton(config);
builder.Services.AddSingleton(_ => new SqliteConnectionFactory(CliPaths.Resolve(root, config.SqlitePath)));
builder.Services.AddSingleton(_ => MetricsCatalog.Load(CliPaths.Resolve(root, config.MetricsConfigPath)));
builder.Services.AddSingleton(_ => EntitySeed.Load(CliPaths.Resolve(root, config.EntitiesConfigPath)));
builder.Services.AddSingleton<AnalyticsService>();
builder.Services.AddScoped(sp => new HttpClient
{
    BaseAddress = new Uri(sp.GetRequiredService<NavigationManager>().BaseUri),
});

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapGet("/api/overview", (AnalyticsService analytics) => Results.Ok(analytics.GetOverview()));
app.MapGet("/api/coverage", (AnalyticsService analytics) => Results.Ok(analytics.GetMetricCoverage()));
app.MapGet("/api/series", (AnalyticsService analytics, string entity, string metric) => Results.Ok(analytics.GetSeries(entity, metric)));
app.MapGet("/api/snapshot", (AnalyticsService analytics, string? entity, string? metric, string? category, int limit = 200) => Results.Ok(analytics.GetLatestSnapshot(entity, metric, category, limit)));
app.MapGet("/api/entities", (AnalyticsService analytics, string? q, string? type, int limit = 500) => Results.Ok(analytics.GetEntities(q, type, limit)));
app.MapGet("/api/runs", (AnalyticsService analytics, int limit = 50) => Results.Ok(analytics.GetRuns(limit)));
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

public partial class Program;
