using System.Text.Json.Serialization;

namespace EconData.Core.Cli;

public enum PipelineProgressKind
{
    RunStarted,
    MetricStarted,
    MetricCompleted,
    RunCompleted
}

public sealed record PipelineProgress(
    [property: JsonPropertyName("kind")] PipelineProgressKind Kind,
    [property: JsonPropertyName("metric_code")] string? MetricCode,
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("records")] int Records,
    [property: JsonPropertyName("upserted")] int Upserted,
    [property: JsonPropertyName("elapsed_seconds")] double ElapsedSeconds);
