using System.Text.Json;

namespace EconData.Core.Chat;

/// <summary>Validates a model-produced tool call against the fixed tool set.</summary>
public static class ChatToolValidator
{
    public static ChatToolValidation Validate(string name, JsonElement? arguments)
    {
        var tool = ChatTools.Find(name);
        if (tool is null)
        {
            return new ChatToolValidation(false, null, $"Unknown tool '{name}'. Available: {string.Join(", ", ChatTools.All.Select(t => t.Name))}.");
        }

        var args = arguments ?? JsonSerializer.Deserialize<JsonElement>("{}")!;
        if (args.ValueKind != JsonValueKind.Object)
        {
            return new ChatToolValidation(false, null, $"Tool '{name}' arguments must be a JSON object.");
        }

        return name switch
        {
            "get_series" => RequireString(args, "entity", "metric", name),
            "compare_entities" => RequireStringAndArray(args, "metric", "entities", name),
            "rank_entities" => RequireDirectionAndLimit(args, name),
            "get_latest" or "list_metrics" or "list_entities" =>
                new ChatToolValidation(true, new ChatToolCall(name, args), null),
            _ => new ChatToolValidation(false, null, $"No validator for tool '{name}'."),
        };
    }

    private static ChatToolValidation RequireString(JsonElement args, params string[] required)
    {
        var toolName = required.Length > 2 ? required[^1] : required[0];
        foreach (var req in required.Take(required.Length - 1))
        {
            if (!args.TryGetProperty(req, out var value)
                || value.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(value.GetString()))
            {
                return new ChatToolValidation(false, null, $"Tool requires non-empty string parameter '{req}'.");
            }
        }
        return new ChatToolValidation(true, new ChatToolCall(toolName, args), null);
    }

    private static ChatToolValidation RequireStringAndArray(JsonElement args, string stringParam, string arrayParam, string toolName)
    {
        if (!args.TryGetProperty(stringParam, out var str) ||
            str.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(str.GetString()))
        {
            return new ChatToolValidation(false, null, $"Tool requires non-empty string parameter '{stringParam}'.");
        }

        if (!args.TryGetProperty(arrayParam, out var arr) ||
            arr.ValueKind != JsonValueKind.Array ||
            arr.GetArrayLength() == 0)
        {
            return new ChatToolValidation(false, null, $"Tool requires non-empty array parameter '{arrayParam}'.");
        }

        return new ChatToolValidation(true, new ChatToolCall(toolName, args), null);
    }

    private static ChatToolValidation RequireDirectionAndLimit(JsonElement args, string toolName)
    {
        if (!args.TryGetProperty("direction", out var dir) || dir.ValueKind != JsonValueKind.String ||
            (!string.Equals(dir.GetString(), "asc", StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(dir.GetString(), "desc", StringComparison.OrdinalIgnoreCase)))
        {
            return new ChatToolValidation(false, null, "rank_entities requires direction 'asc' or 'desc'.");
        }
        return new ChatToolValidation(true, new ChatToolCall(toolName, args), null);
    }
}
