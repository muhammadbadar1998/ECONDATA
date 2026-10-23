using DuckDB.NET.Data;
using EconData.Core.Analytics;
using EconData.Core.Configuration;
using EconData.Core.Data;
using EconData.Core.Entities;
using EconData.Core.Extraction;
using EconData.Core.Lake;
using Microsoft.Data.Sqlite;
using Parquet;
using Serilog;

namespace EconData.Tests;

public class LakeTierTests : IDisposable
{
    private enum Tier { Bronze, Silver, Gold }

    private readonly string _dir;
    private readonly LakePaths _paths;
    private readonly ILogger _logger = new LoggerConfiguration().CreateLogger();
    private readonly MetricsCatalog _catalog;

    public LakeTierTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "econdata-lake-" + Guid.NewGuid().ToString("N"));
        _paths = new LakePaths(_dir);
        _paths.EnsureDirectories();
        _catalog = MetricsCatalog.Load(TestPaths.ConfigFile("metrics.json"));
    }

    public void Dispose()
    {
        (_logger as IDisposable)?.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private static string SchemaPath => Path.Combine(TestPaths.RepoRoot, "sql", "duckdb", "schema.sql");

    [Fact]
    public async Task Bronze_RoundTripsEveryRawSeriesFieldIncludingNulls()
    {
        var bronze = new BronzeTier(_paths, _logger);
        var series = new[]
        {
            new RawSeries(
                new Dictionary<string, string> { ["country"] = "USA", ["weo-subject"] = "NGDP" },
                new[] { "2022", "2023", null },
                new[] { "100", null, "not-a-number" },
                "United States"),
        };

        var path = await bronze.AppendAsync("gdp_usd", series, _catalog, "run1");

        Assert.True(File.Exists(path));
        var back = await bronze.ReadAsync("gdp_usd");
        var item = Assert.Single(back);
        Assert.Equal(series[0].Dimensions, item.Dimensions);
        Assert.Equal(series[0].SeriesName, item.SeriesName);
        Assert.Equal(series[0].Periods, item.Periods);
        Assert.Equal(series[0].Values, item.Values);
        Assert.Null(item.Periods[2]);
        Assert.Null(item.Values[1]);
    }

    [Fact]
    public async Task Bronze_SecondRunAppendsWithoutRewritingFirst()
    {
        var bronze = new BronzeTier(_paths, _logger);
        var first = new[] { RawSeries("USA", new[] { "2020" }, new[] { "10" }) };
        var second = new[] { RawSeries("USA", new[] { "2020", "2021" }, new[] { "10", "20" }) };

        var path1 = await bronze.AppendAsync("gdp_usd", first, _catalog, "run1");
        var bytes1 = await File.ReadAllBytesAsync(path1);
        var path2 = await bronze.AppendAsync("gdp_usd", second, _catalog, "run2");

        Assert.NotEqual(path1, path2);
        Assert.Equal(bytes1, await File.ReadAllBytesAsync(path1));
        Assert.Equal(2, bronze.BronzeFiles("gdp_usd").Count);

        var all = await bronze.ReadAsync("gdp_usd");
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task Bronze_RowCountSumsRowGroupsAcrossFiles()
    {
        var bronze = new BronzeTier(_paths, _logger);
        await bronze.AppendAsync("gdp_usd", new[] { RawSeries("USA", new[] { "2020" }, new[] { "10" }) }, _catalog, "run1");
        await bronze.AppendAsync("gdp_usd", new[] { RawSeries("FRA", new[] { "2020" }, new[] { "20" }) }, _catalog, "run2");
        await bronze.AppendAsync("gdp_usd", new[] { RawSeries("DEU", new[] { "2020" }, new[] { "30" }) }, _catalog, "run3");

        var counts = await bronze.RowCountsAsync();
        Assert.Equal(3, counts["gdp_usd"]);

        var total = await bronze.RowCountAsync("gdp_usd");
        Assert.Equal(3, total);
    }

    [Fact]
    public async Task LakeStatus_ReportsNonZeroBronzeRows()
    {
        var service = new LakeStatusService(_paths, new GoldTier(_paths.GoldPath, SchemaPath, _logger), _logger);
        var bronze = new BronzeTier(_paths, _logger);
        await bronze.AppendAsync("gdp_usd", new[] { RawSeries("USA", new[] { "2020" }, new[] { "10" }) }, _catalog, "run1");
        await bronze.AppendAsync("gdp_usd", new[] { RawSeries("FRA", new[] { "2020" }, new[] { "20" }) }, _catalog, "run2");
        await bronze.AppendAsync("gdp_usd", new[] { RawSeries("DEU", new[] { "2020" }, new[] { "30" }) }, _catalog, "run3");

        var status = await service.GetAsync();
        Assert.Equal(3, status.Bronze.Rows);
        Assert.True(status.Bronze.Present);
    }

    [Fact]
    public async Task Silver_OverwritesPartition()
    {
        var silver = new SilverTier(_paths, _logger);
        await silver.WriteAsync("gdp_usd", new[] { Obs("USA", 2020, 10) });
        Assert.Single(silver.Partitions());

        await silver.WriteAsync("gdp_usd", new[] { Obs("USA", 2020, 10), Obs("USA", 2021, 20) });

        var counts = await silver.RowCountsAsync();
        Assert.Equal(2, counts["gdp_usd"]);
        Assert.Single(silver.Partitions());
    }

    [Fact]
    public async Task Silver_IsKeyStableAcrossRuns()
    {
        var silver = new SilverTier(_paths, _logger);
        var rows = new[] { Obs("USA", 2020, 10), Obs("FRA", 2020, 30) };

        await silver.WriteAsync("gdp_usd", rows);
        var first = await File.ReadAllBytesAsync(_paths.SilverFile("gdp_usd"));
        await silver.WriteAsync("gdp_usd", rows);

        var second = await File.ReadAllBytesAsync(_paths.SilverFile("gdp_usd"));
        Assert.Equal(first.Length, second.Length);
        var counts = await silver.RowCountsAsync();
        Assert.Equal(2, counts["gdp_usd"]);
    }

    [Fact]
    public async Task Silver_CountSidecarUsedAndSurvivesDeletion()
    {
        var silver = new SilverTier(_paths, _logger);
        await silver.WriteAsync("gdp_usd", new[] { Obs("USA", 2020, 10), Obs("USA", 2021, 20) });

        var parquet = _paths.SilverFile("gdp_usd");
        var sidecar = Path.ChangeExtension(parquet, ".count");
        Assert.True(File.Exists(sidecar));

        var counts = await silver.RowCountsAsync();
        Assert.Equal(2, counts["gdp_usd"]);

        // sidecar is fresh (written after parquet) so the second call skips the scan
        Assert.True(File.GetLastWriteTimeUtc(sidecar) >= File.GetLastWriteTimeUtc(parquet));
        counts = await silver.RowCountsAsync();
        Assert.Equal(2, counts["gdp_usd"]);

        // missing sidecar → falls back to a Parquet scan, still correct
        File.Delete(sidecar);
        counts = await silver.RowCountsAsync();
        Assert.Equal(2, counts["gdp_usd"]);
    }

    [Fact]
    public async Task Gold_RebuildFromSilverReflectsReducedCount()
    {
        var rebuild = new GoldTier(Path.Combine(_paths.GoldDir, "econdata.rebuild.duckdb"), SchemaPath, _logger);
        var silver = new SilverTier(_paths, _logger);
        var seed = EntitySeed.Load(TestPaths.ConfigFile("entities.json"));

        await silver.WriteAsync("gdp_usd", new[] { Obs("USA", 2020, 10), Obs("FRA", 2020, 20) });
        var counts = await rebuild.RebuildFromSilverAsync(seed, _catalog, silver, new[] { "gdp_usd" });
        Assert.Equal(2, counts["gdp_usd"]);

        // Reduce silver (one row removed): rebuild from an empty file reflects the reduced count.
        File.Delete(rebuild.DatabasePath);
        await silver.WriteAsync("gdp_usd", new[] { Obs("USA", 2020, 10) });
        counts = await rebuild.RebuildFromSilverAsync(seed, _catalog, silver, new[] { "gdp_usd" });
        Assert.Equal(1, counts["gdp_usd"]);

        // Atomic swap into the live gold file.
        File.Move(rebuild.DatabasePath, _paths.GoldPath, overwrite: true);
        using var db = new DuckDBConnection($"Data Source={_paths.GoldPath}");
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM dbe_observations";
        Assert.Equal(1, Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Gold_BootstrapIsIdempotentAndCreatesAllTablesAndViews()
    {
        using var gold = new GoldFixture(_paths, _logger);

        gold.Value.EnsureSchema();
        gold.Value.EnsureSchema();

        using var db = gold.Value.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT table_name FROM information_schema.tables WHERE table_type='BASE TABLE' ORDER BY table_name";
        using var reader = cmd.ExecuteReader();
        var tables = new List<string>();
        while (reader.Read())
        {
            tables.Add(reader.GetString(0));
        }

        Assert.Contains("dbe_observations", tables);
        Assert.Contains("dbe_derived_observations", tables);
        Assert.Contains("dbe_pipeline_runs", tables);
        Assert.DoesNotContain("dbe_embeddings", tables);

        using var views = gold.Value.Open();
        using var vc = views.CreateCommand();
        vc.CommandText = "SELECT table_name FROM information_schema.views ORDER BY table_name";
        using var vr = vc.ExecuteReader();
        var names = new List<string>();
        while (vr.Read())
        {
            names.Add(vr.GetString(0));
        }

        Assert.Contains("v_observations_all", names);
        Assert.Contains("v_latest_metrics_snapshot", names);
        Assert.Contains("v_dated_execution_snapshot", names);
    }

    [Fact]
    public void Gold_ObservationUpsertIsIdempotent()
    {
        using var gold = new GoldFixture(_paths, _logger);
        var g = gold.Value;
        g.EnsureSchema();
        Seed(g);

        var (ids, sources) = g.GetMetricAndSourceIds();
        var rows = new[] { Obs("USA", 2020, 10), Obs("USA", 2021, 20) };

        Assert.Equal(2, g.UpsertObservations("gdp_usd", rows, ids["gdp_usd"], sources["gdp_usd"]));
        Assert.Equal(2, g.UpsertObservations("gdp_usd", rows, ids["gdp_usd"], sources["gdp_usd"]));

        using var db = g.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM dbe_observations";
        Assert.Equal(2, Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Gold_AggregationHonoursCoverageRuleAndProvenance()
    {
        using var gold = new GoldFixture(_paths, _logger);
        var g = gold.Value;
        g.EnsureSchema();
        Seed(g);

        using (var db = g.Open())
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText =
                "INSERT INTO dbe_entities (entity_code, entity_name, entity_type, is_aggregate) " +
                "VALUES ('AGG','Agg','aggregate',1),('M1','M1','country',0),('M2','M2','country',0),('M3','M3','country',0),('M4','M4','country',0)";
            cmd.ExecuteNonQuery();
            cmd.CommandText =
                "INSERT INTO dbe_entity_membership (aggregate_entity_id, member_entity_id, start_date) " +
                "SELECT a.entity_id, m.entity_id, '1900-01-01' FROM dbe_entities a, dbe_entities m " +
                "WHERE a.entity_code='AGG' AND m.entity_code IN ('M1','M2','M3','M4')";
            cmd.ExecuteNonQuery();
        }

        var (ids, sources) = g.GetMetricAndSourceIds();
        var rows = new[]
        {
            Obs("M1", 2020, 10), Obs("M2", 2020, 20), Obs("M3", 2020, 30), Obs("M4", 2020, 40),
            Obs("M1", 2021, 5), Obs("M2", 2021, 7),
        };
        g.UpsertObservations("gdp_usd", rows, ids["gdp_usd"], sources["gdp_usd"]);

        var written = g.Aggregate("gdp_usd", "sum", "USD", ids);
        Assert.Equal(2, written);

        using var db2 = g.Open();
        using var cmd2 = db2.CreateCommand();
        cmd2.CommandText =
            "SELECT d.start_date, d.val, d.aggregation_method, d.member_count, d.covered_count " +
            "FROM dbe_derived_observations d ORDER BY d.start_date";
        using var r = cmd2.ExecuteReader();
        Assert.True(r.Read());
        Assert.Equal("2020-01-01", r.GetString(0));
        Assert.Equal(100.0, r.GetDouble(1));
        Assert.Equal("sum", r.GetString(2));
        Assert.Equal(4, r.GetInt32(3));
        Assert.Equal(4, r.GetInt32(4));
        Assert.True(r.Read());
        Assert.Equal("2021-01-01", r.GetString(0));
        Assert.Equal(12.0, r.GetDouble(1));
        Assert.Equal(2, r.GetInt32(4));
    }

    [Fact]
    public void Gold_DerivedRowsSurfaceThroughUnifiedView()
    {
        using var gold = new GoldFixture(_paths, _logger);
        var g = gold.Value;
        g.EnsureSchema();
        Seed(g);

        using (var db = g.Open())
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText =
                "INSERT INTO dbe_entities (entity_code, entity_name, entity_type, is_aggregate) " +
                "VALUES ('AGG','Agg','aggregate',1),('M1','M1','country',0),('M2','M2','country',0)";
            cmd.ExecuteNonQuery();
            cmd.CommandText =
                "INSERT INTO dbe_entity_membership (aggregate_entity_id, member_entity_id, start_date) " +
                "SELECT a.entity_id, m.entity_id, '1900-01-01' FROM dbe_entities a, dbe_entities m " +
                "WHERE a.entity_code='AGG' AND m.entity_code IN ('M1','M2')";
            cmd.ExecuteNonQuery();
        }

        var (ids, sources) = g.GetMetricAndSourceIds();
        g.UpsertObservations("gdp_usd", new[] { Obs("M1", 2020, 10), Obs("M2", 2020, 20) }, ids["gdp_usd"], sources["gdp_usd"]);
        g.Aggregate("gdp_usd", "sum", "USD", ids);

        using var db2 = g.Open();
        using var cmd2 = db2.CreateCommand();
        cmd2.CommandText =
            "SELECT v.val, v.source_kind FROM v_observations_all v " +
            "JOIN dbe_entities e ON e.entity_id = v.entity_id WHERE e.entity_code = 'AGG'";
        using var r = cmd2.ExecuteReader();
        Assert.True(r.Read());
        Assert.Equal(30.0, r.GetDouble(0));
        Assert.Equal("derived", r.GetString(1));
    }

    [Fact]
    public async Task BronzeAndSilverParquetHaveTheDocumentedColumns()
    {
        var bronze = new BronzeTier(_paths, _logger);
        await bronze.AppendAsync("gdp_usd", new[] { RawSeries("USA", new[] { "2020" }, new[] { "10" }) }, _catalog, "run1");

        var bronzeFile = Assert.Single(bronze.BronzeFiles("gdp_usd"));
        Assert.Equal(
            new[]
            {
                "metric_code", "provider", "dataset", "indicator_code", "series_name",
                "dimensions_json", "periods_json", "values_json", "run_id", "fetched_at",
            },
            await ColumnNames(bronzeFile));

        var silver = new SilverTier(_paths, _logger);
        await silver.WriteAsync("gdp_usd", new[] { Obs("USA", 2020, 10) });

        Assert.Equal(
            new[]
            {
                "metric_code", "entity_code", "entity_name", "start_date", "end_date",
                "val", "raw_val", "unit", "period_str", "extracted_at",
            },
            await ColumnNames(_paths.SilverFile("gdp_usd")));
    }

    private static async Task<IReadOnlyList<string>> ColumnNames(string path)
    {
        await using var stream = File.OpenRead(path);
        await using var reader = await ParquetReader.CreateAsync(stream);
        return reader.Schema.Fields.Select(field => field.Name).ToList();
    }

    private static RawSeries RawSeries(string country, string?[] periods, string?[] values)
        => new(new Dictionary<string, string> { ["country"] = country }, periods, values, country);

    private static ObservationRecord Obs(string entity, int year, double val)
        => new(
            entity,
            entity,
            new DateOnly(year, 1, 1),
            new DateOnly(year, 12, 31),
            val,
            val,
            "USD",
            year.ToString(System.Globalization.CultureInfo.InvariantCulture),
            DateTimeOffset.UtcNow);

    private void Seed(GoldTier g)
    {
        var seed = EntitySeed.Load(TestPaths.ConfigFile("entities.json"));
        var records = EconData.Core.Transform.DataTransformer.CreateMetricsRecords(_catalog);
        g.SeedEntities(seed.Entities, seed.Aliases);
        g.UpsertMetrics(records);
        var (ids, _) = g.GetMetricAndSourceIds();
        g.UpsertSources(records, ids);
    }

    private sealed class GoldFixture : IDisposable
    {
        public GoldFixture(LakePaths paths, ILogger logger)
        {
            Value = new GoldTier(paths.GoldPath, SchemaPath, logger);
            Value.EnsureSchema();
        }

        public GoldTier Value { get; }

        public void Dispose() => SqliteConnection.ClearAllPools();
    }
}
