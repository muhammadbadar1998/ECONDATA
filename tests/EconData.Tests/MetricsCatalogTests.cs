using EconData.Core.Configuration;

namespace EconData.Tests;

public class MetricsCatalogTests
{
    [Fact]
    public void LoadsAllMetricsFromRepoConfig()
    {
        var catalog = MetricsCatalog.Load(TestPaths.ConfigFile("metrics.json"));

        Assert.Equal(9, catalog.Codes.Count);
        Assert.NotNull(catalog.Get("gdp_usd"));
    }

    [Fact]
    public void MetricDetailsAndSynonymsAreLoaded()
    {
        var metric = MetricsCatalog.Load(TestPaths.ConfigFile("metrics.json")).Get("gdp_usd");

        Assert.NotNull(metric);
        Assert.Equal("NY.GDP.MKTP.CD", metric!.IndicatorCode);
        Assert.Contains("GDP", metric.Synonyms);
        Assert.Equal("Current US$", metric.EffectiveBaseUnit);
    }

    [Fact]
    public void PipelineSettingsAreLoaded()
    {
        var catalog = MetricsCatalog.Load(TestPaths.ConfigFile("metrics.json"));

        Assert.Equal(1960, catalog.Settings.DefaultStartYear);
        Assert.Equal(2060, catalog.Settings.DefaultEndYear);
        Assert.Equal(100, catalog.Settings.DbBatchSize);
    }

    [Fact]
    public void MissingFileThrows()
    {
        var missing = Path.Combine(TestPaths.RepoRoot, "config", "does-not-exist.json");
        Assert.Throws<FileNotFoundException>(() => MetricsCatalog.Load(missing));
    }
}
