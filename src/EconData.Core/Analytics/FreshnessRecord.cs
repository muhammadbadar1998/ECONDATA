using System.Text.Json.Serialization;

namespace EconData.Core.Analytics;

/// <summary>
/// Freshness information for a metric: latest period, row count, and lag in years.
/// </summary>
public sealed record FreshnessRecord(
    [property: JsonPropertyName("metric_code")] string MetricCode,
    [property: JsonPropertyName("latest_period")] string? LatestPeriod,
    [property: JsonPropertyName("row_count")] int RowCount,
    [property: JsonPropertyName("lag_years")] int? LagYears);
