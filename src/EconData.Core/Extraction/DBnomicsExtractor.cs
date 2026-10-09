using System.Globalization;
using System.Text.Json;
using EconData.Core.Configuration;
using Serilog;
using Serilog.Core;

namespace EconData.Core.Extraction;

/// <summary>Fetches economic series from the DBnomics REST API with retries and paging.</summary>
public sealed class DBnomicsExtractor : ISeriesSource
{
    public const string DefaultBaseUrl = "https://api.db.nomics.world/v22";
    private const int MaxRetries = 3;

    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly ILogger _logger;

    public DBnomicsExtractor(
        HttpClient httpClient,
        string baseUrl = DefaultBaseUrl,
        int timeoutSeconds = 30,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        _httpClient = httpClient;
        _baseUrl = baseUrl.TrimEnd('/');
        _httpClient.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("EconDataPipeline/1.0 (Economic Data Extraction Infra)");
        }

        _logger = logger ?? Logger.None;
    }

    public async Task<IReadOnlyList<RawSeries>> FetchMetricSeriesAsync(
        string metricCode,
        MetricsCatalog catalog,
        int limit = 1000,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var metric = catalog.Get(metricCode)
            ?? throw new ArgumentException($"Unknown metric code: '{metricCode}'.", nameof(metricCode));

        var url = $"{_baseUrl}/series/{metric.Provider}/{metric.Dataset}";
        var dimensionsFilter = BuildDimensionsFilter(metric.Provider, metric.Dataset, metric.IndicatorCode);

        _logger.Information(
            "Fetching series for '{Metric}' (Indicator: {Indicator}) from {Provider}/{Dataset}...",
            metricCode,
            metric.IndicatorCode,
            metric.Provider,
            metric.Dataset);
        _logger.Debug("GET {Url} dimensions={Dimensions} limit={Limit}", url, dimensionsFilter, limit);

        var all = new List<RawSeries>();
        var offset = 0;
        while (true)
        {
            var query =
                $"dimensions={Uri.EscapeDataString(dimensionsFilter)}&observations=1" +
                $"&limit={limit.ToString(CultureInfo.InvariantCulture)}&offset={offset.ToString(CultureInfo.InvariantCulture)}";
            var requestUri = $"{url}?{query}";

            using var document = await GetJsonWithRetryAsync(requestUri, cancellationToken).ConfigureAwait(false);
            var page = ParsePage(document.RootElement, out var numFound);
            all.AddRange(page);

            _logger.Information("Retrieved {Retrieved}/{Found} series for {Metric}...", all.Count, numFound, metricCode);
            if (all.Count >= numFound || page.Count == 0)
            {
                break;
            }

            offset += limit;
        }

        _logger.Debug("Finished fetch for '{Metric}': {Count} series.", metricCode, all.Count);
        return all;
    }

    private async Task<JsonDocument> GetJsonWithRetryAsync(string requestUri, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                using var response = await _httpClient.GetAsync(new Uri(requestUri), cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                _logger.Warning("Request attempt {Attempt}/{Max} failed for {Url}: {Message}", attempt, MaxRetries, requestUri, ex.Message);
                if (attempt == MaxRetries)
                {
                    throw;
                }

                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), cancellationToken).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException("Retry loop exited unexpectedly.");
    }

    private static List<RawSeries> ParsePage(JsonElement root, out int numFound)
    {
        numFound = 0;
        var page = new List<RawSeries>();

        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("series", out var seriesNode)
            || seriesNode.ValueKind != JsonValueKind.Object)
        {
            return page;
        }

        if (seriesNode.TryGetProperty("num_found", out var found) && found.ValueKind == JsonValueKind.Number)
        {
            numFound = found.GetInt32();
        }

        if (seriesNode.TryGetProperty("docs", out var docs) && docs.ValueKind == JsonValueKind.Array)
        {
            foreach (var doc in docs.EnumerateArray())
            {
                page.Add(ParseSeries(doc));
            }
        }

        return page;
    }

    private static RawSeries ParseSeries(JsonElement doc)
    {
        var dimensions = new Dictionary<string, string>(StringComparer.Ordinal);
        if (doc.TryGetProperty("dimensions", out var dims) && dims.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in dims.EnumerateObject())
            {
                dimensions[property.Name] = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString() ?? string.Empty
                    : property.Value.ToString();
            }
        }

        var seriesName = doc.TryGetProperty("series_name", out var name) && name.ValueKind == JsonValueKind.String
            ? name.GetString()
            : null;

        return new RawSeries(dimensions, ReadScalarArray(doc, "period"), ReadScalarArray(doc, "value"), seriesName);
    }

    private static List<string?> ReadScalarArray(JsonElement doc, string property)
    {
        var values = new List<string?>();
        if (doc.TryGetProperty(property, out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
            {
                values.Add(item.ValueKind switch
                {
                    JsonValueKind.String => item.GetString(),
                    JsonValueKind.Number => item.GetRawText(),
                    JsonValueKind.Null or JsonValueKind.Undefined => null,
                    _ => item.ToString(),
                });
            }
        }

        return values;
    }

    private static string BuildDimensionsFilter(string provider, string dataset, string indicatorCode)
    {
        var key = provider == "IMF" && dataset == "WEO"
            ? "weo-subject-code"
            : provider == "OECD"
                ? "INDICATOR"
                : "indicator";
        return JsonSerializer.Serialize(new Dictionary<string, string[]> { [key] = new[] { indicatorCode } });
    }
}
