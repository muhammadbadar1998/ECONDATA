using System.Globalization;
using System.Text.Json;

namespace EconData.Core.Catalog;

/// <summary>Deterministic value formatting and vector encoding for catalog documents.</summary>
internal static class DocumentFormatting
{
    /// <summary>
    /// Render an observation value deterministically for document text (it feeds
    /// the content hash). Non-finite -&gt; "n/a"; "%" units use two decimals with a
    /// suffix; large integrals use thousands separators; others use two decimals.
    /// </summary>
    internal static string FormatValue(object? value, string baseUnit = "")
    {
        if (value is null)
        {
            return "n/a";
        }

        if (!TryToDouble(value, out var number))
        {
            return value.ToString() ?? "n/a";
        }

        if (double.IsNaN(number) || double.IsInfinity(number))
        {
            return "n/a";
        }

        var unit = (baseUnit ?? string.Empty).Trim();
        if (unit == "%")
        {
            return number.ToString("N2", CultureInfo.InvariantCulture) + "%";
        }

        if (number == Math.Truncate(number))
        {
            var asLong = (long)number;
            return Math.Abs(asLong) >= 1000
                ? asLong.ToString("N0", CultureInfo.InvariantCulture)
                : asLong.ToString(CultureInfo.InvariantCulture);
        }

        return number.ToString("N2", CultureInfo.InvariantCulture);
    }

    /// <summary>Normalize the recent-points value (JSON text, JsonElement, or list) into year/value pairs.</summary>
    internal static List<Dictionary<string, object?>> ParseRecentPoints(object? raw)
    {
        if (raw is null)
        {
            return new List<Dictionary<string, object?>>();
        }

        if (raw is string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return new List<Dictionary<string, object?>>();
            }

            try
            {
                using var document = JsonDocument.Parse(text);
                return ParseRecentElement(document.RootElement);
            }
            catch (JsonException)
            {
                return new List<Dictionary<string, object?>>();
            }
        }

        if (raw is JsonElement element)
        {
            return ParseRecentElement(element);
        }

        if (raw is IEnumerable<Dictionary<string, object?>> list)
        {
            return list.ToList();
        }

        return new List<Dictionary<string, object?>>();
    }

    /// <summary>Encode a float vector as little-endian float32 bytes for sqlite-vec.</summary>
    internal static byte[] EncodeVector(IReadOnlyList<float> vector)
    {
        var bytes = new byte[vector.Count * sizeof(float)];
        for (var i = 0; i < vector.Count; i++)
        {
            BitConverter.TryWriteBytes(bytes.AsSpan(i * sizeof(float)), vector[i]);
        }

        return bytes;
    }

    private static List<Dictionary<string, object?>> ParseRecentElement(JsonElement element)
    {
        var points = new List<Dictionary<string, object?>>();
        if (element.ValueKind != JsonValueKind.Array)
        {
            return points;
        }

        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            points.Add(new Dictionary<string, object?>
            {
                ["year"] = ReadNumber(item, "year"),
                ["value"] = ReadNumber(item, "value"),
            });
        }

        return points;
    }

    private static object? ReadNumber(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetInt64(out var number) ? (object)number : value.GetDouble(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => value.ToString(),
        };
    }

    private static bool TryToDouble(object value, out double result)
    {
        switch (value)
        {
            case double d:
                result = d;
                return true;
            case float f:
                result = f;
                return true;
            case int i:
                result = i;
                return true;
            case long l:
                result = l;
                return true;
            case decimal m:
                result = (double)m;
                return true;
            case string s:
                return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
            default:
                result = 0;
                return false;
        }
    }
}
