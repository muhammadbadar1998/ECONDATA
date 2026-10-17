using System.Text.Json.Serialization;

namespace EconData.Web.Services;

public sealed record LogEntry(
    [property: JsonPropertyName("sequence")] long Sequence,
    [property: JsonPropertyName("timestamp")] DateTimeOffset Timestamp,
    [property: JsonPropertyName("level")] string Level,
    [property: JsonPropertyName("message")] string Message);
