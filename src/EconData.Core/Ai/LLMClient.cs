using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EconData.Core.Configuration;
using Serilog;
using Serilog.Core;

namespace EconData.Core.Ai;

/// <summary>
/// Provider-agnostic chat-completion client for the OpenAI wire format
/// (<c>POST {base_url}/chat/completions</c>). Works with local Ollama, vLLM,
/// LM Studio, OpenAI, and Groq; only sends the auth header when a key is set.
/// </summary>
public sealed class LLMClient : IChatModel
{
    // HTTP statuses worth retrying: timeout, conflict, too-early, rate limit, 5xx.
    private static readonly HashSet<int> RetryableStatuses = new() { 408, 409, 425, 429, 500, 502, 503, 504 };

    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public string BaseUrl { get; }
    public string ApiKey { get; }
    public string Model { get; }
    public int TimeoutSeconds { get; }
    public double Temperature { get; }
    public int MaxRetries { get; }

    public LLMClient(
        AppConfig config,
        HttpClient httpClient,
        ILogger? logger = null,
        int maxRetries = 3,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient = httpClient;
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
        _logger = logger ?? Logger.None;
        _delay = delayAsync ?? Task.Delay;

        BaseUrl = config.LlmBaseUrl.TrimEnd('/');
        ApiKey = config.LlmApiKey;
        Model = config.LlmModel;
        TimeoutSeconds = config.LlmTimeout;
        Temperature = config.LlmTemperature;
        MaxRetries = Math.Max(1, maxRetries);
    }

    public string ChatUrl => $"{BaseUrl}/chat/completions";

    Task<JsonElement> IChatModel.ChatAsync(IReadOnlyList<ChatTurn> messages, CancellationToken cancellationToken)
        => ChatAsync(messages, null, cancellationToken);

    public IReadOnlyDictionary<string, object?> BuildPayload(IReadOnlyList<ChatTurn> messages, JsonElement? tools = null)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var payload = new Dictionary<string, object?>
        {
            ["model"] = Model,
            ["messages"] = messages
                .Select(message => (object?)new Dictionary<string, object?> { ["role"] = message.Role, ["content"] = message.Content })
                .ToList(),
            ["temperature"] = Temperature,
            ["stream"] = false,
        };

        if (tools is not null)
        {
            payload["tools"] = tools.Value;
        }

        return payload;
    }

    public async Task<bool> VerifyConnectivityAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CreateTimeoutToken(cancellationToken);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/models");
            ApplyAuthorization(request);
            using var response = await _httpClient.SendAsync(request, timeout.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status >= 400)
            {
                _logger.Warning("GET {Base}/models returned HTTP {Status}; the endpoint may not implement it.", BaseUrl, status);
                return true;
            }

            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            var listed = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                {
                    foreach (var model in data.EnumerateArray())
                    {
                        if (model.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                        {
                            listed.Add(id.GetString() ?? string.Empty);
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // A non-JSON /models body still means the endpoint is reachable.
            }

            if (listed.Count > 0 && !listed.Contains(Model))
            {
                _logger.Warning(
                    "LLM model '{Model}' is not listed by {Base} ({Listed}). For a local Ollama server run `ollama pull {Model}`.",
                    Model,
                    BaseUrl,
                    string.Join(", ", listed.OrderBy(value => value, StringComparer.Ordinal).Take(10)),
                    Model);
            }

            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.Error(ex, "LLM endpoint {Base} unreachable.", BaseUrl);
            return false;
        }
    }

    public async Task<JsonElement> ChatAsync(IReadOnlyList<ChatTurn> messages, JsonElement? tools = null, CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.Serialize(BuildPayload(messages, tools));
        var started = Stopwatch.StartNew();
        string? lastError = null;

        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            int status;
            string body;
            using (var timeout = CreateTimeoutToken(cancellationToken))
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, ChatUrl)
                    {
                        Content = new StringContent(payload, Encoding.UTF8, "application/json"),
                    };
                    ApplyAuthorization(request);
                    using var response = await _httpClient.SendAsync(request, timeout.Token).ConfigureAwait(false);
                    status = (int)response.StatusCode;
                    body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    lastError = $"cannot reach the LLM endpoint at {BaseUrl} ({ex.Message})";
                    _logger.Warning("LLM attempt {Attempt}/{Max} failed: {Message}", attempt, MaxRetries, ex.Message);
                    if (attempt < MaxRetries)
                    {
                        await _delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), cancellationToken).ConfigureAwait(false);
                    }

                    continue;
                }
            }

            if (status is 401 or 403)
            {
                throw new LLMException(
                    $"LLM endpoint {BaseUrl} rejected the credentials (HTTP {status}). " +
                    "Check LLM_API_KEY (it may be unset or wrong for this server).");
            }

            if (status == 404)
            {
                throw new LLMException(
                    $"LLM endpoint {BaseUrl} has no /chat/completions route (HTTP 404). " +
                    "Check LLM_BASE_URL points at the OpenAI-compatible root (usually .../v1).");
            }

            if (status >= 400 && !RetryableStatuses.Contains(status))
            {
                throw new LLMException($"LLM request failed with HTTP {status}: {SafeDetail(body)}");
            }

            if (RetryableStatuses.Contains(status))
            {
                lastError = $"HTTP {status}: {SafeDetail(body)}";
                _logger.Warning("LLM attempt {Attempt}/{Max} failed: {Detail}", attempt, MaxRetries, lastError);
            }
            else
            {
                var message = ParseMessage(body);
                _logger.Information("Chat completion succeeded in {Elapsed:F2}s (model={Model}).", started.Elapsed.TotalSeconds, Model);
                return message;
            }

            if (attempt < MaxRetries)
            {
                await _delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), cancellationToken).ConfigureAwait(false);
            }
        }

        throw new LLMException(
            $"LLM request to {BaseUrl} failed after {MaxRetries} attempts ({lastError}). " +
            $"Is the endpoint running and is LLM_MODEL='{Model}' available (`ollama pull <model>` for a local Ollama server)?");
    }

    private JsonElement ParseMessage(string body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new LLMException($"LLM endpoint {BaseUrl} returned a non-JSON body: {ex.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new LLMException($"LLM endpoint {BaseUrl} returned {root.ValueKind}, expected an object.");
            }

            var hasChoices = root.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0;

            if (root.TryGetProperty("error", out var error) && !hasChoices)
            {
                var detail = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var message)
                    ? message.GetString()
                    : error.ToString();
                throw new LLMException($"LLM endpoint returned an error: {detail}");
            }

            if (!hasChoices)
            {
                var keys = string.Join(", ", root.EnumerateObject().Select(property => property.Name).OrderBy(key => key, StringComparer.Ordinal));
                throw new LLMException(
                    $"LLM endpoint {BaseUrl} returned no choices (keys: {keys}). Is '{Model}' a chat-capable model?");
            }

            var first = choices[0];
            if (first.ValueKind != JsonValueKind.Object
                || !first.TryGetProperty("message", out var messageElement)
                || messageElement.ValueKind != JsonValueKind.Object)
            {
                throw new LLMException($"LLM response choice[0] has no 'message' object: {first}");
            }

            return messageElement.Clone();
        }
    }

    private void ApplyAuthorization(HttpRequestMessage request)
    {
        if (!string.IsNullOrEmpty(ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
        }
    }

    private static string SafeDetail(string body, int limit = 300)
    {
        var collapsed = string.Join(" ", body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (collapsed.Length == 0)
        {
            return "no response body";
        }

        return collapsed.Length > limit ? collapsed[..limit] : collapsed;
    }

    private CancellationTokenSource CreateTimeoutToken(CancellationToken cancellationToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));
        return cts;
    }
}
