using EconData.Core.Configuration;
using EconData.Core.Data;
using EconData.Core.Entities;
using EconData.Core.Transform;
using Microsoft.Data.Sqlite;
using Serilog;

namespace EconData.Tests;

/// <summary>Proves the documented backup/restore path (S2-US3) round-trips a valid database.</summary>
public class BackupRestoreTests : IDisposable
{
    private static readonly string[] Suffixes = { "", "-wal", "-shm" };

    private readonly string _sourcePath;
    private readonly string _restorePath;
    private readonly ILogger _logger = new LoggerConfiguration().CreateLogger();

    public BackupRestoreTests()
    {
        _sourcePath = Path.Combine(Path.GetTempPath(), "econdata-bk-src-" + Guid.NewGuid().ToString("N") + ".db");
        _restorePath = Path.Combine(Path.GetTempPath(), "econdata-bk-dst-" + Guid.NewGuid().ToString("N") + ".db");
    }

    public void Dispose()
    {
        (_logger as IDisposable)?.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var suffix in Suffixes)
        {
            foreach (var path in new[] { _sourcePath, _restorePath })
            {
                var file = path + suffix;
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void VacuumIntoSnapshotRestoresAsValidDatabaseWithData()
    {
        var factory = new SqliteConnectionFactory(_sourcePath);
        var loader = new DatabaseLoader(factory, Path.Combine(TestPaths.RepoRoot, "sql", "sqlite", "schema.sql"), _logger);
        Assert.True(loader.VerifyOrInitSchema(), "schema init failed");

        var seed = EntitySeed.Load(TestPaths.ConfigFile("entities.json"));
        loader.SeedEntities(seed.Entities, seed.Aliases);
        var catalog = MetricsCatalog.Load(TestPaths.ConfigFile("metrics.json"));
        var metrics = DataTransformer.CreateMetricsRecords(catalog);
        loader.UpsertMetrics(metrics);
        var (beforeSources, _) = loader.GetMetricAndSourceIds();
        loader.UpsertSources(metrics, beforeSources);
        var (metricIds, sourceIds) = loader.GetMetricAndSourceIds();

        var observation = new ObservationRecord(
            "USA", "United States", new DateOnly(2024, 1, 1), new DateOnly(2024, 12, 31),
            300.0, 300.0, "Current US$", "2024", DateTimeOffset.UtcNow);
        loader.UpsertObservations("gdp_usd", new[] { observation }, metricIds["gdp_usd"], sourceIds["gdp_usd"]);

        // Exactly what `backup --to <path>` does: read-only source, VACUUM INTO destination.
        var sourceConnection = new SqliteConnectionStringBuilder
        {
            DataSource = _sourcePath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();
        using (var connection = new SqliteConnection(sourceConnection))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "VACUUM INTO @dest;";
            command.Parameters.AddWithValue("@dest", _restorePath);
            command.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        Assert.True(File.Exists(_restorePath), "snapshot was not written");

        // Restored copy passes the same checks operators run.
        var restored = new SqliteConnectionFactory(_restorePath);
        var initializer = new SchemaInitializer(restored, _logger);
        Assert.True(initializer.Verify(out var message), message);

        // Sample analytics query returns the seeded row.
        using var check = restored.OpenReadOnly();
        using var sample = check.CreateCommand();
        sample.CommandText = "SELECT value FROM v_latest_metrics_snapshot WHERE country_code='USA' AND metric_code='gdp_usd';";
        Assert.Equal(300.0, Convert.ToDouble(sample.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
    }
}
