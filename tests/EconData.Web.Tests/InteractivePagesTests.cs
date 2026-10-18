using Xunit;

namespace EconData.Web.Tests;

/// <summary>
/// Guards the interaction contract of the pages whose buttons depend on a live
/// Blazor circuit. Static SSR renders the same markup but never wires handlers,
/// which is exactly the regression these tests catch.
/// </summary>
public class InteractivePagesTests : IClassFixture<WebAppFixture>
{
    // Blazor emits a component boundary comment carrying the render mode for
    // interactive server components, e.g. <!--Blazor:{"type":"server",...}-->
    private const string InteractiveServerMarker = "\"type\":\"server\"";

    private readonly HttpClient _client;

    public InteractivePagesTests(WebAppFixture fixture)
    {
        _client = fixture.Client;
    }

    [Theory]
    [InlineData("/builder")]
    [InlineData("/data")]
    [InlineData("/runner")]
    public async Task InteractivePage_IsRenderedWithServerInteractivity(string route)
    {
        var html = await _client.GetStringAsync(route);

        Assert.Contains(InteractiveServerMarker, html, StringComparison.Ordinal);
    }
}
