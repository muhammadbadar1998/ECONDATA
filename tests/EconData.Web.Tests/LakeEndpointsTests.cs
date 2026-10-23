using System.Net.Http.Json;
using EconData.Core.Analytics;
using Xunit;

namespace EconData.Web.Tests;

public class LakeEndpointsTests : IClassFixture<WebAppFixture>
{
    private readonly HttpClient _client;

    public LakeEndpointsTests(WebAppFixture fixture)
    {
        _client = fixture.Client;
    }

    [Fact]
    public async Task GetLakeStatus_ReturnsAllThreeTiers()
    {
        var status = await _client.GetFromJsonAsync<LakeStatus>("/api/lake/status");

        Assert.NotNull(status);
        Assert.Equal("bronze", status!.Bronze.Tier);
        Assert.Equal("silver", status.Silver.Tier);
        Assert.Equal("gold", status.Gold.Tier);
        Assert.False(string.IsNullOrEmpty(status.Directory));
        Assert.False(string.IsNullOrEmpty(status.GoldPath));
    }

    [Fact]
    public async Task GetLakeStatus_ReportsEmptyTiersWithoutFailing()
    {
        var status = await _client.GetFromJsonAsync<LakeStatus>("/api/lake/status");

        Assert.NotNull(status);
        Assert.NotNull(status!.Tables);
        Assert.NotNull(status.Partitions);
        if (!status.Gold.Present)
        {
            Assert.Equal("not built yet", status.Gold.Detail);
            Assert.Empty(status.Tables);
        }
    }

    [Fact]
    public async Task LakePage_Renders()
    {
        var response = await _client.GetAsync("/lake");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task OverviewPage_Renders()
    {
        var response = await _client.GetAsync("/");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }
}
