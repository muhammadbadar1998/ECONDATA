using System.Globalization;
using EconData.Core.Ai;
using EconData.Core.Cli;
using EconData.Core.Configuration;
using EconData.Core.Data;
using EconData.Core.Entities;
using Microsoft.Data.Sqlite;
using Serilog;
using Serilog.Core;

namespace EconData.Core.Diagnostics;

/// <summary>Read-only readiness checks for the EconData environment and database.</summary>
public sealed class DiagnosticsService
{
    private readonly AppConfig _config;
    private readonly SqliteConnectionFactory _factory;
    private readonly ILogger _logger;
    private readonly IEmbedder? _embedder;
    private readonly Func<CancellationToken, Task<bool>>? _llmProbe;

    public DiagnosticsService(
        AppConfig config,
        SqliteConnectionFactory factory,
        ILogger? logger = null,
        IEmbedder? embedder = null,
        Func<CancellationToken, Task<bool>>? llmProbe = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(factory);
        _config = config;
        _factory = factory;
        _logger = logger ?? Logger.None;
        _embedder = embedder;
        _llmProbe = llmProbe;
    }

    public async Task<DoctorReport> RunAsync(CancellationToken cancellationToken = default)
    {
        var checks = new List<CheckResult>();

        // 1. Runtime
        var version = Environment.Version;
        checks.Add(version.Major >= 10
            ? new CheckResult("runtime", Diagnosis.Pass, $".NET {version}")
            : new CheckResult("runtime", Diagnosis.Blocker, $".NET {version} (need 10+)"));

        // 2. Config
        try
        {
            var root = CliPaths.Root();
            MetricsCatalog.Load(CliPaths.Resolve(root, _config.MetricsConfigPath));
            EntitySeed.Load(CliPaths.Resolve(root, _config.EntitiesConfigPath));
            checks.Add(new CheckResult("config", Diagnosis.Pass, "metrics and entity seed loaded"));
        }
        catch (Exception ex)
        {
            checks.Add(new CheckResult("config", Diagnosis.Blocker, ex.Message));
        }

        var dbPath = _factory.DatabasePath;
        var dbExists = File.Exists(dbPath);

        // 3. Database file
        if (dbExists)
        {
            checks.Add(new CheckResult("database", Diagnosis.Pass, dbPath));
        }
        else
        {
            checks.Add(new CheckResult("database", Diagnosis.Warn, $"{dbPath} not found; run `init-db`"));
        }

        if (!dbExists)
        {
            checks.Add(new CheckResult("integrity", Diagnosis.Warn, "database absent"));
            checks.Add(new CheckResult("schema", Diagnosis.Warn, "database absent"));
            checks.Add(new CheckResult("vector", Diagnosis.Warn, "database absent"));
            await AddProbeChecksAsync(checks, cancellationToken).ConfigureAwait(false);
            return new DoctorReport(checks);
        }

        // 4. Integrity
        try
        {
            using var connection = _factory.OpenReadOnly();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA integrity_check;";
            var integrity = Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            checks.Add(string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase)
                ? new CheckResult("integrity", Diagnosis.Pass, $"integrity_check={integrity}")
                : new CheckResult("integrity", Diagnosis.Blocker, $"integrity_check={integrity}"));
        }
        catch (Exception ex)
        {
            checks.Add(new CheckResult("integrity", Diagnosis.Blocker, ex.Message));
        }

        // 5. Schema + view descriptor
        try
        {
            var initializer = new SchemaInitializer(_factory, _logger);
            if (!initializer.Verify(out var message))
            {
                checks.Add(new CheckResult("schema", Diagnosis.Blocker, message));
            }
            else
            {
                var viewSql = ReadViewSql();
                if (viewSql.Contains("COALESCE", StringComparison.OrdinalIgnoreCase))
                {
                    checks.Add(new CheckResult("schema", Diagnosis.Blocker, "v_latest_metrics_snapshot still uses COALESCE (stale view)"));
                }
                else if (!viewSql.Contains("lo.val AS value", StringComparison.OrdinalIgnoreCase)
                         && !viewSql.Contains("o.val AS value", StringComparison.OrdinalIgnoreCase))
                {
                    checks.Add(new CheckResult("schema", Diagnosis.Warn, "snapshot view value column not recognized"));
                }
                else
                {
                    checks.Add(new CheckResult("schema", Diagnosis.Pass, "dbe_ tables present; snapshot view current"));
                }
            }
        }
        catch (Exception ex)
        {
            checks.Add(new CheckResult("schema", Diagnosis.Blocker, ex.Message));
        }

        // 6. Vector extension
        await RunVectorCheckAsync(checks).ConfigureAwait(false);

        try
        {
            using var connection = _factory.OpenReadOnly();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='dbe_derived_observations';";
            var t = Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            using var cmd2 = connection.CreateCommand();
            cmd2.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='view' AND name='v_observations_all';";
            var v = Convert.ToInt32(cmd2.ExecuteScalar(), CultureInfo.InvariantCulture);
            checks.Add(t > 0 && v > 0
                ? new CheckResult("aggregates", Diagnosis.Pass, "derived table and unified view present")
                : new CheckResult("aggregates", Diagnosis.Warn, "derived aggregates not initialized"));
        }
        catch (Exception ex)
        {
            checks.Add(new CheckResult("aggregates", Diagnosis.Warn, ex.Message));
        }

        await AddProbeChecksAsync(checks, cancellationToken).ConfigureAwait(false);

        return new DoctorReport(checks);
    }

    private string ReadViewSql()
    {
        using var connection = _factory.OpenReadOnly();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type='view' AND name='v_latest_metrics_snapshot';";
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private Task RunVectorCheckAsync(List<CheckResult> checks)
    {
        var vecPath = CliPaths.Resolve(CliPaths.Root(), _config.SqliteVecExtensionPath);
        var fileExists = File.Exists(vecPath);

        bool vecTableExists;
        try
        {
            using var connection = _factory.OpenReadOnly();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='dbe_embeddings_vec';";
            vecTableExists = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
        }
        catch (SqliteException ex)
        {
            _logger.Debug(ex, "Could not probe dbe_embeddings_vec presence.");
            vecTableExists = false;
        }

        if (!fileExists)
        {
            checks.Add(vecTableExists
                ? new CheckResult("vector", Diagnosis.Blocker, $"extension missing at {vecPath} but vec table exists")
                : new CheckResult("vector", Diagnosis.Warn, $"extension not found at {vecPath}"));
            return Task.CompletedTask;
        }

        try
        {
            using var connection = _factory.OpenReadOnly();
            connection.LoadExtension(vecPath);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT vec_version();";
            var ver = Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            checks.Add(new CheckResult("vector", Diagnosis.Pass, $"sqlite-vec {ver}"));
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or SqliteException or InvalidOperationException)
        {
            checks.Add(new CheckResult("vector", Diagnosis.Blocker, $"failed to load {vecPath}: {ex.Message}"));
        }
        return Task.CompletedTask;
    }

    private async Task AddProbeChecksAsync(List<CheckResult> checks, CancellationToken cancellationToken)
    {
        // 7. Ollama embedder
        if (_embedder is null)
        {
            checks.Add(new CheckResult("ollama", Diagnosis.Warn, "not configured"));
        }
        else
        {
            await RunProbeAsync(checks, "ollama", ct => _embedder.VerifyConnectivityAsync(ct), cancellationToken).ConfigureAwait(false);
        }

        // 8. LLM endpoint
        if (_llmProbe is null)
        {
            checks.Add(new CheckResult("llm", Diagnosis.Warn, "not configured"));
        }
        else
        {
            await RunProbeAsync(checks, "llm", ct => _llmProbe!(ct), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RunProbeAsync(List<CheckResult> checks, string name, Func<CancellationToken, Task<bool>> probe, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(3));

        var probeTask = SafeProbe(probe, cts.Token);
        var delayTask = Task.Delay(TimeSpan.FromSeconds(3), cts.Token);

        var winner = await Task.WhenAny(probeTask, delayTask).ConfigureAwait(false);
        if (winner == delayTask)
        {
            cts.Cancel();
            _ = probeTask.ContinueWith(_ => { }, TaskContinuationOptions.OnlyOnFaulted);
            checks.Add(new CheckResult(name, Diagnosis.Warn, "timed out after 3s"));
            return;
        }

        try
        {
            var ok = await probeTask.ConfigureAwait(false);
            checks.Add(ok
                ? new CheckResult(name, Diagnosis.Pass, "reachable")
                : new CheckResult(name, Diagnosis.Warn, "unreachable"));
        }
        catch (Exception ex)
        {
            checks.Add(new CheckResult(name, Diagnosis.Warn, ex.Message));
        }
    }

    private static async Task<bool> SafeProbe(Func<CancellationToken, Task<bool>> probe, CancellationToken cancellationToken)
    {
        return await probe(cancellationToken).ConfigureAwait(false);
    }
}
