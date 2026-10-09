using System.Text.Json.Serialization;

namespace EconData.Core.Configuration;

/// <summary>A single catalog metric, mirroring the entries in <c>config/metrics.json</c>.</summary>
public sealed class MetricDefinition
{
    [JsonPropertyName("metric_code")] public string MetricCode { get; init; } = string.Empty;
    [JsonPropertyName("metric_name")] public string MetricName { get; init; } = string.Empty;
    [JsonPropertyName("category")] public string Category { get; init; } = "historical";
    [JsonPropertyName("provider")] public string Provider { get; init; } = "WB";
    [JsonPropertyName("dataset")] public string Dataset { get; init; } = "WDI";
    [JsonPropertyName("indicator_code")] public string IndicatorCode { get; init; } = string.Empty;
    [JsonPropertyName("unit")] public string Unit { get; init; } = string.Empty;
    [JsonPropertyName("frequency")] public string Frequency { get; init; } = "Annual";
    [JsonPropertyName("description")] public string Description { get; init; } = string.Empty;
    [JsonPropertyName("start_year")] public int? StartYear { get; init; }
    [JsonPropertyName("end_year")] public int? EndYear { get; init; }
    [JsonPropertyName("synonyms")] public IReadOnlyList<string> Synonyms { get; init; } = Array.Empty<string>();
    [JsonPropertyName("base_unit")] public string BaseUnit { get; init; } = string.Empty;
    [JsonPropertyName("unit_normalization")] public string UnitNormalization { get; init; } = string.Empty;
    [JsonPropertyName("dimension_key")] public string DimensionKey { get; init; } = "country";
    [JsonPropertyName("entity_scope")] public string EntityScope { get; init; } = "country";
    [JsonPropertyName("aggregation")] public string Aggregation { get; init; } = "mean";

    /// <summary>Base unit, falling back to the display unit when unset (matches the Python loader).</summary>
    public string EffectiveBaseUnit => string.IsNullOrEmpty(BaseUnit) ? Unit : BaseUnit;
}
