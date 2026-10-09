using System.Text.Json;
using System.Text.Json.Serialization;
using EconData.Core.Serialization;

namespace EconData.Core.Entities;

/// <summary>A seed entity row (canonical entity plus its aggregate/metadata flags).</summary>
public sealed record EntitySeedRow(
    [property: JsonPropertyName("entity_code")] string EntityCode,
    [property: JsonPropertyName("entity_name")] string EntityName,
    [property: JsonPropertyName("entity_type")] string EntityType,
    [property: JsonPropertyName("is_aggregate")] bool IsAggregate,
    [property: JsonPropertyName("parent_entity_code")] string? ParentEntityCode);

/// <summary>A provider-code to canonical-entity alias.</summary>
public sealed record EntitySeedAlias(
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("provider_code")] string ProviderCode,
    [property: JsonPropertyName("entity_code")] string EntityCode);

/// <summary>
/// Seed entities, aliases, and aggregate memberships loaded from
/// <c>config/entities.json</c> (exported once from the Python <c>src/entities.py</c>).
/// </summary>
public sealed class EntitySeed
{
    public IReadOnlyList<EntitySeedRow> Entities { get; }
    public IReadOnlyList<EntitySeedAlias> Aliases { get; }
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Memberships { get; }

    /// <summary>Total number of aggregate membership pairs across all aggregate entities.</summary>
    public int MembershipCount => Memberships.Values.Sum(members => members.Count);

    private EntitySeed(
        IReadOnlyList<EntitySeedRow> entities,
        IReadOnlyList<EntitySeedAlias> aliases,
        IReadOnlyDictionary<string, IReadOnlyList<string>> memberships)
    {
        Entities = entities;
        Aliases = aliases;
        Memberships = memberships;
    }

    public static EntitySeed Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Entity seed file not found: {path}", path);
        }

        using var stream = File.OpenRead(path);
        var file = JsonSerializer.Deserialize<EntitySeedFile>(stream, JsonDefaults.Options)
                   ?? throw new InvalidDataException($"Entity seed file is empty or invalid: {path}");

        var memberships = file.Memberships.ToDictionary(
            entry => entry.Key,
            entry => (IReadOnlyList<string>)entry.Value);
        return new EntitySeed(file.Entities, file.Aliases, memberships);
    }

    private sealed class EntitySeedFile
    {
        [JsonPropertyName("entities")] public List<EntitySeedRow> Entities { get; init; } = new();
        [JsonPropertyName("aliases")] public List<EntitySeedAlias> Aliases { get; init; } = new();
        [JsonPropertyName("memberships")] public Dictionary<string, List<string>> Memberships { get; init; } = new();
    }
}
