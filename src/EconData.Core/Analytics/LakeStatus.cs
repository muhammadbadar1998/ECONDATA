using System.Text.Json.Serialization;

namespace EconData.Core.Analytics;

/// <summary>Health of one lake tier, as rendered by the Lake page and <c>lake status</c>.</summary>
public sealed record TierStatus(
    [property: JsonPropertyName("tier")] string Tier,
    [property: JsonPropertyName("present")] bool Present,
    [property: JsonPropertyName("rows")] long Rows,
    [property: JsonPropertyName("files")] int Files,
    [property: JsonPropertyName("bytes")] long Bytes,
    [property: JsonPropertyName("newest")] string? Newest,
    [property: JsonPropertyName("detail")] string? Detail);

/// <summary>Whole-lake status consumed by <c>GET /api/lake/status</c> and the CLI.</summary>
public sealed record LakeStatus(
    [property: JsonPropertyName("directory")] string Directory,
    [property: JsonPropertyName("gold_path")] string GoldPath,
    [property: JsonPropertyName("bronze")] TierStatus Bronze,
    [property: JsonPropertyName("silver")] TierStatus Silver,
    [property: JsonPropertyName("gold")] TierStatus Gold,
    [property: JsonPropertyName("tables")] IReadOnlyDictionary<string, long> Tables,
    [property: JsonPropertyName("partitions")] IReadOnlyDictionary<string, long> Partitions)
{
    /// <summary>Lake root. Exposed as a non-shadowing name for Razor, where a bare
    /// <c>Directory</c> member would bind to <see cref="System.IO.Directory"/>.</summary>
    [JsonIgnore]
    public string DirectoryValue => Directory;
}
