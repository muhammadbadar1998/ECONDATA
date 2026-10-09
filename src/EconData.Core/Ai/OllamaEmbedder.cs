using System.Text;
using System.Text.Json;
using EconData.Core.Configuration;
using Serilog;
using Serilog.Core;

namespace EconData.Core.Ai;

/// <summary>
/// HTTP client for Ollama's <c>/api/embed</c> endpoint (default nomic-embed-text,
/// 768-dim). Batches texts, retries transient failures with backoff, and
/// dimension-validates every vector.
/// </summary>
public sealed class OllamaEmbedder
{
    public const int DefaultEmbeddingDim = 768;
    private const int MaxAttempts = 3;

    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public string BaseUrl { get; }
    public string Model { get; }
    public int TimeoutSeconds { get; }
    public int BatchSize { get; }
    public int EmbeddingDim { get; }

    public OllamaEmbedder(
        AppConfig config,
        HttpClient httpClient,
        ILogger? logger = null,
        int embeddingDim = DefaultEmbeddingDim,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient = httpClient;
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
        _logger = logger ?? Logger.None;
        _delay = delayAsync ?? Task.Delay;

        BaseUrl = config.OllamaHost.TrimEnd('/');
        Model = config.OllamaEmbeddingModel;
        TimeoutSeconds = config.OllamaTimeout;
        BatchSize = Math.Max(1, config.OllamaEmbeddingBatchSize);
        EmbeddingDim = embeddingDim;
    }

    public async Task<bool> VerifyConnectivityAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CreateTimeoutToken(cancellationToken);
        try
        {
            using var response = await _httpClient.GetAsync($"{BaseUrl}/api/tags", timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.Error("Ollama connectivity check failed: HTTP {Status} from {Base}/api/tags.", (int)response.StatusCode, BaseUrl);
                return false;
            }

            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);
            var loaded = new HashSet<string>(StringComparer.Ordinal);
            if (document.RootElement.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
            {
                foreach (var model in models.EnumerateArray())
                {
                    if (model.TryGetProperty("model", out var name) && name.ValueKind == JsonValueKind.String)
                    {
                        loaded.Add((name.GetString() ?? string.Empty).Split(':')[0]);
                    }
                }
            }

            if (!loaded.Contains(Model))
            {
                _logger.Warning(
                    "Ollama model {Model} not in /api/tags ({Loaded}); it may still be pulled on first use.",
                    Model,
                    loaded.Count > 0 ? string.Join(", ", loaded.OrderBy(value => value, StringComparer.Ordinal)) : "no models listed");
            }

            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.Error(ex, "Ollama connectivity check failed.");
            return false;
        }
    }

    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(texts);
        var results = new List<float[]>(texts.Count);
        _logger.Information("Embedding {Count} text(s) with model {Model} in batches of {Batch}.", texts.Count, Model, BatchSize);

        for (var offset = 0; offset < texts.Count; offset += BatchSize)
        {
            var batch = texts.Skip(offset).Take(BatchSize).ToList();
            results.AddRange(await EmbedBatchAsync(batch, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    public async Task<float[]> EmbedQueryAsync(string text, CancellationToken cancellationToken = default)
    {
        var results = await EmbedAsync(new[] { text }, cancellationToken).ConfigureAwait(false);
        return results[0];
    }

    private async Task<List<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        var payload = JsonSerializer.Serialize(new { model = Model, input = texts });

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            using var timeout = CreateTimeoutToken(cancellationToken);
            try
            {
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                using var response = await _httpClient.PostAsync($"{BaseUrl}/api/embed", content, timeout.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                using var document = JsonDocument.Parse(body);
                var embeddings = ParseEmbeddings(document.RootElement, texts.Count);
                foreach (var vector in embeddings)
                {
                    ValidateVector(vector);
                }

                _logger.Debug("Embedded batch of {Count} text(s).", texts.Count);
                return embeddings;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or EmbeddingException)
            {
                lastError = ex;
                _logger.Warning("Embedding attempt {Attempt}/{Max} failed for {Count} texts: {Message}", attempt, MaxAttempts, texts.Count, ex.Message);
                if (attempt < MaxAttempts)
                {
                    await _delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), cancellationToken).ConfigureAwait(false);
                }
            }
        }

        throw new EmbeddingException($"Ollama embedding failed after {MaxAttempts} attempts (model='{Model}'): {lastError?.Message}");
    }

    private List<float[]> ParseEmbeddings(JsonElement root, int expectedCount)
    {
        var embeddings = new List<float[]>();
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("embeddings", out var array)
            && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
            {
                embeddings.Add(ParseVector(item));
            }
        }
        else if (root.ValueKind == JsonValueKind.Object
                 && root.TryGetProperty("embedding", out var single)
                 && single.ValueKind == JsonValueKind.Array)
        {
            embeddings.Add(ParseVector(single));
        }
        else
        {
            var keys = root.ValueKind == JsonValueKind.Object
                ? string.Join(", ", root.EnumerateObject().Select(property => property.Name))
                : "non-object";
            throw new EmbeddingException($"Ollama /api/embed returned no embeddings for batch (model='{Model}'). Response keys: {keys}");
        }

        if (embeddings.Count != expectedCount)
        {
            throw new EmbeddingException($"Embedding count mismatch: requested {expectedCount} but got {embeddings.Count}");
        }

        return embeddings;
    }

    private static float[] ParseVector(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new EmbeddingException("Ollama returned a non-array embedding vector.");
        }

        var vector = new float[element.GetArrayLength()];
        var index = 0;
        foreach (var component in element.EnumerateArray())
        {
            vector[index++] = component.GetSingle();
        }

        return vector;
    }

    private void ValidateVector(float[] vector)
    {
        if (vector.Length != EmbeddingDim)
        {
            throw new EmbeddingException($"Expected {EmbeddingDim}-dimensional embedding, got {vector.Length} vector");
        }
    }

    private CancellationTokenSource CreateTimeoutToken(CancellationToken cancellationToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));
        return cts;
    }
}
