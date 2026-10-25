using EconData.Core.Analytics;
using EconData.Core.Cli;
using EconData.Core.Configuration;
using EconData.Core.Data;
using EconData.Core.Entities;
using EconData.Core.Extraction;
using EconData.Core.Lake;
using Microsoft.Data.Sqlite;
using Serilog;

namespace EconData.Tests;

/// <summary>
/// Covers the CLI wiring for <c>lake import-from-sqlite</c>: exit codes, the parity table,
/// and the dry-run / write-silver flags. The data layer is covered by
/// <see cref="ImportFromSqliteTests"/>; this class covers the command seam.
/// </summary>
public class LakeImportCommandTests : IDisposable
{
    private readonly string _dir;
    private readonly SqliteConnectionFactory _sqlite;
    private readonly LakePaths _lake;
    private readonly GoldTier _gold;
    private readonly ILogger _logger = new LoggerConfiguration().CreateLogger();

    public LakeImportCommandTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "econdata-import-cmd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _sqlite = new SqliteConnectionFactory(Path.Combine(_dir, "econdata.db"));
        _lake = new LakePaths(Path.Combine(_dir, "lake"));
        _lake.EnsureDirectories();
        _gold = new GoldTier(_lake.GoldPath, Path.Combine(TestPaths.RepoRoot, "sql", "duckdb", "schema.sql"), _logger);
        _gold.EnsureSchema();
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

    private void SeedSqlite()
    {
        var catalog = MetricsCatalog.Load(TestPaths.ConfigFile("metrics.json"));
        var seed = EntitySeed.Load(TestPaths.ConfigFile("entities.json"));
        new PipelineRunner(
            catalog,
            seed,
            _sqlite,
            Path.Combine(TestPaths.RepoRoot, "sql", "sqlite", "schema.sql"),
            new FakeSource(),
            _logger)
            .RunAsync(new[] { "gdp_usd" }, loadToDb: true)
            .GetAwaiter().GetResult();
    }

    private long GoldScalar(string sql)
    {
        using var connection = _gold.OpenReadOnly();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private (int Exit, ImportSummary Summary, string Out, string Err) Run(bool dryRun, bool writeSilver)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var silver = new SilverTier(_lake, _logger);
        var (exit, summary) = LakeImportCommand
            .ExecuteAsync(dryRun, writeSilver, _gold, _sqlite, silver, stdout, stderr)
            .GetAwaiter().GetResult();
        return (exit, summary, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void DryRun_ReturnsZeroAndLeavesGoldEmpty()
    {
        SeedSqlite();

        var (exit, summary, stdout, stderr) = Run(dryRun: true, writeSilver: false);

        Assert.Equal(0, exit);
        Assert.True(summary.DryRun);
        Assert.Empty(stderr);
        Assert.Contains("dry run", stdout, StringComparison.Ordinal);
        Assert.Equal(0, GoldScalar("SELECT COUNT(*) FROM dbe_entities"));
        Assert.Equal(0, GoldScalar("SELECT COUNT(*) FROM dbe_metrics"));
        Assert.Equal(0, GoldScalar("SELECT COUNT(*) FROM dbe_sources"));
        Assert.Equal(0, GoldScalar("SELECT COUNT(*) FROM dbe_observations"));
        Assert.Equal(85, summary.EntitiesSource);
        Assert.Equal(3, summary.ObservationsSource);
    }

    [Fact]
    public void Apply_ReturnsZeroAndReportsParity()
    {
        SeedSqlite();

        var (exit, summary, stdout, stderr) = Run(dryRun: false, writeSilver: false);

        Assert.Equal(0, exit);
        Assert.True(summary.IsMatch);
        Assert.Empty(stderr);
        Assert.Contains("ok", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("MISMATCH", stdout, StringComparison.Ordinal);
        Assert.Equal(3, GoldScalar("SELECT COUNT(*) FROM dbe_observations"));
    }

    [Fact]
    public void WriteSilver_MaterialisesPartition()
    {
        SeedSqlite();

        var (exit, _, stdout, _) = Run(dryRun: false, writeSilver: true);

        Assert.Equal(0, exit);
        Assert.Contains("Silver: wrote 1 partitions", stdout, StringComparison.Ordinal);
        Assert.True(new SilverTier(_lake, _logger).HasPartition("gdp_usd"));
    }

    [Fact]
    public void DivergedSource_ReturnsOneAndReportsMismatch()
    {
        SeedSqlite();
        var (firstExit, _, _, _) = Run(dryRun: false, writeSilver: false);
        Assert.Equal(0, firstExit);

        // Remove observations from the source so source < gold. A second import only
        // upserts, so the gold rows remain and the counts diverge.
        using (var connection = _sqlite.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DELETE FROM dbe_observations";
            command.ExecuteNonQuery();
        }

        var (exit, summary, _, stderr) = Run(dryRun: false, writeSilver: false);

        Assert.Equal(1, exit);
        Assert.False(summary.IsMatch);
        Assert.False(summary.ObservationsMatch);
        Assert.Contains("Mismatch detected:", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_PrintsMismatchKeysAndReturnsOne()
    {
        var summary = new ImportSummary(
            EntitiesSource: 2,
            EntitiesGold: 1,
            MetricsSource: 1,
            MetricsGold: 1,
            SourcesSource: 1,
            SourcesGold: 1,
            ObservationsSource: 1,
            ObservationsGold: 1,
            MismatchKeys: new[] { "entity missing in gold: AAA" });

        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = LakeImportCommand.Render(summary, dryRun: false, "/tmp/gold.duckdb", stdout, stderr);

        Assert.Equal(1, exit);
        Assert.Contains("MISMATCH", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("entity missing in gold: AAA", stderr.ToString(), StringComparison.Ordinal);
    }

    private sealed class FakeSource : ISeriesSource
    {
        public Task<IReadOnlyList<RawSeries>> FetchMetricSeriesAsync(
            string metricCode,
            MetricsCatalog catalog,
            int limit = 1000,
            CancellationToken cancellationToken = default)
        {
            var series = new RawSeries(
                new Dictionary<string, string> { ["country"] = "USA" },
                new[] { "2022", "2023", "2024" },
                new[] { "100", "200", "300" },
                "United States");
            return Task.FromResult<IReadOnlyList<RawSeries>>(new[] { series });
        }
    }
}
