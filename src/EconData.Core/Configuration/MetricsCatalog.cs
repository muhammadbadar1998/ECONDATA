using System.Text.Json;
using System.Text.Json.Serialization;
using EconData.Core.Serialization;

namespace EconData.Core.Configuration;

/// <summary>The metric catalog loaded from <c>config/metrics.json</c>, keyed by metric code.</summary>
public sealed class MetricsCatalog
{
    private readonly Dictionary<string, MetricDefinition> _byCode;

    public PipelineSettings Settings { get; }
    public IReadOnlyDictionary<string, MetricDefinition> ByCode => _byCode;
    public IReadOnlyList<string> Codes { get; }

    private MetricsCatalog(PipelineSettings settings, IEnumerable<MetricDefinition> metrics)
    {
        Settings = settings;
        _byCode = new Dictionary<string, MetricDefinition>(StringComparer.Ordinal);
        foreach (var metric in metrics)
        {
            _byCode[metric.MetricCode] = metric;
        }

        Codes = _byCode.Keys.ToList();
    }

    public MetricDefinition? Get(string metricCode)
        => _byCode.TryGetValue(metricCode, out var metric) ? metric : null;

    public static MetricsCatalog Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Metrics config not found: {path}", path);
        }

        using var stream = File.OpenRead(path);
        var file = JsonSerializer.Deserialize<MetricsConfigFile>(stream, JsonDefaults.Options)
                   ?? throw new InvalidDataException($"Metrics config is empty or invalid: {path}");
        return new MetricsCatalog(file.Settings, file.Metrics);
    }

    private sealed class MetricsConfigFile
    {
        [JsonPropertyName("pipeline_settings")] public PipelineSettings Settings { get; init; } = new();
        [JsonPropertyName("metrics")] public List<MetricDefinition> Metrics { get; init; } = new();
    }
}
