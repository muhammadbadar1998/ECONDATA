using System.Globalization;

namespace EconData.Core.Cli;

/// <summary>Minimal hand-rolled <c>--key value</c> / <c>--flag</c> parser (no preview dependencies).</summary>
public sealed class Arguments
{
    private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _present = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Positional { get; } = new();

    public static Arguments Parse(IReadOnlyList<string> args, int startIndex)
    {
        ArgumentNullException.ThrowIfNull(args);
        var result = new Arguments();
        for (var i = startIndex; i < args.Count; i++)
        {
            var token = args[i];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                result.Positional.Add(token);
                continue;
            }

            var name = token[2..];
            var equals = name.IndexOf('=');
            if (equals >= 0)
            {
                var key = name[..equals];
                result._present.Add(key);
                result._values[key] = name[(equals + 1)..];
                continue;
            }

            if (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                result._present.Add(name);
                result._values[name] = args[++i];
            }
            else
            {
                result._present.Add(name);
                result._values[name] = null;
            }
        }

        return result;
    }

    public bool Has(string name) => _present.Contains(name);

    public string? Value(string name, string? fallback = null)
        => _values.TryGetValue(name, out var value) && value is not null ? value : fallback;

    public int IntValue(string name, int fallback)
        => _values.TryGetValue(name, out var value)
           && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
}
