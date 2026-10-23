using EconData.Core.Catalog;
using EconData.Core.Configuration;
using EconData.Core.Entities;

namespace EconData.Tests;

public class VectorDocumentTests
{
    private static readonly List<Dictionary<string, object?>> RecentPoints = new()
    {
        new() { ["year"] = 2024L, ["value"] = 27369400000000.0 },
        new() { ["year"] = 2023L, ["value"] = 27720700000000.0 },
    };

    [Fact]
    public void BuildMetricDocumentsIncludesNameSynonymsAndMetadata()
    {
        var documents = Vectorizer.BuildMetricDocuments(MetricsCatalog.Load(TestPaths.ConfigFile("metrics.json")));

        Assert.Equal(26, documents.Count);
        var gdp = documents.Single(document => document.DocKey == "gdp_usd");
        Assert.Equal("metric", gdp.DocType);
        Assert.Contains("Gross Domestic Product", gdp.Content);
        Assert.Contains("GDP", gdp.Content);
        Assert.Equal("Annual", gdp.Metadata["frequency"]);
        Assert.False(string.IsNullOrEmpty(gdp.DocHash));
    }

    [Fact]
    public void BuildMetricDocumentsHashIsStable()
    {
        var catalog = MetricsCatalog.Load(TestPaths.ConfigFile("metrics.json"));
        var first = Vectorizer.BuildMetricDocuments(catalog);
        var second = Vectorizer.BuildMetricDocuments(catalog);

        Assert.Equal(
            first.Select(document => document.DocHash),
            second.Select(document => document.DocHash));
    }

    [Fact]
    public void BuildEntityDocumentsOnePerEntity()
    {
        var seed = EntitySeed.Load(TestPaths.ConfigFile("entities.json"));
        var documents = Vectorizer.BuildEntityDocuments(seed.Entities);

        Assert.Equal(seed.Entities.Count, documents.Count);
        Assert.All(documents, document => Assert.Equal("entity", document.DocType));
        var world = documents.Single(document => document.DocKey == "WLD");
        Assert.Contains("World", world.Content);
    }

    [Fact]
    public void BuildEntityMetricDocumentsContainsValueSummary()
    {
        var document = Vectorizer.BuildEntityMetricDocuments(new[] { EnrichedRow() }).Single();

        Assert.Equal("USA|gdp_usd", document.DocKey);
        Assert.Equal("entity_metric", document.DocType);
        Assert.Contains("Latest: 27,369,400,000,000 (2024)", document.Content);
        Assert.Contains("Min: 543,300,000,000 (1960)", document.Content);
        Assert.Contains("Max: 27,369,400,000,000 (2024)", document.Content);
        Assert.Contains("Recent: 2024=27,369,400,000,000; 2023=27,720,700,000,000", document.Content);
        Assert.Contains("Observations: 65, range 1960–2024", document.Content);
        Assert.Equal(27369400000000.0, document.Metadata["latest_val"]);
        Assert.Equal(2, ((List<Dictionary<string, object?>>)document.Metadata["recent_points"]!).Count);
    }

    [Fact]
    public void BuildEntityMetricDocumentsHashStableAndChangesOnValue()
    {
        var stableA = Vectorizer.BuildEntityMetricDocuments(new[] { EnrichedRow() }).Single();
        var stableB = Vectorizer.BuildEntityMetricDocuments(new[] { EnrichedRow() }).Single();
        Assert.Equal(stableA.DocHash, stableB.DocHash);

        var mutated = EnrichedRow();
        mutated["latest_val"] = 999.0;
        var changed = Vectorizer.BuildEntityMetricDocuments(new[] { mutated }).Single();
        Assert.NotEqual(stableA.DocHash, changed.DocHash);
    }

    [Fact]
    public void FormatValueFollowsDeterministicRules()
    {
        Assert.Equal("n/a", DocumentFormatting.FormatValue(null));
        Assert.Equal("27,369,400,000,000", DocumentFormatting.FormatValue(27369400000000L));
        Assert.Equal("999", DocumentFormatting.FormatValue(999));
        Assert.Equal("-1,234", DocumentFormatting.FormatValue(-1234.0));
        Assert.Equal("2.50", DocumentFormatting.FormatValue(2.5));
        Assert.Equal("3.50%", DocumentFormatting.FormatValue(3.5, "%"));
        Assert.Equal("1,234.57%", DocumentFormatting.FormatValue(1234.5678, "%"));
        Assert.Equal("n/a", DocumentFormatting.FormatValue(double.NaN));
        Assert.Equal("not a number", DocumentFormatting.FormatValue("not a number"));
    }

    [Fact]
    public void ParseRecentPointsHandlesTextListAndBadInput()
    {
        Assert.Empty(DocumentFormatting.ParseRecentPoints(null));
        Assert.Empty(DocumentFormatting.ParseRecentPoints(string.Empty));
        Assert.Empty(DocumentFormatting.ParseRecentPoints("not json"));

        var points = DocumentFormatting.ParseRecentPoints("""[{"year":2024,"value":1.5}]""");
        Assert.Single(points);
        Assert.Equal(2024L, points[0]["year"]);
        Assert.Equal(1.5, points[0]["value"]);
    }

    private static Dictionary<string, object?> EnrichedRow() => new()
    {
        ["entity_code"] = "USA",
        ["entity_name"] = "United States",
        ["metric_code"] = "gdp_usd",
        ["metric_name"] = "Gross Domestic Product (Current USD)",
        ["frequency"] = "Annual",
        ["base_unit"] = "Current US$",
        ["obs_count"] = 65L,
        ["min_year"] = 1960L,
        ["max_year"] = 2024L,
        ["latest_val"] = 27369400000000.0,
        ["latest_year"] = 2024L,
        ["min_val"] = 543300000000.0,
        ["min_val_year"] = 1960L,
        ["max_val"] = 27369400000000.0,
        ["max_val_year"] = 2024L,
        ["recent_points"] = RecentPoints,
    };
}
