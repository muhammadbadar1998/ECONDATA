using System.Text.Json.Serialization;

namespace EconData.Web.Services;

public enum JobKind
{
    Check,
    Init,
    Extract,
    Vectorize,
    RunAll,
    Rebuild
}

public sealed record JobRequest(
    [property: JsonPropertyName("kind")] JobKind Kind,
    [property: JsonPropertyName("metrics")] IReadOnlyList<string>? Metrics,
    [property: JsonPropertyName("load_to_db")] bool LoadToDb,
    [property: JsonPropertyName("reembed")] bool Reembed,
    [property: JsonPropertyName("bronze")] bool Bronze = false,
    [property: JsonPropertyName("silver")] bool Silver = false,
    [property: JsonPropertyName("gold")] bool Gold = false);

public sealed record JobProgressUpdate(
    [property: JsonPropertyName("phase")] string Phase,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("current")] int Current,
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("metric_code")] string? MetricCode = null,
    [property: JsonPropertyName("records")] int Records = 0,
    [property: JsonPropertyName("upserted")] int Upserted = 0,
    [property: JsonPropertyName("seconds")] double Seconds = 0,
    [property: JsonPropertyName("step_state")] string? StepState = null);

public sealed record JobResult(
    [property: JsonPropertyName("message")] string Message);

public enum JobState
{
    Idle,
    Running,
    Succeeded,
    Failed,
    Cancelled
}

public sealed record StepLine(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("metric_code")] string MetricCode,
    [property: JsonPropertyName("records")] int Records,
    [property: JsonPropertyName("upserted")] int Upserted,
    [property: JsonPropertyName("seconds")] double Seconds,
    [property: JsonPropertyName("state")] string State);

public sealed record JobStatus(
    [property: JsonPropertyName("job_id")] string? JobId,
    [property: JsonPropertyName("kind")] JobKind Kind,
    [property: JsonPropertyName("state")] JobState State,
    [property: JsonPropertyName("started_at")] DateTimeOffset? StartedAt,
    [property: JsonPropertyName("finished_at")] DateTimeOffset? FinishedAt,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("phase")] string? Phase,
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("current")] int Current,
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("steps")] IReadOnlyList<StepLine> Steps);
