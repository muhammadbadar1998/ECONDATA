using System.Text.Json;
using EconData.Core.Ai;
using EconData.Core.Analytics;
using EconData.Core.Catalog;
using EconData.Core.Configuration;
using EconData.Core.Entities;
using EconData.Core.Rag;
using Serilog;
using Serilog.Core;

namespace EconData.Core.Chat;

/// <summary>
/// Intent-gated, tool-calling question answering over the EconData catalog.
//
// Subjective questions go to the existing RAG answerer (grounded in retrieved
// catalog documents). Objective questions are answered only from fixed,
// parameterized queries the LLM selects by name and parameters; the LLM never
// authors SQL.
/// </summary>
public sealed class ChatOrchestrator
{
    private const int MaxToolIterations = 3;
    private const string ObjectiveIntent = "objective";

    private readonly IChatModel _llm;
    private readonly IRagAnswerer _rag;
    private readonly AnalyticsService _analytics;
    private readonly MetricsCatalog _catalog;
    private readonly EntitySeed _seed;
    private readonly ILogger _logger;

    public ChatOrchestrator(
        IChatModel llm,
        IRagAnswerer rag,
        AnalyticsService analytics,
        MetricsCatalog catalog,
        EntitySeed seed,
        ILogger? logger = null)
    {
        _llm = llm;
        _rag = rag;
        _analytics = analytics;
        _catalog = catalog;
        _seed = seed;
        _logger = logger ?? Logger.None;
    }

    public string Model => _llm.Model;

    public async Task<ChatResult> AnswerAsync(string question, List<ChatTurn>? history = null, CancellationToken cancellationToken = default)
    {
        var trimmed = (question ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return new ChatResult(string.Empty, ChatIntent.Subjective, Array.Empty<string>());
        }

        var intent = await ClassifyIntentAsync(trimmed, cancellationToken).ConfigureAwait(false);
        _logger.Information("Classified intent {Intent} for question.", intent);

        return intent switch
        {
            ChatIntent.Objective => await AnswerObjectiveAsync(trimmed, history, cancellationToken),
            _ => await AnswerSubjectiveAsync(trimmed, history, cancellationToken),
        };
    }

    private async Task<ChatIntent> ClassifyIntentAsync(string question, CancellationToken cancellationToken)
    {
        var prompt = """
            You classify a user's question about an economic-data catalog into one of two intents.

            Reply with exactly one JSON object and nothing else:
            {"intent": "objective"} if the question asks for a specific number, value, ranking,
            comparison, or list that can be answered by querying the catalog (e.g. "what is the
            latest US GDP?", "compare GDP for USA and Germany", "which countries have the highest
            population?").

            {"intent": "subjective"} if the question asks for a definition, explanation, overview,
            or anything that requires prose rather than a single lookup (e.g. "what is GDP?",
            "why did inflation rise?", "tell me about the catalog").

            Do not include any other text.
            """;

        var messages = new List<ChatTurn>
        {
            new("system", "You are an intent classifier. Reply only with the JSON object."),
            new("user", prompt + "\n\nQUESTION: " + question),
        };

        try
        {
            var message = await _llm.ChatAsync(messages, cancellationToken).ConfigureAwait(false);
            var content = ExtractContent(message);
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            if (root.TryGetProperty("intent", out var intent) && intent.ValueKind == JsonValueKind.String)
            {
                var value = intent.GetString()?.Trim().ToLowerInvariant();
                if (string.Equals(value, ObjectiveIntent, StringComparison.OrdinalIgnoreCase))
                {
                    return ChatIntent.Objective;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Intent classification failed; defaulting to subjective.");
        }

        return ChatIntent.Subjective;
    }

    private async Task<ChatResult> AnswerSubjectiveAsync(string question, List<ChatTurn>? history, CancellationToken cancellationToken)
    {
        var (text, docs) = await _rag.AnswerAsync(question, history, cancellationToken).ConfigureAwait(false);
        return new ChatResult(text, ChatIntent.Subjective, docs.Select(d => d.DocKey).ToList());
    }

    private async Task<ChatResult> AnswerObjectiveAsync(string question, List<ChatTurn>? history, CancellationToken cancellationToken)
    {
        var messages = new List<ChatTurn>
        {
            new("system", ObjectiveSystemPrompt),
            new("user", question),
        };

        if (history is not null)
        {
            foreach (var turn in Tail(history, 10))
            {
                messages.Insert(messages.Count - 1, turn);
            }
        }

        var tools = ChatTools.All.Select(t => new Dictionary<string, object?>
        {
            ["type"] = "function",
            ["function"] = new Dictionary<string, object?>
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["parameters"] = t.ParametersSchema,
            },
        }).ToList();

        var toolsJson = JsonSerializer.SerializeToElement(new { tools });

        for (var iteration = 0; iteration < MaxToolIterations; iteration++)
        {
            var message = await _llm.ChatAsync(messages, toolsJson, cancellationToken).ConfigureAwait(false);
            var toolCalls = ExtractToolCalls(message);

            if (toolCalls is null || toolCalls.Count == 0)
            {
                var text = ExtractContent(message);
                if (text.Length == 0)
                {
                    text = "The model returned an empty answer for this question.";
                }
                if (history is not null)
                {
                    history.Add(new ChatTurn("user", question));
                    history.Add(new ChatTurn("assistant", text));
                }
                return new ChatResult(text, ChatIntent.Objective, Array.Empty<string>());
            }

            foreach (var call in toolCalls)
            {
                var validation = ChatToolValidator.Validate(call.Name, call.Arguments);
                if (!validation.IsValid || validation.Call is null)
                {
                    messages.Add(new ChatTurn("tool", $"Invalid call to {call.Name}: {validation.Error}"));
                    continue;
                }

                var result = await DispatchAsync(validation.Call, cancellationToken).ConfigureAwait(false);
                messages.Add(new ChatTurn("tool", result));
            }
        }

        return new ChatResult("The model did not produce a final answer after several attempts.", ChatIntent.Objective, Array.Empty<string>());
    }

    private async Task<string> DispatchAsync(ChatToolCall call, CancellationToken cancellationToken)
    {
        try
        {
            return call.Name switch
            {
                "get_series" => FormatSeries(GetSeries(GetString(call.Arguments, "entity"), GetString(call.Arguments, "metric"))),
                "get_latest" => FormatSnapshot(GetLatestSnapshot(
                    OptString(call.Arguments, "entity"), OptString(call.Arguments, "metric"), OptString(call.Arguments, "category"))),
                "compare_entities" => FormatSeries(CompareEntities(GetString(call.Arguments, "metric"), GetStringArray(call.Arguments, "entities"))),
                "rank_entities" => FormatSnapshot(RankEntities(GetString(call.Arguments, "metric"), GetString(call.Arguments, "direction"), GetLimit(call.Arguments))),
                "list_metrics" => FormatList(_catalog.Codes.Select(c => $"{c} — {_catalog.Get(c)?.MetricName ?? c}")),
                "list_entities" => FormatList(_seed.Entities.Select(e => $"{e.EntityCode} — {e.EntityName}")),
                _ => $"Unknown tool {call.Name}.",
            };
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Tool {Tool} failed.", call.Name);
            return $"Tool {call.Name} failed: {ex.Message}.";
        }
    }

    private static string GetString(JsonElement args, string name)
        => args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

    private static string? OptString(JsonElement args, string name)
        => args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int GetLimit(JsonElement args)
    {
        if (args.TryGetProperty("limit", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) && n > 0)
        {
            return Math.Clamp(n, 1, 100);
        }
        return 10;
    }

    private static IReadOnlyList<string> GetStringArray(JsonElement args, string name)
    {
        var list = new List<string>();
        if (args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in v.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                {
                    list.Add(item.GetString()!);
                }
            }
        }
        return list;
    }

    private IReadOnlyList<SeriesPoint> GetSeries(string entity, string metric)
        => _analytics.GetSeries(entity, metric);

    private IReadOnlyList<LatestSnapshotRow> GetLatestSnapshot(string? entity, string? metric, string? category)
        => _analytics.GetLatestSnapshot(entity, metric, category, 20);

    private IReadOnlyList<SeriesPoint> CompareEntities(string metric, IReadOnlyList<string> entities)
        => _analytics.GetSeriesForEntities(metric, entities);

    private IReadOnlyList<LatestSnapshotRow> RankEntities(string metric, string direction, int limit)
    {
        var rows = _analytics.GetLatestSnapshot(null, metric, null, 1000);
        var ordered = string.Equals(direction, "desc", StringComparison.OrdinalIgnoreCase)
            ? rows.OrderByDescending(r => r.Value)
            : rows.OrderBy(r => r.Value);
        return ordered.Take(limit).ToList();
    }

    private static string FormatSeries(IReadOnlyList<SeriesPoint> points)
    {
        if (points.Count == 0)
        {
            return "No data found for the requested entity/metric.";
        }
        var lines = points.Select(p => $"{p.Period}: {p.Value:G} {p.Unit ?? string.Empty}".TrimEnd());
        return string.Join("\n", lines);
    }

    private static string FormatSnapshot(IReadOnlyList<LatestSnapshotRow> rows)
    {
        if (rows.Count == 0)
        {
            return "No data found for the requested criteria.";
        }
        var lines = rows.Select(r => $"{r.CountryCode} ({r.MetricCode}): {r.Value:G} {r.BaseUnit ?? string.Empty} as of {r.Period}".TrimEnd());
        return string.Join("\n", lines);
    }

    private static string FormatList(IEnumerable<string> items)
    {
        var list = items.ToList();
        if (list.Count == 0)
        {
            return "(none)";
        }
        return string.Join("\n", list);
    }

    private static List<ChatToolCall>? ExtractToolCalls(JsonElement message)
    {
        if (!message.TryGetProperty("tool_calls", out var calls) || calls.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        var result = new List<ChatToolCall>();
        foreach (var call in calls.EnumerateArray())
        {
            var name = call.TryGetProperty("function", out var fn) && fn.TryGetProperty("name", out var n) ? n.GetString() : null;
            var arguments = call.TryGetProperty("function", out var f2) && f2.TryGetProperty("arguments", out var a) ? a : JsonSerializer.Deserialize<JsonElement>("{}")!;
            if (name is not null)
            {
                result.Add(new ChatToolCall(name, arguments));
            }
        }
        return result.Count > 0 ? result : null;
    }

    private static string ExtractContent(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.String)
        {
            return string.Empty;
        }
        return (content.GetString() ?? string.Empty).Trim();
    }

    private static IReadOnlyList<ChatTurn> Tail(IReadOnlyList<ChatTurn>? history, int maxMessages)
    {
        if (history is null || history.Count == 0)
        {
            return Array.Empty<ChatTurn>();
        }
        var filtered = history.Where(t => !string.IsNullOrEmpty(t.Role)).ToList();
        return maxMessages > 0 && filtered.Count > maxMessages
            ? filtered.Skip(filtered.Count - maxMessages).ToList()
            : filtered;
    }

    private const string ObjectiveSystemPrompt = """
        You answer objective questions about an economic-data catalog ONLY by calling the available
        tools with their exact parameter names. You never author SQL, never recall figures from
        memory, and never estimate.

        Available tools: get_series, get_latest, compare_entities, rank_entities, list_metrics,
        list_entities.

        Rules:
        1. Pick the single tool that best answers the question and call it with validated parameters.
        2. If a tool returns no data, say the catalog does not cover that combination; do not guess.
        3. If multiple lookups are needed, call one tool at a time and incorporate each result.
        4. When you have enough information, give the final answer as plain text, citing the values
           exactly as returned by the tools. Preserve units exactly.
        5. Be concise.
        """;
}

/// <summary>The result of a chat turn.</summary>
public sealed record ChatResult(string Text, ChatIntent Intent, IReadOnlyList<string> Sources);
