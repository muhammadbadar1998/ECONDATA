using System.Text.Json.Serialization;

namespace EconData.Web.Services;

public sealed record GrowthPointDto(
    [property: JsonPropertyName("period")] string Period,
    [property: JsonPropertyName("value")] double Value,
    [property: JsonPropertyName("growth_pct")] double GrowthPct);

public sealed record RankingRowDto(
    [property: JsonPropertyName("rank")] int Rank,
    [property: JsonPropertyName("entity_code")] string EntityCode,
    [property: JsonPropertyName("entity_name")] string EntityName,
    [property: JsonPropertyName("value")] double Value,
    [property: JsonPropertyName("period")] string Period);

public sealed record FreshnessDto(
    [property: JsonPropertyName("metric_code")] string MetricCode,
    [property: JsonPropertyName("latest_period")] string? LatestPeriod,
    [property: JsonPropertyName("row_count")] int RowCount,
    [property: JsonPropertyName("lag_years")] int? LagYears);
