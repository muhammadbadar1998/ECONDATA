using System.Globalization;
using EconData.Core.Configuration;
using EconData.Core.Data;
using EconData.Core.Diagnostics;
using EconData.Core.Lake;
using Microsoft.Data.Sqlite;
using Serilog;

namespace EconData.Tests;

public class DiagnosticsServiceTests : IDisposable
{
    private static readonly string[] Suffixes = { "", "-wal", "-shm" };
    private readonly string _dbPath;
    private readonly SqliteConnectionFactory _factory;
    private readonly ILogger _logger = new LoggerConfiguration().CreateLogger();

    public DiagnosticsServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "econdata-doctor-" + Guid.NewGuid().ToString("N") + ".db");
        _factory = new SqliteConnectionFactory(_dbPath);
    }

    public void Dispose()
    {
        (_logger as IDisposable)?.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var suffix in Suffixes)
        {
            var path = _dbPath + suffix;
            if (File.Exists(path)) File.Delete(path);
        }
        GC.SuppressFinalize(this);
    }

    private DiagnosticsService Create() => new(
        AppConfig.Load(TestPaths.RepoRoot),
        _factory,
        _logger);

    private void InitSchema()
    {
        new SchemaInitializer(_factory, _logger)
            .Initialize(Path.Combine(TestPaths.RepoRoot, "sql", "sqlite", "schema.sql"));
    }

    [Fact]
    public async Task ReportsPassForHealthyDatabase()
    {
        InitSchema();
        var report = await Create().RunAsync();
        Assert.False(report.HasBlockers);
        Assert.Contains(report.Checks, c => c.Name == "integrity" && c.Diagnosis == Diagnosis.Pass);
        Assert.Contains(report.Checks, c => c.Name == "schema" && c.Diagnosis == Diagnosis.Pass);
        Assert.Contains(report.Checks, c => c.Name == "vector" && c.Diagnosis == Diagnosis.Pass);
        Assert.Contains(report.Checks, c => c.Name == "ollama" && c.Diagnosis == Diagnosis.Warn);
    }

    [Fact]
    public async Task WarnsWhenDatabaseMissing()
    {
        var report = await Create().RunAsync();
        Assert.False(report.HasBlockers);
        var db = report.Checks.Single(c => c.Name == "database");
        Assert.Equal(Diagnosis.Warn, db.Diagnosis);
    }

    [Fact]
    public async Task BlocksWhenDatabaseCorrupt()
    {
        File.WriteAllText(_dbPath, "not a sqlite database");
        var report = await Create().RunAsync();
        Assert.True(report.HasBlockers);
        Assert.Contains(report.Checks, c => c.Diagnosis == Diagnosis.Blocker);
    }

    [Fact]
    public async Task BlocksWhenVectorExtensionCannotLoad()
    {
        InitSchema();
        var fake = Path.Combine(Path.GetTempPath(), "vec0.so");
        File.WriteAllText(fake, "not an elf");
        var baseCfg = AppConfig.Load(TestPaths.RepoRoot);
        var cfg = new AppConfig
        {
            SqlitePath = baseCfg.SqlitePath,
            MetricsConfigPath = baseCfg.MetricsConfigPath,
            EntitiesConfigPath = baseCfg.EntitiesConfigPath,
            SqliteVecExtensionPath = fake,
        };
        var service = new DiagnosticsService(cfg, _factory, _logger);
        var report = await service.RunAsync();
        Assert.True(report.HasBlockers);
        Assert.Contains(report.Checks, c => c.Name == "vector" && c.Diagnosis == Diagnosis.Blocker);
        File.Delete(fake);
    }

    [Fact]
    public async Task FlagsStaleSnapshotView()
    {
        InitSchema();
        using (var conn = _factory.Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "DROP VIEW IF EXISTS v_latest_metrics_snapshot; CREATE VIEW v_latest_metrics_snapshot AS SELECT 1 AS country_code, COALESCE(1,0) AS value;";
            cmd.ExecuteNonQuery();
        }
        var report = await Create().RunAsync();
        Assert.True(report.HasBlockers);
        Assert.Contains(report.Checks, c => c.Name == "schema" && c.Diagnosis == Diagnosis.Blocker);
    }

    [Fact]
    public async Task DoesNotMutateDatabase()
    {
        InitSchema();
        var rowsBefore = CountTables();
        await Create().RunAsync();
        var rowsAfter = CountTables();
        Assert.Equal(rowsBefore, rowsAfter);
    }

    [Fact]
    public async Task DoesNotMutateJournalMode()
    {
        InitSchema();
        using (var conn = _factory.Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA journal_mode=DELETE;";
            cmd.ExecuteScalar();
        }

        await Create().RunAsync();

        using var verify = _factory.OpenReadOnly();
        using var cmd2 = verify.CreateCommand();
        cmd2.CommandText = "PRAGMA journal_mode;";
        var mode = Convert.ToString(cmd2.ExecuteScalar(), CultureInfo.InvariantCulture);
        Assert.Equal("delete", mode, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VerifyDoesNotCreateMissingDatabase()
    {
        var missing = Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N") + ".db");
        var factory = new SqliteConnectionFactory(missing);
        var init = new SchemaInitializer(factory, _logger);
        Assert.False(init.Verify(out var message));
        Assert.Contains("not found", message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(missing));
    }

    [Fact]
    public async Task ProbeFailuresAreWarnings()
    {
        InitSchema();
        var service = new DiagnosticsService(
            AppConfig.Load(TestPaths.RepoRoot),
            _factory,
            _logger,
            llmProbe: _ => throw new InvalidOperationException("boom"));
        var report = await service.RunAsync();
        var llm = report.Checks.Single(c => c.Name == "llm");
        Assert.Equal(Diagnosis.Warn, llm.Diagnosis);
        Assert.False(report.HasBlockers);
    }

    [Fact]
    public async Task ProbeTimeoutIsBounded()
    {
        InitSchema();
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new DiagnosticsService(
            AppConfig.Load(TestPaths.RepoRoot),
            _factory,
            _logger,
            llmProbe: _ => gate.Task);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var report = await service.RunAsync();
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(6), $"took {sw.Elapsed}");
        var llm = report.Checks.Single(c => c.Name == "llm");
        Assert.Equal(Diagnosis.Warn, llm.Diagnosis);
        Assert.Contains("timed out", llm.Detail, StringComparison.OrdinalIgnoreCase);
        gate.TrySetResult(true);
    }

    [Fact]
    public async Task WarnsWhenLakeAbsent()
    {
        var dir = Path.Combine(Path.GetTempPath(), "econdata-doctor-lake-" + Guid.NewGuid().ToString("N"));
        try
        {
            var report = await CreateWithLake(dir).RunAsync();
            var lake = report.Checks.Single(c => c.Name == "lake");
            Assert.Equal(Diagnosis.Warn, lake.Diagnosis);
            Assert.Contains("not built", lake.Detail, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task PassesWhenGoldIsPresent()
    {
        var dir = Path.Combine(Path.GetTempPath(), "econdata-doctor-lake-" + Guid.NewGuid().ToString("N"));
        try
        {
            var lakePaths = new LakePaths(Path.Combine(dir, "lake"));
            lakePaths.EnsureDirectories();
            new GoldTier(
                    lakePaths.GoldPath,
                    Path.Combine(TestPaths.RepoRoot, "sql", "duckdb", "schema.sql"),
                    _logger)
                .EnsureSchema();

            var report = await CreateWithLake(lakePaths.LakeDir).RunAsync();
            var lake = report.Checks.Single(c => c.Name == "lake");
            Assert.Equal(Diagnosis.Pass, lake.Diagnosis);
            Assert.Contains("gold", lake.Detail, StringComparison.Ordinal);
            Assert.False(report.HasBlockers);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task WarnsWhenGoldPresentButUninitialized()
    {
        var dir = Path.Combine(Path.GetTempPath(), "econdata-doctor-lake-" + Guid.NewGuid().ToString("N"));
        try
        {
            var lakePaths = new LakePaths(Path.Combine(dir, "lake"));
            lakePaths.EnsureDirectories();
            // gold file exists but is not a valid initialized DuckDB database
            File.WriteAllBytes(lakePaths.GoldPath, Array.Empty<byte>());

            var report = await CreateWithLake(lakePaths.LakeDir).RunAsync();
            var lake = report.Checks.Single(c => c.Name == "lake");
            Assert.Equal(Diagnosis.Warn, lake.Diagnosis);
            Assert.Contains("lake init", lake.Detail, StringComparison.Ordinal);
            Assert.False(report.HasBlockers);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task PassesAfterLakeInit()
    {
        var dir = Path.Combine(Path.GetTempPath(), "econdata-doctor-lake-" + Guid.NewGuid().ToString("N"));
        try
        {
            var lakePaths = new LakePaths(Path.Combine(dir, "lake"));
            lakePaths.EnsureDirectories();
            new GoldTier(
                    lakePaths.GoldPath,
                    Path.Combine(TestPaths.RepoRoot, "sql", "duckdb", "schema.sql"),
                    _logger)
                .EnsureSchema();
            // idempotent: initialize again
            new GoldTier(
                    lakePaths.GoldPath,
                    Path.Combine(TestPaths.RepoRoot, "sql", "duckdb", "schema.sql"),
                    _logger)
                .EnsureSchema();

            var report = await CreateWithLake(lakePaths.LakeDir).RunAsync();
            var lake = report.Checks.Single(c => c.Name == "lake");
            Assert.Equal(Diagnosis.Pass, lake.Diagnosis);
            Assert.Contains("gold", lake.Detail, StringComparison.Ordinal);
            Assert.False(report.HasBlockers);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    private DiagnosticsService CreateWithLake(string lakeDir)
    {
        var baseCfg = AppConfig.Load(TestPaths.RepoRoot);
        var cfg = new AppConfig
        {
            SqlitePath = baseCfg.SqlitePath,
            MetricsConfigPath = baseCfg.MetricsConfigPath,
            EntitiesConfigPath = baseCfg.EntitiesConfigPath,
            SqliteVecExtensionPath = baseCfg.SqliteVecExtensionPath,
            LakeDir = lakeDir,
        };
        return new DiagnosticsService(cfg, _factory, _logger);
    }

    private int CountTables()
    {
        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name LIKE 'dbe_%';";
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
}
