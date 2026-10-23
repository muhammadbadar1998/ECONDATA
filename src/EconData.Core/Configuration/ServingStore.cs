using System.Globalization;

namespace EconData.Core.Configuration;

/// <summary>
/// Which store read-only analytics read from.
/// </summary>
public enum ServingStore
{
    /// <summary>Read from DuckDB gold when it exists, otherwise fall back to SQLite.</summary>
    Auto,
    /// <summary>Read only from DuckDB gold; fail if it is missing.</summary>
    Gold,
    /// <summary>Read only from SQLite.</summary>
    Sqlite,
}

/// <summary>Parses the <c>ECONDATA_SERVING_STORE</c> configuration value.</summary>
public static class ServingStores
{
    public static ServingStore Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return ServingStore.Auto;

        return value.Trim().ToUpperInvariant() switch
        {
            "AUTO" => ServingStore.Auto,
            "GOLD" => ServingStore.Gold,
            "SQLITE" => ServingStore.Sqlite,
            var v => throw new InvalidOperationException(
                $"Unknown ECONDATA_SERVING_STORE '{v}'. Use 'auto', 'gold' or 'sqlite'.")
        };
    }
}
