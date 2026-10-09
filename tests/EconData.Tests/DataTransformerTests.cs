using EconData.Core.Configuration;
using EconData.Core.Entities;
using EconData.Core.Extraction;
using EconData.Core.Transform;

namespace EconData.Tests;

public class DataTransformerTests
{
    private static EntityResolver CreateResolver()
        => new(EntitySeed.Load(TestPaths.ConfigFile("entities.json")));

    [Fact]
    public void ResolvesEntitiesAndDedupesByEntityAndStartDate()
    {
        var catalog = MetricsCatalog.Load(TestPaths.ConfigFile("metrics.json"));
        var raw = new List<RawSeries>
        {
            new(
                new Dictionary<string, string> { ["country"] = "USA" },
                new[] { "2023", "2024", "2024" },
                new[] { "21.0", "22.0", "22.0" },
                "United States"),
        };

        var records = DataTransformer.TransformSeriesList("gdp_usd", raw, catalog, CreateResolver());

        Assert.Equal(2, records.Count);
        Assert.All(records, record => Assert.Equal("USA", record.EntityCode));
        Assert.Contains(records, record => record.StartDate == new DateOnly(2024, 1, 1));
    }

    [Fact]
    public void SkipsNonNumericAndUnparseableRows()
    {
        var catalog = MetricsCatalog.Load(TestPaths.ConfigFile("metrics.json"));
        var raw = new List<RawSeries>
        {
            new(
                new Dictionary<string, string> { ["country"] = "USA" },
                new[] { "2023", "bad", "2024" },
                new[] { "1.0", "NA", null },
                null),
        };

        var records = DataTransformer.TransformSeriesList("unknown_metric", raw, catalog, CreateResolver());

        Assert.Single(records);
        Assert.Equal("2023", records[0].PeriodStr);
    }
}
