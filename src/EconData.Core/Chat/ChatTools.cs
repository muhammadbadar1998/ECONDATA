using System.Text.Json;
using System.Text.Json.Nodes;

namespace EconData.Core.Chat;

/// <summary>Outcome of intent classification.</summary>
public enum ChatIntent
{
    /// <summary>Best-effort prose answer from the retrieved catalog (RAG path).</summary>
    Subjective,
    /// <summary>Exact answer from a fixed parameterized query (tool path).</summary>
    Objective,
}

/// <summary>One callable tool backed by a fixed, parameterized query. No raw SQL.</summary>
public sealed record ChatTool(
    string Name,
    string Description,
    JsonElement ParametersSchema);

/// <summary>A validated tool invocation ready to dispatch to a parameterized query.</summary>
public sealed record ChatToolCall(
    string Name,
    JsonElement Arguments);

/// <summary>Validation result for a proposed tool call.</summary>
public sealed record ChatToolValidation(
    bool IsValid,
    ChatToolCall? Call,
    string? Error);

/// <summary>Describes the fixed tool set the assistant may call for objective questions.</summary>
public static class ChatTools
{
    public static IReadOnlyList<ChatTool> All { get; } = new List<ChatTool>
    {
        new(
            "get_series",
            "Get the full time series for one entity and metric (e.g. US GDP over time).",
            ObjectSchema(
                ("entity", JsonValueKind.String, "The entity code, e.g. USA.", required: true),
                ("metric", JsonValueKind.String, "The metric code, e.g. gdp_usd.", required: true))),
        new(
            "get_latest",
            "Get the most recent value(s). Optionally filter by entity, metric, or category.",
            ObjectSchema(
                ("entity", JsonValueKind.String, "Optional entity code, e.g. USA.", required: false),
                ("metric", JsonValueKind.String, "Optional metric code, e.g. gdp_usd.", required: false),
                ("category", JsonValueKind.String, "Optional category, e.g. historical.", required: false))),
        new(
            "compare_entities",
            "Compare one metric across several entities (e.g. GDP for USA vs Germany vs France).",
            ObjectSchema(
                ("metric", JsonValueKind.String, "The metric code, e.g. gdp_usd.", required: true),
                ("entities", JsonValueKind.Array, "Entity codes to compare, e.g. [\"USA\", \"DEU\"].", required: true))),
        new(
            "rank_entities",
            "Rank entities for one metric (e.g. highest population). Direction is asc or desc.",
            ObjectSchema(
                ("metric", JsonValueKind.String, "The metric code, e.g. population.", required: true),
                ("direction", JsonValueKind.String, "asc or desc.", required: true),
                ("limit", JsonValueKind.Number, "How many results to return, default 10.", required: false))),
        new("list_metrics", "List available metrics in the catalog.", EmptySchema),
        new("list_entities", "List available entities in the catalog.", EmptySchema),
    };

    public static ChatTool? Find(string name) => All.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    private static readonly JsonElement EmptySchema = Parse("{}");

    private static JsonElement ObjectSchema(params (string name, JsonValueKind type, string description, bool required)[] props)
    {
        var requiredNames = props.Where(p => p.required).Select(p => p.name).ToList();
        var properties = new JsonObject();
        foreach (var (name, type, description, _) in props)
        {
            properties[name] = new JsonObject
            {
                ["type"] = type.ToString().ToLowerInvariant(),
                ["description"] = description,
            };
        }
        var doc = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray(requiredNames.Select(n => (JsonNode?)n).ToArray()),
        };
        return doc.Deserialize<JsonElement>()!;
    }

    private static JsonElement Parse(string json) => JsonSerializer.Deserialize<JsonElement>(json)!;
}
