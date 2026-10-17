using System.Text.Json.Serialization;

namespace EconData.Web.Services;

public sealed record MetricItem(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("frequency")] string Frequency);
