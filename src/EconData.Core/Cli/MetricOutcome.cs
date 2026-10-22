namespace EconData.Core.Cli;

/// <summary>Outcome of a single metric within a pipeline run.</summary>
public enum PipelineOutcome
{
    Succeeded,
    Failed,
    Skipped,
}

/// <summary>Per-metric result recorded for a pipeline run.</summary>
public sealed record MetricOutcome(
    string MetricCode,
    PipelineOutcome Outcome,
    int Observations,
    string? Message);
