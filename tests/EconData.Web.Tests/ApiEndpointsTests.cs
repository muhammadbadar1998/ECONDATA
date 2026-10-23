using System.Net.Http.Json;
using EconData.Core.Analytics;
using Xunit;

namespace EconData.Web.Tests;

public class ApiEndpointsTests : IClassFixture<WebAppFixture>
{
    private readonly HttpClient _client;

    public ApiEndpointsTests(WebAppFixture fixture)
    {
        _client = fixture.Client;
    }

    [Fact]
    public async Task GetCoverage_ReturnsNineMetrics_GdpUsdHasYearRange()
    {
        var coverage = await _client.GetFromJsonAsync<IReadOnlyList<MetricCoverage>>("/api/coverage");

        Assert.NotNull(coverage);
        Assert.Equal(26, coverage!.Count);
        var gdpUsd = coverage.FirstOrDefault(c => c.MetricCode == "gdp_usd");
        Assert.NotNull(gdpUsd);
        Assert.NotNull(gdpUsd!.MinPeriod);
        Assert.NotNull(gdpUsd.MaxPeriod);
        Assert.Equal("2022-01-01", gdpUsd.MinPeriod);
        Assert.Equal("2024-01-01", gdpUsd.MaxPeriod);
        Assert.Equal(1, gdpUsd.EntityCount);
        Assert.Equal(3, gdpUsd.ObservationCount);

        foreach (var row in coverage.Where(c => c.MetricCode != "gdp_usd"))
        {
            Assert.Null(row.MinPeriod);
            Assert.Null(row.MaxPeriod);
            Assert.Equal(0, row.EntityCount);
            Assert.Equal(0, row.ObservationCount);
        }
    }

    [Fact]
    public async Task GetSeries_UsaGdpUsd_ReturnsThreePoints()
    {
        var series = await _client.GetFromJsonAsync<IReadOnlyList<SeriesPoint>>("/api/series?entity=USA&metric=gdp_usd");

        Assert.NotNull(series);
        Assert.Equal(3, series!.Count);
        Assert.Equal(100.0, series[0].Value);
        Assert.Equal(200.0, series[1].Value);
        Assert.Equal(300.0, series[2].Value);
        Assert.Equal("2022-01-01", series[0].Period);
        Assert.Equal("2023-01-01", series[1].Period);
        Assert.Equal("2024-01-01", series[2].Period);
    }

    [Fact]
    public async Task GetSeries_MissingParameters_Returns400()
    {
        var response = await _client.GetAsync("/api/series");
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetSnapshot_UsaGdpUsd_ReturnsLatestValue300()
    {
        var snapshot = await _client.GetFromJsonAsync<IReadOnlyList<LatestSnapshotRow>>("/api/snapshot?entity=USA&metric=gdp_usd&limit=10");

        Assert.NotNull(snapshot);
        Assert.Single(snapshot!);
        Assert.Equal(300.0, snapshot![0].Value);
        Assert.Equal("USA", snapshot[0].CountryCode);
        Assert.Equal("gdp_usd", snapshot[0].MetricCode);
    }

    [Fact]
    public async Task GetEntities_ReturnsSeedEntitiesAndFilters()
    {
        var all = await _client.GetFromJsonAsync<IReadOnlyList<EntitySummary>>("/api/entities?limit=1000");
        Assert.NotNull(all);
        Assert.Equal(85, all!.Count);

        var usa = await _client.GetFromJsonAsync<IReadOnlyList<EntitySummary>>("/api/entities?q=USA&limit=10");
        Assert.NotNull(usa);
        Assert.Single(usa!);
        Assert.Equal("USA", usa![0].EntityCode);
    }

    [Fact]
    public async Task GetRuns_ReturnsOneSuccessRun()
    {
        var runs = await _client.GetFromJsonAsync<IReadOnlyList<RunSummary>>("/api/runs?limit=50");

        Assert.NotNull(runs);
        Assert.Single(runs!);
        Assert.Equal("SUCCESS", runs![0].Status);
        Assert.Equal(1, runs[0].MetricsExtracted);
        Assert.Equal(3, runs[0].ObservationsUpserted);
    }
}
