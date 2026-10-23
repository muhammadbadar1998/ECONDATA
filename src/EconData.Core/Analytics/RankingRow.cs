using System.Text.Json.Serialization;

namespace EconData.Core.Analytics;

/// <summary>
/// A single row in a ranking result: rank position, entity, and value.
/// </summary>
public sealed record RankingRow(
    [property: JsonPropertyName("rank")] int Rank,
    [property: JsonPropertyName("entity_code")] string EntityCode,
    [property: JsonPropertyName("entity_name")] string EntityName,
    [property: JsonPropertyName("value")] double Value,
    [property: JsonPropertyName("period")] string Period);
