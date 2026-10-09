using EconData.Core.Data;
using Microsoft.Data.Sqlite;
using Serilog;
using Serilog.Core;

namespace EconData.Core.Entities;

/// <summary>
/// Maps a provider dimension code to a canonical entity code. Resolution order:
/// curated alias cache, optional live alias-table lookup, ISO-3 heuristic for
/// WB/OECD/IMF, then deterministic registration of unknowns.
/// </summary>
public sealed class EntityResolver
{
    private static readonly HashSet<string> RefusedCodes = new(StringComparer.Ordinal)
    {
        "ANNUAL", "QUARTERLY", "MONTHLY", "YEAR", "NA", "N/A",
    };

    private readonly Dictionary<(string Provider, string ProviderCode), string> _aliasCache = new();
    private readonly HashSet<string> _canonicalCodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _entityNames = new(StringComparer.Ordinal);
    private readonly SqliteConnectionFactory? _factory;
    private readonly ILogger _logger;

    public EntityResolver(EntitySeed seed, SqliteConnectionFactory? factory = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(seed);
        _factory = factory;
        _logger = logger ?? Logger.None;

        foreach (var entity in seed.Entities)
        {
            _canonicalCodes.Add(entity.EntityCode);
            _entityNames[entity.EntityCode] = entity.EntityName;
        }

        foreach (var alias in seed.Aliases)
        {
            _aliasCache[(alias.Provider, alias.ProviderCode)] = alias.EntityCode;
        }
    }

    public string? ResolveCode(string provider, string providerCode)
    {
        var key = (provider, providerCode);
        if (_aliasCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var dbCode = LookupDbAlias(provider, providerCode);
        if (dbCode is not null)
        {
            _aliasCache[key] = dbCode;
            return dbCode;
        }

        if (provider is "WB" or "OECD" or "IMF" && LooksLikeIso3(providerCode))
        {
            var canonical = providerCode.ToUpperInvariant();
            _aliasCache[key] = canonical;
            _logger.Debug("Accepted {Provider}/{Code} as canonical entity {Entity}.", provider, providerCode, canonical);
            return canonical;
        }

        return null;
    }

    public bool IsKnownEntity(string entityCode) => _canonicalCodes.Contains(entityCode);

    public string EntityName(string entityCode)
        => _entityNames.TryGetValue(entityCode, out var name) ? name : entityCode;

    public string RegisterUnknown(string provider, string providerCode, string? label)
    {
        var code = providerCode.ToUpperInvariant();
        if (RefusedCodes.Contains(code))
        {
            _logger.Warning("Refusing to register non-entity code '{Code}' from {Provider}.", code, provider);
            return code;
        }

        var name = string.IsNullOrEmpty(label) ? $"{provider}:{code}" : label.Trim();
        _canonicalCodes.Add(code);
        _entityNames[code] = name;
        _aliasCache[(provider, providerCode)] = code;
        _logger.Information(
            "Resolved unknown {Provider} code '{ProviderCode}' -> entity '{Code}' (name='{Name}').",
            provider,
            providerCode,
            code,
            name);
        return code;
    }

    private string? LookupDbAlias(string provider, string providerCode)
    {
        if (_factory is null)
        {
            return null;
        }

        try
        {
            using var connection = _factory.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT e.entity_code FROM dbe_entity_aliases a " +
                "JOIN dbe_entities e ON e.entity_id = a.entity_id " +
                "WHERE a.provider = @provider AND a.provider_code = @code";
            command.Parameters.AddWithValue("@provider", provider);
            command.Parameters.AddWithValue("@code", providerCode);
            return command.ExecuteScalar() as string;
        }
        catch (SqliteException ex)
        {
            _logger.Warning("Alias DB lookup failed for ({Provider}, {Code}): {Message}", provider, providerCode, ex.Message);
            return null;
        }
    }

    private static bool LooksLikeIso3(string code)
        => code.Length == 3 && code.All(char.IsLetter) && code.All(char.IsUpper);
}
