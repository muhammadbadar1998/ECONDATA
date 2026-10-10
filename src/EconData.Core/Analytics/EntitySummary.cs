using System.Text.Json.Serialization;

namespace EconData.Core.Analytics;

/// <summary>Summary information for an entity including alias and observation counts.</summary>
public sealed record EntitySummary(
    [property: JsonPropertyName("entity_code")] string EntityCode,
    [property: JsonPropertyName("entity_name")] string EntityName,
    [property: JsonPropertyName("entity_type")] string EntityType,
    [property: JsonPropertyName("is_aggregate")] int IsAggregate,
    [property: JsonPropertyName("alias_count")] int AliasCount,
    [property: JsonPropertyName("observation_count")] int ObservationCount);
