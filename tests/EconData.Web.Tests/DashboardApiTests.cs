using System.Net;
using System.Net.Http.Json;
using EconData.Web.Services;

namespace EconData.Web.Tests;

public class DashboardApiTests : IClassFixture<DashboardWebAppFixture>
{
    private readonly DashboardWebAppFixture _fixture;
    private readonly HttpClient _client;

    public DashboardApiTests(DashboardWebAppFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    [Fact]
    public async Task GetLayout_MissingFile_ReturnsDefaults()
    {
        if (File.Exists(_fixture.LayoutPath))
        {
            File.Delete(_fixture.LayoutPath);
        }

        var layout = await _client.GetFromJsonAsync<DashboardLayout>("/api/dashboard/layout");

        Assert.NotNull(layout);
        Assert.Equal(1, layout!.Version);
        Assert.Equal(4, layout.Widgets.Count);
    }

    [Fact]
    public async Task PutLayout_ThenGet_RoundTrips()
    {
        var defaults = DashboardLayoutStore.DefaultLayout();
        var moved = defaults.Widgets[0] with { X = 6, Y = 0, Title = "Moved series" };
        var layout = defaults with { Widgets = new[] { moved }.Concat(defaults.Widgets.Skip(1)).ToList() };

        var put = await _client.PutAsJsonAsync("/api/dashboard/layout", layout);
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        var loaded = await _client.GetFromJsonAsync<DashboardLayout>("/api/dashboard/layout");
        Assert.NotNull(loaded);
        Assert.Equal(6, loaded!.Widgets[0].X);
        Assert.Equal("Moved series", loaded.Widgets[0].Title);
        Assert.Equal(layout.Widgets.Count, loaded.Widgets.Count);
    }

    [Fact]
    public async Task PutLayout_InvalidSize_Returns400()
    {
        var defaults = DashboardLayoutStore.DefaultLayout();
        var invalid = defaults with
        {
            Widgets = new[] { defaults.Widgets[0] with { W = 0 } }
        };

        var response = await _client.PutAsJsonAsync("/api/dashboard/layout", invalid);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PutLayout_UnknownType_Returns400()
    {
        var defaults = DashboardLayoutStore.DefaultLayout();
        var invalid = defaults with
        {
            Widgets = new[] { defaults.Widgets[0] with { Type = "pie" } }
        };

        var response = await _client.PutAsJsonAsync("/api/dashboard/layout", invalid);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
