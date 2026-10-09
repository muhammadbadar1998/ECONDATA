using System.Net;
using System.Text.Json;
using EconData.Core.Ai;
using EconData.Core.Configuration;

namespace EconData.Tests;

public class LLMClientTests
{
    private static readonly Func<TimeSpan, CancellationToken, Task> NoDelay = (_, _) => Task.CompletedTask;
    private static readonly ChatTurn[] Hello = { new("user", "hi") };

    private static string OkBody(string content = "The latest value is X.")
        => "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"" + content + "\"}}]}";

    [Fact]
    public async Task ChatPostsExpectedPayloadToChatCompletions()
    {
        var handler = new StubHttpHandler().EnqueueJson(OkBody());
        using var http = new HttpClient(handler);
        var client = new LLMClient(Config(), http, delayAsync: NoDelay);

        var message = await client.ChatAsync(Hello, cancellationToken: default);

        Assert.Equal("The latest value is X.", message.GetProperty("content").GetString());
        var request = handler.Requests[0];
        Assert.Equal("http://llm.test/v1/chat/completions", request.Url);
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal("qwen2.5:7b-instruct", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("hi", body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.False(body.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal(0.2, body.RootElement.GetProperty("temperature").GetDouble());
        Assert.False(body.RootElement.TryGetProperty("tools", out _));
    }

    [Fact]
    public async Task OmitsAuthorizationHeaderWithoutKey()
    {
        var handler = new StubHttpHandler().EnqueueJson(OkBody());
        using var http = new HttpClient(handler);
        var client = new LLMClient(Config(), http, delayAsync: NoDelay);

        await client.ChatAsync(Hello, cancellationToken: default);

        Assert.Null(handler.Requests[0].Authorization);
    }

    [Fact]
    public async Task SendsAuthorizationHeaderWithKey()
    {
        var handler = new StubHttpHandler().EnqueueJson(OkBody());
        using var http = new HttpClient(handler);
        var client = new LLMClient(Config(apiKey: "sk-secret"), http, delayAsync: NoDelay);

        await client.ChatAsync(Hello, cancellationToken: default);

        Assert.Equal("Bearer sk-secret", handler.Requests[0].Authorization);
    }

    [Fact]
    public async Task PassesToolsThrough()
    {
        var handler = new StubHttpHandler().EnqueueJson(OkBody());
        using var http = new HttpClient(handler);
        var client = new LLMClient(Config(), http, delayAsync: NoDelay);
        using var tools = JsonDocument.Parse("""[{"type":"function","function":{"name":"get_series"}}]""");

        await client.ChatAsync(Hello, tools.RootElement.Clone());

        using var body = JsonDocument.Parse(handler.Requests[0].Body!);
        Assert.Equal("get_series", body.RootElement.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());
    }

    [Fact]
    public async Task ReturnsToolCallsWhenPresent()
    {
        const string payload = """{"choices":[{"message":{"role":"assistant","content":null,"tool_calls":[{"id":"call_1","function":{"name":"get_series"}}]}}]}""";
        var handler = new StubHttpHandler().EnqueueJson(payload);
        using var http = new HttpClient(handler);
        var client = new LLMClient(Config(), http, delayAsync: NoDelay);

        var message = await client.ChatAsync(Hello, cancellationToken: default);

        Assert.Equal("get_series", message.GetProperty("tool_calls")[0].GetProperty("function").GetProperty("name").GetString());
    }

    [Fact]
    public async Task UnauthorizedIsNotRetried()
    {
        var handler = new StubHttpHandler().EnqueueRaw("nope", status: HttpStatusCode.Unauthorized);
        using var http = new HttpClient(handler);
        var client = new LLMClient(Config(), http, delayAsync: NoDelay);

        var error = await Assert.ThrowsAsync<LLMException>(() => client.ChatAsync(Hello, cancellationToken: default));
        Assert.Contains("LLM_API_KEY", error.Message, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task NotFoundMentionsChatRoute()
    {
        var handler = new StubHttpHandler().EnqueueRaw("", status: HttpStatusCode.NotFound);
        using var http = new HttpClient(handler);
        var client = new LLMClient(Config(), http, delayAsync: NoDelay);

        var error = await Assert.ThrowsAsync<LLMException>(() => client.ChatAsync(Hello, cancellationToken: default));
        Assert.Contains("chat/completions", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConnectionErrorFailsAfterRetries()
    {
        var handler = new StubHttpHandler().EnqueueThrow().EnqueueThrow().EnqueueThrow();
        using var http = new HttpClient(handler);
        var client = new LLMClient(Config(), http, delayAsync: NoDelay);

        var error = await Assert.ThrowsAsync<LLMException>(() => client.ChatAsync(Hello, cancellationToken: default));
        Assert.Contains("failed after 3 attempts", error.Message, StringComparison.Ordinal);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task RetryThenSucceeds()
    {
        var handler = new StubHttpHandler().EnqueueThrow("transient").EnqueueJson(OkBody("second"));
        using var http = new HttpClient(handler);
        var client = new LLMClient(Config(), http, delayAsync: NoDelay);

        var message = await client.ChatAsync(Hello, cancellationToken: default);

        Assert.Equal("second", message.GetProperty("content").GetString());
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task ServerErrorRetriedThenFails()
    {
        var handler = new StubHttpHandler()
            .EnqueueRaw("unavailable", status: HttpStatusCode.ServiceUnavailable)
            .EnqueueRaw("unavailable", status: HttpStatusCode.ServiceUnavailable)
            .EnqueueRaw("unavailable", status: HttpStatusCode.ServiceUnavailable);
        using var http = new HttpClient(handler);
        var client = new LLMClient(Config(), http, delayAsync: NoDelay);

        var error = await Assert.ThrowsAsync<LLMException>(() => client.ChatAsync(Hello, cancellationToken: default));
        Assert.Contains("HTTP 503", error.Message, StringComparison.Ordinal);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task BadRequestIsNotRetried()
    {
        var handler = new StubHttpHandler().EnqueueRaw("bad schema", status: HttpStatusCode.BadRequest);
        using var http = new HttpClient(handler);
        var client = new LLMClient(Config(), http, delayAsync: NoDelay);

        var error = await Assert.ThrowsAsync<LLMException>(() => client.ChatAsync(Hello, cancellationToken: default));
        Assert.Contains("HTTP 400", error.Message, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task EmptyChoicesMentionsModel()
    {
        var handler = new StubHttpHandler().EnqueueJson("""{"choices":[],"id":"x"}""");
        using var http = new HttpClient(handler);
        var client = new LLMClient(Config(), http, delayAsync: NoDelay);

        var error = await Assert.ThrowsAsync<LLMException>(() => client.ChatAsync(Hello, cancellationToken: default));
        Assert.Contains("chat-capable model", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ErrorObjectInBodyIsSurfaced()
    {
        var handler = new StubHttpHandler().EnqueueJson("""{"error":{"message":"model not found"}}""");
        using var http = new HttpClient(handler);
        var client = new LLMClient(Config(), http, delayAsync: NoDelay);

        var error = await Assert.ThrowsAsync<LLMException>(() => client.ChatAsync(Hello, cancellationToken: default));
        Assert.Contains("model not found", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonJsonBodyIsSurfaced()
    {
        var handler = new StubHttpHandler().EnqueueRaw("<html>oops</html>");
        using var http = new HttpClient(handler);
        var client = new LLMClient(Config(), http, delayAsync: NoDelay);

        var error = await Assert.ThrowsAsync<LLMException>(() => client.ChatAsync(Hello, cancellationToken: default));
        Assert.Contains("non-JSON", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyConnectivityOk()
    {
        var handler = new StubHttpHandler().EnqueueJson("""{"data":[{"id":"qwen2.5:7b-instruct"}]}""");
        using var http = new HttpClient(handler);
        var client = new LLMClient(Config(), http, delayAsync: NoDelay);

        Assert.True(await client.VerifyConnectivityAsync());
        Assert.Equal("http://llm.test/v1/models", handler.Requests[0].Url);
    }

    [Fact]
    public async Task VerifyConnectivityFalseWhenUnreachable()
    {
        var handler = new StubHttpHandler().EnqueueThrow("refused");
        using var http = new HttpClient(handler);
        var client = new LLMClient(Config(), http, delayAsync: NoDelay);

        Assert.False(await client.VerifyConnectivityAsync());
    }

    [Fact]
    public async Task VerifyConnectivityToleratesMissingModelsRoute()
    {
        var handler = new StubHttpHandler().EnqueueRaw("not found", status: HttpStatusCode.NotFound);
        using var http = new HttpClient(handler);
        var client = new LLMClient(Config(), http, delayAsync: NoDelay);

        Assert.True(await client.VerifyConnectivityAsync());
    }

    private static AppConfig Config(string apiKey = "")
        => new()
        {
            LlmBaseUrl = "http://llm.test/v1",
            LlmApiKey = apiKey,
            LlmModel = "qwen2.5:7b-instruct",
            LlmTimeout = 7,
            LlmTemperature = 0.2,
        };
}
