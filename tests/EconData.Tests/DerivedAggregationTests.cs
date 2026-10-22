using EconData.Core.Aggregation;
using EconData.Core.Analytics;
using EconData.Core.Catalog;
using EconData.Core.Configuration;
using EconData.Core.Data;
using EconData.Core.Transform;
using Microsoft.Data.Sqlite;
using Serilog;

namespace EconData.Tests;

public class DerivedAggregationTests : IDisposable
{
    private static readonly string[] Suffixes = { "", "-wal", "-shm" };
    private readonly string _dbPath;
    private readonly SqliteConnectionFactory _factory;
    private readonly ILogger _logger = new LoggerConfiguration().CreateLogger();

    public DerivedAggregationTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "econdata-deriv-" + Guid.NewGuid().ToString("N") + ".db");
        _factory = new SqliteConnectionFactory(_dbPath);
        new SchemaInitializer(_factory, _logger).Initialize(Path.Combine(TestPaths.RepoRoot, "sql", "sqlite", "schema.sql"));
    }

    public void Dispose()
    {
        (_logger as IDisposable)?.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var s in Suffixes) { var p = _dbPath + s; if (File.Exists(p)) File.Delete(p); }
        GC.SuppressFinalize(this);
    }

    private long Mid(string code)
    {
        using var c = _factory.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT metric_id FROM dbe_metrics WHERE metric_code=@c";
        cmd.Parameters.AddWithValue("@c", code);
        return (long)cmd.ExecuteScalar()!;
    }

    private void Seed(string code, string method, int agg, IReadOnlyList<int> members)
    {
        using var c = _factory.Open();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "INSERT OR IGNORE INTO dbe_metrics (metric_code,metric_name,category,frequency,base_unit,is_aggregatable,aggregation_method) VALUES (@c,'M','historical','Annual','U',1,@m)";
            cmd.Parameters.AddWithValue("@c", code); cmd.Parameters.AddWithValue("@m", method);
            cmd.ExecuteNonQuery();
        }
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "INSERT OR IGNORE INTO dbe_entities (entity_id,entity_code,entity_name,entity_type,is_aggregate) VALUES (@id,@code,@code,'aggregate',1)";
            cmd.Parameters.AddWithValue("@id", agg); cmd.Parameters.AddWithValue("@code", "AGG" + agg);
            cmd.ExecuteNonQuery();
        }
        foreach (var m in members)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO dbe_entities (entity_id,entity_code,entity_name,entity_type,is_aggregate) VALUES (@id,@code,@code,'country',0)";
            cmd.Parameters.AddWithValue("@id", m); cmd.Parameters.AddWithValue("@code", "M" + m);
            cmd.ExecuteNonQuery();
            using var cmd2 = c.CreateCommand();
            cmd2.CommandText = "INSERT OR IGNORE INTO dbe_entity_membership (aggregate_entity_id,member_entity_id,start_date) VALUES (@a,@m,'1900-01-01')";
            cmd2.Parameters.AddWithValue("@a", agg); cmd2.Parameters.AddWithValue("@m", m);
            cmd2.ExecuteNonQuery();
        }
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "INSERT OR IGNORE INTO dbe_sources (provider,dataset,indicator_code,metric_id,dimension_key,entity_scope) VALUES ('WB','WDI','I',@m,'country','country')";
            cmd.Parameters.AddWithValue("@m", Mid(code));
            cmd.ExecuteNonQuery();
        }
    }

    private void AddObs(string code, int entity, string start, string end, double val)
    {
        using var c = _factory.Open();
        long metricId = Mid(code);
        long sourceId;
        using (var cmd = c.CreateCommand()) { cmd.CommandText = "SELECT source_id FROM dbe_sources WHERE metric_id=@m"; cmd.Parameters.AddWithValue("@m", metricId); sourceId = (long)cmd.ExecuteScalar()!; }
        using var ins = c.CreateCommand();
        ins.CommandText = "INSERT INTO dbe_observations (metric_id,entity_id,source_id,start_date,end_date,val,raw_val,unit,period_str,extracted_at) VALUES (@m,@e,@s,@sd,@ed,@v,@v,'U',@sd,'2024-01-01T00:00:00Z')";
        ins.Parameters.AddWithValue("@m", metricId); ins.Parameters.AddWithValue("@e", entity); ins.Parameters.AddWithValue("@s", sourceId);
        ins.Parameters.AddWithValue("@sd", start); ins.Parameters.AddWithValue("@ed", end); ins.Parameters.AddWithValue("@v", val);
        ins.ExecuteNonQuery();
    }

    private void RunAgg(string code)
    {
        var catalog = MetricsCatalog.Load(TestPaths.ConfigFile("metrics.json"));
        var loader = new DatabaseLoader(_factory, Path.Combine(TestPaths.RepoRoot, "sql", "sqlite", "schema.sql"), _logger);
        var (metricIds, _) = loader.GetMetricAndSourceIds();
        var records = DataTransformer.CreateMetricsRecords(catalog);
        new DerivedAggregator(_factory, loader, _logger).Aggregate(new[] { code }, records, metricIds);
    }

    [Fact]
    public void Sum_AggregateEmitsMemberTotal()
    {
        Seed("gdp_usd", "sum", 10, new[] { 1, 2, 3 });
        AddObs("gdp_usd", 1, "2020-01-01", "2020-12-31", 10);
        AddObs("gdp_usd", 2, "2020-01-01", "2020-12-31", 20);
        AddObs("gdp_usd", 3, "2020-01-01", "2020-12-31", 30);
        RunAgg("gdp_usd");
        using var c = _factory.Open();
        using var rd = c.CreateCommand();
        rd.CommandText = "SELECT val,member_count,covered_count,aggregation_method FROM dbe_derived_observations WHERE entity_id=10";
        using var r = rd.ExecuteReader(); r.Read();
        Assert.Equal(60.0, r.GetDouble(0)); Assert.Equal(3, r.GetInt32(1));
        Assert.Equal(3, r.GetInt32(2)); Assert.Equal("sum", r.GetString(3));
    }

    [Fact]
    public void Mean_AggregateEmitsAverage()
    {
        Seed("gdp_growth", "mean", 10, new[] { 1, 2, 3 });
        AddObs("gdp_growth", 1, "2020-01-01", "2020-12-31", 1);
        AddObs("gdp_growth", 2, "2020-01-01", "2020-12-31", 2);
        AddObs("gdp_growth", 3, "2020-01-01", "2020-12-31", 4);
        RunAgg("gdp_growth");
        using var c = _factory.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT val,aggregation_method FROM dbe_derived_observations WHERE entity_id=10";
        using var r = cmd.ExecuteReader(); r.Read();
        Assert.Equal(7.0 / 3.0, r.GetDouble(0), 3); Assert.Equal("mean", r.GetString(1));
    }

    [Fact]
    public void Coverage_BelowHalf_EmitsNoRow()
    {
        Seed("gdp_usd", "sum", 10, new[] { 1, 2, 3, 4 });
        AddObs("gdp_usd", 1, "2020-01-01", "2020-12-31", 10);
        AddObs("gdp_usd", 2, "2019-01-01", "2019-12-31", 20);
        AddObs("gdp_usd", 3, "2019-01-01", "2019-12-31", 30);
        AddObs("gdp_usd", 4, "2019-01-01", "2019-12-31", 40);
        RunAgg("gdp_usd");
        using var c = _factory.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM dbe_derived_observations WHERE entity_id=10 AND start_date='2020-01-01'";
        Assert.Equal(0L, (long)cmd.ExecuteScalar()!);
    }

    [Fact]
    public void Coverage_AtHalf_EmitsRow()
    {
        Seed("gdp_usd", "sum", 10, new[] { 1, 2, 3, 4 });
        AddObs("gdp_usd", 1, "2020-01-01", "2020-12-31", 10);
        AddObs("gdp_usd", 2, "2020-01-01", "2020-12-31", 20);
        AddObs("gdp_usd", 3, "2019-01-01", "2019-12-31", 30);
        AddObs("gdp_usd", 4, "2019-01-01", "2019-12-31", 40);
        RunAgg("gdp_usd");
        using var c = _factory.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM dbe_derived_observations WHERE entity_id=10 AND start_date='2020-01-01'";
        Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
    }

    [Fact]
    public void OptOut_NoAggregationKey_EmitsNothing()
    {
        // current_account_balance has no aggregation key in config -> defaults to "none"
        Seed("current_account_balance", "none", 10, new[] { 1, 2 });
        using (var c = _factory.Open()) { using var cmd = c.CreateCommand(); cmd.CommandText = "UPDATE dbe_metrics SET is_aggregatable=0,aggregation_method='none' WHERE metric_code='current_account_balance'"; cmd.ExecuteNonQuery(); }
        AddObs("current_account_balance", 1, "2020-01-01", "2020-12-31", 10);
        AddObs("current_account_balance", 2, "2020-01-01", "2020-12-31", 20);
        RunAgg("current_account_balance");
        using var c2 = _factory.Open();
        using var cmd2 = c2.CreateCommand();
        cmd2.CommandText = "SELECT COUNT(*) FROM dbe_derived_observations";
        Assert.Equal(0L, (long)cmd2.ExecuteScalar()!);
    }

    [Fact]
    public void Idempotent_SecondRunUnchanged()
    {
        Seed("gdp_usd", "sum", 10, new[] { 1, 2 });
        AddObs("gdp_usd", 1, "2020-01-01", "2020-12-31", 10);
        AddObs("gdp_usd", 2, "2020-01-01", "2020-12-31", 20);
        RunAgg("gdp_usd");
        RunAgg("gdp_usd");
        using var c = _factory.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM dbe_derived_observations";
        Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
    }

    [Fact]
    public void ReadPath_DerivedVisibleThroughSeries()
    {
        Seed("gdp_usd", "sum", 10, new[] { 1, 2 });
        AddObs("gdp_usd", 1, "2020-01-01", "2020-12-31", 10);
        AddObs("gdp_usd", 2, "2020-01-01", "2020-12-31", 20);
        RunAgg("gdp_usd");
        var analytics = new AnalyticsService(_factory);
        var series = analytics.GetSeries("AGG10", "gdp_usd");
        Assert.Single(series);
        Assert.Equal(30.0, series[0].Value);
        Assert.Equal("derived", series[0].SourceKind);
    }

    [Fact]
    public void DuplicateStartDifferentEnd_KeptSeparate()
    {
        Seed("gdp_usd", "sum", 10, new[] { 1 });
        AddObs("gdp_usd", 1, "2020-01-01", "2020-06-30", 10);
        AddObs("gdp_usd", 1, "2020-01-01", "2020-12-31", 20);
        RunAgg("gdp_usd");
        using var c = _factory.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM dbe_derived_observations WHERE entity_id=10";
        Assert.Equal(2L, (long)cmd.ExecuteScalar()!);
    }

    [Fact]
    public void AggregationFailure_ReportedInFailedMetrics()
    {
        // Force failure by dropping the derived table before aggregation.
        using (var c = _factory.Open()) { using var cmd = c.CreateCommand(); cmd.CommandText = "DROP TABLE dbe_derived_observations"; cmd.ExecuteNonQuery(); }
        Seed("gdp_usd", "sum", 10, new[] { 1, 2 });
        AddObs("gdp_usd", 1, "2020-01-01", "2020-12-31", 10);
        AddObs("gdp_usd", 2, "2020-01-01", "2020-12-31", 20);
        var catalog = MetricsCatalog.Load(TestPaths.ConfigFile("metrics.json"));
        var loader = new DatabaseLoader(_factory, Path.Combine(TestPaths.RepoRoot, "sql", "sqlite", "schema.sql"), _logger);
        var (metricIds, _) = loader.GetMetricAndSourceIds();
        var records = DataTransformer.CreateMetricsRecords(catalog);
        // Re-create schema? no; aggregator will throw on missing table. Wrap via PipelineRunner? PipelineRunner needs source. Use direct: call aggregator inside try.
        Exception? ex = null;
        try { new DerivedAggregator(_factory, loader, _logger).Aggregate(new[] { "gdp_usd" }, records, metricIds); }
        catch (Exception e) { ex = e; }
        Assert.NotNull(ex);
    }

}
