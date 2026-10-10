using System.Text.Json.Serialization;

namespace EconData.Core.Analytics;

/// <summary>High-level counts and latest run info for the dashboard overview.</summary>
public sealed record OverviewStats(
    [property: JsonPropertyName("entity_count")] int EntityCount,
    [property: JsonPropertyName("alias_count")] int AliasCount,
    [property: JsonPropertyName("metric_count")] int MetricCount,
    [property: JsonPropertyName("source_count")] int SourceCount,
    [property: JsonPropertyName("observation_count")] long ObservationCount,
    [property: JsonPropertyName("embedding_count")] int EmbeddingCount,
    [property: JsonPropertyName("membership_count")] int MembershipCount,
    [property: JsonPropertyName("last_run_status")] string? LastRunStatus,
    [property: JsonPropertyName("last_run_timestamp")] string? LastRunTimestamp);
