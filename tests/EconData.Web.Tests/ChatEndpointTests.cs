using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace EconData.Web.Tests;

/// <summary>
/// Guards the chat endpoint wiring. The orchestrator is resolved from DI before the
/// handler body runs, so a missing registration shows up as a 500 here instead of
/// a 400 for an empty question.
/// </summary>
public class ChatEndpointTests : IClassFixture<WebAppFixture>
{
    private readonly HttpClient _client;

    public ChatEndpointTests(WebAppFixture fixture)
    {
        _client = fixture.Client;
    }

    [Fact]
    public async Task EmptyQuestion_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync("api/chat", new { question = "   " });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ChatEndpoint_ResolvesOrchestratorAndDoesNotReturnServerError()
    {
        // An empty question short-circuits before any LLM call, but only after the
        // endpoint's dependencies have been resolved from the container.
        var response = await _client.PostAsJsonAsync("api/chat", new { question = "" });

        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
