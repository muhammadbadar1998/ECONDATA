using System.Text.Json.Serialization;

namespace EconData.Core.Analytics;

/// <summary>
/// A single growth data point: value and growth rate for a specific period.
/// </summary>
public sealed record GrowthPoint(
    [property: JsonPropertyName("period")] string Period,
    [property: JsonPropertyName("value")] double Value,
    [property: JsonPropertyName("growth_pct")] double GrowthPct);
