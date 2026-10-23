using System.Text.Json.Serialization;

namespace EconData.Core.Analytics;

/// <summary>
/// Correlation result between two metrics over a shared period.
/// </summary>
public sealed record CorrelationResult(
    [property: JsonPropertyName("metric_code_1")] string MetricCode1,
    [property: JsonPropertyName("metric_code_2")] string MetricCode2,
    [property: JsonPropertyName("period")] string Period,
    [property: JsonPropertyName("n")] int N,
    [property: JsonPropertyName("correlation")] double Correlation);
