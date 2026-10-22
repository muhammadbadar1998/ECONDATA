using System.Globalization;
using EconData.Core.Configuration;
using EconData.Core.Data;
using EconData.Core.Entities;
using EconData.Core.Transform;
using Microsoft.Data.Sqlite;
using Serilog;

namespace EconData.Tests;

public class DatabaseLoaderIntegrationTests : IDisposable
{
    private static readonly string[] DatabaseFileSuffixes = { "", "-wal", "-shm" };

    private readonly string _dbPath;
    private readonly SqliteConnectionFactory _factory;
    private readonly DatabaseLoader _loader;
    private readonly ILogger _logger = new LoggerConfiguration().CreateLogger();

    public DatabaseLoaderIntegrationTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "econdata-test-" + Guid.NewGuid().ToString("N") + ".db");
        _factory = new SqliteConnectionFactory(_dbPath);
        _loader = new DatabaseLoader(
            _factory,
            Path.Combine(TestPaths.RepoRoot, "sql", "sqlite", "schema.sql"),
            _logger);
    }

    public void Dispose()
    {
        (_logger as IDisposable)?.Dispose();
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
    }

    [Fact]
    public void FullSchemaSeedAndUpsertIsIdempotent()
    {
        Assert.True(_loader.VerifyOrInitSchema(), "schema init failed");
        Assert.True(_loader.CheckSchema(out var checkMessage), checkMessage);

        var seed = EntitySeed.Load(TestPaths.ConfigFile("entities.json"));
        _loader.SeedEntities(seed.Entities, seed.Aliases);
        _loader.SeedMemberships(seed.Memberships);

        var catalog = MetricsCatalog.Load(TestPaths.ConfigFile("metrics.json"));
        var metrics = DataTransformer.CreateMetricsRecords(catalog);
        _loader.UpsertMetrics(metrics);

        var (metricIdsBeforeSources, _) = _loader.GetMetricAndSourceIds();
        _loader.UpsertSources(metrics, metricIdsBeforeSources);
        var (metricIds, sourceIds) = _loader.GetMetricAndSourceIds();

        Assert.Equal(catalog.Codes.Count, metricIds.Count);
        Assert.Equal(catalog.Codes.Count, sourceIds.Count);

        var observations = new List<ObservationRecord>
        {
            Observation("USA", "United States", 2022, 25000000.0),
            Observation("USA", "United States", 2023, 26000000.0),
            Observation("USA", "United States", 2024, 27000000.0),
            Observation("DEU", "Germany", 2024, 4200000.0),
        };

        var gdpMetricId = metricIds["gdp_usd"];
        var gdpSourceId = sourceIds["gdp_usd"];
        Assert.Equal(4, _loader.UpsertObservations("gdp_usd", observations, gdpMetricId, gdpSourceId));
        // Idempotency: a second identical load must not create duplicate rows.
        Assert.Equal(4, _loader.UpsertObservations("gdp_usd", observations, gdpMetricId, gdpSourceId));

        using (var connection = _factory.Open())
        {
            Assert.Equal(4, ScalarLong(connection, "SELECT count(*) FROM dbe_observations"));
            Assert.Equal(2, ScalarLong(connection, "SELECT count(*) FROM dbe_entities WHERE entity_code IN ('USA','DEU')"));
            Assert.True(ScalarLong(connection, "SELECT count(*) FROM dbe_entity_membership") > 0);

            using var view = connection.CreateCommand();
            view.CommandText = "SELECT value, period FROM v_latest_metrics_snapshot WHERE country_code = 'USA' AND metric_code = 'gdp_usd'";
            using var reader = view.ExecuteReader();
            Assert.True(reader.Read(), "USA gdp_usd missing from v_latest_metrics_snapshot");
            Assert.Equal(27000000.0, reader.GetDouble(0));
            Assert.Equal("2024-01-01", reader.GetString(1));
        }

        var providers = new[] { "WB" };
        var sources = new[] { gdpSourceId };
        _loader.LogPipelineRun("econdata_manual", "test", 1, 4, 0, providers, sources, "SUCCESS", 1.5);

        using (var connection = _factory.Open())
        {
            Assert.Equal(1, ScalarLong(connection, "SELECT count(*) FROM dbe_pipeline_runs"));
        }
    }

    [Fact]
    public void ChangedValueUpdatesRowInPlaceWithoutDuplicating()
    {
        Assert.True(_loader.VerifyOrInitSchema(), "schema init failed");
        var seed = EntitySeed.Load(TestPaths.ConfigFile("entities.json"));
        _loader.SeedEntities(seed.Entities, seed.Aliases);
        var catalog = MetricsCatalog.Load(TestPaths.ConfigFile("metrics.json"));
        var metrics = DataTransformer.CreateMetricsRecords(catalog);
        _loader.UpsertMetrics(metrics);
        var (metricIdsBeforeSources, _) = _loader.GetMetricAndSourceIds();
        _loader.UpsertSources(metrics, metricIdsBeforeSources);
        var (metricIds, sourceIds) = _loader.GetMetricAndSourceIds();

        var original = Observation("USA", "United States", 2024, 100.0);
        _loader.UpsertObservations("gdp_usd", new[] { original }, metricIds["gdp_usd"], sourceIds["gdp_usd"]);
        var firstExtracted = ReadText("SELECT extracted_at FROM dbe_observations WHERE metric_id=@m AND start_date='2024-01-01'", metricIds["gdp_usd"]);

        var revised = original with
        {
            Val = 250.0,
            RawVal = 250.0,
            ExtractedAt = original.ExtractedAt.AddMinutes(5),
        };
        Assert.Equal(1, _loader.UpsertObservations("gdp_usd", new[] { revised }, metricIds["gdp_usd"], sourceIds["gdp_usd"]));

        using var connection = _factory.Open();
        Assert.Equal(1, ScalarLong(connection, "SELECT count(*) FROM dbe_observations WHERE metric_id=" + metricIds["gdp_usd"]));
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT val, extracted_at FROM dbe_observations WHERE metric_id=@m AND start_date='2024-01-01'";
        cmd.Parameters.AddWithValue("@m", metricIds["gdp_usd"]);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(250.0, reader.GetDouble(0));
        Assert.NotEqual(firstExtracted, reader.GetString(1));
    }

    private string ReadText(string sql, int metricId)
    {
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@m", metricId);
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static ObservationRecord Observation(string code, string name, int year, double value)
        => new(
            code,
            name,
            new DateOnly(year, 1, 1),
            new DateOnly(year, 12, 31),
            value,
            value,
            "Current US$",
            year.ToString(CultureInfo.InvariantCulture),
            DateTimeOffset.UtcNow);

    private static long ScalarLong(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
}
