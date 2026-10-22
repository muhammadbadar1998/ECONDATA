using System.Text.Json.Serialization;

namespace EconData.Core.Analytics;

/// <summary>Latest value row for a given entity/metric combination.</summary>
public sealed record LatestSnapshotRow(
    [property: JsonPropertyName("country_code")] string CountryCode,
    [property: JsonPropertyName("country_name")] string CountryName,
    [property: JsonPropertyName("metric_code")] string MetricCode,
    [property: JsonPropertyName("metric_name")] string MetricName,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("frequency")] string Frequency,
    [property: JsonPropertyName("base_unit")] string BaseUnit,
    [property: JsonPropertyName("value")] double Value,
    [property: JsonPropertyName("period")] string Period,
    [property: JsonPropertyName("source_kind")] string SourceKind = "raw");
