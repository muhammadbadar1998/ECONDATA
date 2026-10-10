using System.Text.Json.Serialization;

namespace EconData.Core.Analytics;

/// <summary>Single time-series observation point.</summary>
public sealed record SeriesPoint(
    [property: JsonPropertyName("entity_code")] string EntityCode,
    [property: JsonPropertyName("metric_code")] string MetricCode,
    [property: JsonPropertyName("period")] string Period,
    [property: JsonPropertyName("value")] double Value,
    [property: JsonPropertyName("unit")] string? Unit,
    [property: JsonPropertyName("period_str")] string? PeriodStr);
