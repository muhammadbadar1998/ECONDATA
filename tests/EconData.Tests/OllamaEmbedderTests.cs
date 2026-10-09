using System.Text;
using System.Text.Json;
using EconData.Core.Ai;
using EconData.Core.Configuration;

namespace EconData.Tests;

public class OllamaEmbedderTests
{
    private static readonly Func<TimeSpan, CancellationToken, Task> NoDelay = (_, _) => Task.CompletedTask;

    [Fact]
    public async Task EmbedSingleTextReturnsVectorOfConfiguredDimension()
    {
        var handler = new StubHttpHandler().EnqueueJson(EmbeddingsJson(1, 768));
        using var http = new HttpClient(handler);
        var embedder = new OllamaEmbedder(Config(), http, delayAsync: NoDelay);

        var result = await embedder.EmbedAsync(new[] { "hello" });

        Assert.Single(result);
        Assert.Equal(768, result[0].Length);
    }

    [Fact]
    public async Task EmbedChunksByBatchSize()
    {
        var handler = new StubHttpHandler();
        for (var i = 0; i < 3; i++)
        {
            handler.Enqueue(request => StubHttpHandler.Json(EmbeddingsJson(InputCount(request), 768)));
        }

        using var http = new HttpClient(handler);
        var embedder = new OllamaEmbedder(Config(batchSize: 2), http, delayAsync: NoDelay);

        await embedder.EmbedAsync(new[] { "a", "b", "c", "d", "e" });

        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task EmbedQueryReturnsOneVector()
    {
        var handler = new StubHttpHandler().EnqueueJson(EmbeddingsJson(1, 768));
        using var http = new HttpClient(handler);
        var embedder = new OllamaEmbedder(Config(), http, delayAsync: NoDelay);

        var vector = await embedder.EmbedQueryAsync("what is GDP?");

        Assert.Equal(768, vector.Length);
    }

    [Fact]
    public async Task VerifyConnectivityTrueWhenReachable()
    {
        var handler = new StubHttpHandler().EnqueueJson("""{"models":[{"model":"nomic-embed-text"}]}""");
        using var http = new HttpClient(handler);
        var embedder = new OllamaEmbedder(Config(), http, delayAsync: NoDelay);

        Assert.True(await embedder.VerifyConnectivityAsync());
    }

    [Fact]
    public async Task VerifyConnectivityFalseWhenUnreachable()
    {
        var handler = new StubHttpHandler().EnqueueThrow("refused");
        using var http = new HttpClient(handler);
        var embedder = new OllamaEmbedder(Config(), http, delayAsync: NoDelay);

        Assert.False(await embedder.VerifyConnectivityAsync());
    }

    [Fact]
    public async Task WrongDimensionRetriesThenFails()
    {
        var handler = new StubHttpHandler()
            .EnqueueJson(EmbeddingsJson(1, 3))
            .EnqueueJson(EmbeddingsJson(1, 3))
            .EnqueueJson(EmbeddingsJson(1, 3));
        using var http = new HttpClient(handler);
        var embedder = new OllamaEmbedder(Config(), http, delayAsync: NoDelay);

        var error = await Assert.ThrowsAsync<EmbeddingException>(() => embedder.EmbedAsync(new[] { "text" }));
        Assert.Contains("failed after 3 attempts", error.Message, StringComparison.Ordinal);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task RetryThenSucceeds()
    {
        var handler = new StubHttpHandler()
            .EnqueueThrow("transient")
            .EnqueueJson(EmbeddingsJson(1, 768));
        using var http = new HttpClient(handler);
        var embedder = new OllamaEmbedder(Config(), http, delayAsync: NoDelay);

        var result = await embedder.EmbedAsync(new[] { "retry me" });

        Assert.Single(result);
        Assert.Equal(2, handler.Requests.Count);
    }

    private static AppConfig Config(int batchSize = 32)
        => new()
        {
            OllamaHost = "http://ollama.test",
            OllamaEmbeddingModel = "nomic-embed-text",
            OllamaTimeout = 5,
            OllamaEmbeddingBatchSize = batchSize,
        };

    private static int InputCount(HttpRequestMessage request)
    {
        var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("input").GetArrayLength();
    }

    private static string EmbeddingsJson(int count, int dimension)
    {
        var builder = new StringBuilder("{\"embeddings\":[");
        for (var i = 0; i < count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append('[');
            for (var j = 0; j < dimension; j++)
            {
                if (j > 0)
                {
                    builder.Append(',');
                }

                builder.Append("0.1");
            }

            builder.Append(']');
        }

        builder.Append("]}");
        return builder.ToString();
    }
}
