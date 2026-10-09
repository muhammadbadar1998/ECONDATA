using System.Text.Json.Serialization;

namespace EconData.Core.Configuration;

/// <summary>Global pipeline defaults from the <c>pipeline_settings</c> block of <c>config/metrics.json</c>.</summary>
public sealed class PipelineSettings
{
    [JsonPropertyName("default_start_year")] public int DefaultStartYear { get; init; } = 1960;
    [JsonPropertyName("default_end_year")] public int DefaultEndYear { get; init; } = 2060;
    [JsonPropertyName("include_forecasts")] public bool IncludeForecasts { get; init; } = true;
    [JsonPropertyName("db_batch_size")] public int DbBatchSize { get; init; } = 100;
}
