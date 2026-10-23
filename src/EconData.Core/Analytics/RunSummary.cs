using System.Text.Json.Serialization;

namespace EconData.Core.Analytics;

/// <summary>Summary of a pipeline run.</summary>
public sealed record RunSummary(
    [property: JsonPropertyName("run_id")] long RunId,
    [property: JsonPropertyName("run_timestamp")] string RunTimestamp,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("execution_seconds")] double ExecutionSeconds,
    [property: JsonPropertyName("metrics_extracted")] int MetricsExtracted,
    [property: JsonPropertyName("observations_upserted")] int ObservationsUpserted,
    [property: JsonPropertyName("observations_skipped")] int ObservationsSkipped,
    [property: JsonPropertyName("tiers_written")] string? TiersWritten);
