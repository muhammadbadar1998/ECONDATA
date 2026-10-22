using System.Text.Json.Serialization;

namespace EconData.Core.Analytics;

/// <summary>Coverage statistics per metric across all entities.</summary>
public sealed record MetricCoverage(
    [property: JsonPropertyName("metric_code")] string MetricCode,
    [property: JsonPropertyName("metric_name")] string MetricName,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("frequency")] string Frequency,
    [property: JsonPropertyName("base_unit")] string BaseUnit,
    [property: JsonPropertyName("entity_count")] int EntityCount,
    [property: JsonPropertyName("observation_count")] int ObservationCount,
    [property: JsonPropertyName("derived_entity_count")] int DerivedEntityCount,
    [property: JsonPropertyName("derived_observation_count")] int DerivedObservationCount,
    [property: JsonPropertyName("min_period")] string? MinPeriod,
    [property: JsonPropertyName("max_period")] string? MaxPeriod);
