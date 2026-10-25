using System.Net.Http.Json;
using System.Text.Json;
using EconData.Web.Services;
using Xunit;

namespace EconData.Web.Tests;

/// <summary>
/// Guards the widget DTOs against the API's snake_case payloads. Blazor's
/// <c>GetFromJsonAsync</c> defaults to web (camelCase) naming, so these mirror records
/// only bind when their <see cref="System.Text.Json.Serialization.JsonPropertyNameAttribute"/>
/// values match the wire format exactly.
/// </summary>
public class AnalyticsDtoJsonTests : IClassFixture<WebAppFixture>
{
    private readonly HttpClient _client;

    public AnalyticsDtoJsonTests(WebAppFixture fixture)
    {
        _client = fixture.Client;
    }

    [Fact]
    public async Task Growth_DeserializesThroughWebDto()
    {
        var growth = await _client.GetFromJsonAsync<IReadOnlyList<GrowthPointDto>>(
            "/api/analytics/growth?metric=gdp_usd&entity=USA&kind=yoy");

        Assert.NotNull(growth);
        var point = Assert.Single(growth!);
        Assert.Equal("2024-01-01", point.Period);
        Assert.Equal(50.0, point.GrowthPct);
    }

    [Fact]
    public async Task Ranking_DeserializesThroughWebDto()
    {
        var ranking = await _client.GetFromJsonAsync<IReadOnlyList<RankingRowDto>>(
            "/api/analytics/ranking?metric=gdp_usd&period=2024-01-01&direction=desc");

        Assert.NotNull(ranking);
        var row = Assert.Single(ranking!);
        Assert.Equal("USA", row.EntityCode);
        Assert.Equal(1, row.Rank);
    }

    [Fact]
    public async Task Freshness_DeserializesThroughWebDto()
    {
        var freshness = await _client.GetFromJsonAsync<IReadOnlyList<FreshnessDto>>("/api/analytics/freshness");

        Assert.NotNull(freshness);
        var gdp = freshness!.FirstOrDefault(record => record.MetricCode == "gdp_usd");
        Assert.NotNull(gdp);
        Assert.Equal("2024-01-01", gdp!.LatestPeriod);
        Assert.Equal(3, gdp.RowCount);
    }

    [Fact]
    public async Task AnalyticsPayload_UsesSnakeCaseKeys()
    {
        using var document = JsonDocument.Parse(
            await _client.GetStringAsync("/api/analytics/freshness"));
        var first = document.RootElement.EnumerateArray().First();

        Assert.True(first.TryGetProperty("metric_code", out _), "freshness must serialise metric_code");
        Assert.True(first.TryGetProperty("lag_years", out _), "freshness must serialise lag_years");
    }
}
