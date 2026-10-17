using EconData.Core.Ai;
using EconData.Core.Analytics;
using EconData.Core.Catalog;
using EconData.Core.Cli;
using EconData.Core.Configuration;
using EconData.Core.Data;
using EconData.Core.Entities;
using EconData.Core.Extraction;
using EconData.Core.Logging;
using EconData.Web.Components;
using EconData.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

var root = CliPaths.Root();
var configuration = new ConfigurationBuilder()
    .SetBasePath(root)
    .AddJsonFile(Path.Combine("config", "appsettings.json"), optional: true, reloadOnChange: false)
    .AddEnvironmentVariables()
    .Build();

var config = AppConfig.FromConfiguration(configuration);
var logSink = new InMemoryLogSink();
var logger = LoggingSetup.Configure(
    CliPaths.Resolve(root, config.OutputDir),
    basename: "web",
    console: true,
    extraSink: logSink);

builder.Logging.AddSerilog(logger, dispose: true);
builder.WebHost.UseUrls("http://127.0.0.1:5000");

var schemaPath = CliPaths.Resolve(root, "sql/sqlite/schema.sql");
var vecPath = CliPaths.Resolve(root, config.SqliteVecExtensionPath);

builder.Services.AddSingleton(config);
builder.Services.AddSingleton(logSink);
builder.Services.AddSingleton(_ => new SqliteConnectionFactory(CliPaths.Resolve(root, config.SqlitePath)));
builder.Services.AddSingleton(_ => MetricsCatalog.Load(CliPaths.Resolve(root, config.MetricsConfigPath)));
builder.Services.AddSingleton(_ => EntitySeed.Load(CliPaths.Resolve(root, config.EntitiesConfigPath)));
builder.Services.AddSingleton<AnalyticsService>();
builder.Services.AddSingleton<ISeriesSource>(_ => new DBnomicsExtractor(new HttpClient(), config.DbNomicsBaseUrl, logger: logger));
builder.Services.AddSingleton<IEmbedder>(_ => new OllamaEmbedder(config, new HttpClient(), logger));
builder.Services.AddSingleton(sp => new VectorStore(sp.GetRequiredService<SqliteConnectionFactory>(), vecPath, logger));
builder.Services.AddSingleton(sp => new SchemaInitializer(sp.GetRequiredService<SqliteConnectionFactory>(), logger));
builder.Services.AddSingleton<IJobExecutor>(sp => new PipelineJobExecutor(
    sp.GetRequiredService<SqliteConnectionFactory>(),
    sp.GetRequiredService<SchemaInitializer>(),
    sp.GetRequiredService<MetricsCatalog>(),
    sp.GetRequiredService<EntitySeed>(),
    sp.GetRequiredService<ISeriesSource>(),
    sp.GetRequiredService<IEmbedder>(),
    sp.GetRequiredService<VectorStore>(),
    schemaPath,
    logger));
builder.Services.AddSingleton<JobService>();
builder.Services.AddSingleton(_ => new DashboardLayoutStore(
    Path.Combine(CliPaths.Resolve(root, config.OutputDir), "dashboards.json")));
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

app.MapGet("/api/catalog/metrics", (MetricsCatalog catalog) => Results.Ok(catalog.ByCode
    .Select(pair => new MetricItem(pair.Key, pair.Value.MetricName, pair.Value.Category, pair.Value.Frequency))
    .ToList()));

app.MapPost("/api/jobs", (JobRequest request, JobService jobs) =>
{
    if (!Enum.IsDefined(request.Kind))
    {
        return Results.BadRequest(new { error = "unknown job kind" });
    }

    var (started, jobId) = jobs.TryStart(request);
    if (!started)
    {
        return Results.Conflict(new { busy = true, job = jobs.Snapshot() });
    }

    return Results.Accepted($"/api/jobs/{jobId}", new { job_id = jobId });
});

app.MapGet("/api/jobs/current", (JobService jobs) => Results.Ok(jobs.Snapshot()));

app.MapPost("/api/jobs/{id}/cancel", (string id, JobService jobs) =>
    jobs.Cancel(id) ? Results.Accepted() : Results.NotFound());

app.MapGet("/api/jobs/{id}/log", (string id, long since, JobService jobs, InMemoryLogSink sink) =>
{
    var current = jobs.Snapshot().JobId;
    return Results.Ok(string.Equals(current, id, StringComparison.Ordinal) ? sink.Snapshot(since) : Array.Empty<LogEntry>());
});

app.MapGet("/api/dashboard/layout", (DashboardLayoutStore store) => Results.Ok(store.Load()));

app.MapPut("/api/dashboard/layout", async (DashboardLayout? layout, DashboardLayoutStore store) =>
{
    var error = DashboardLayoutStore.Validate(layout);
    if (error is not null)
    {
        return Results.BadRequest(new { error });
    }

    await store.SaveAsync(layout!);
    return Results.NoContent();
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

public partial class Program;
