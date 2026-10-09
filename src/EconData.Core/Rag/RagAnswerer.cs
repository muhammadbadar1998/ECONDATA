using System.Text.Json;
using EconData.Core.Ai;
using EconData.Core.Catalog;
using Serilog;
using Serilog.Core;

namespace EconData.Core.Rag;

/// <summary>
/// Read-only retrieval-augmented question answering over the embedded catalog.
/// Retrieval finds documents; the LLM may only answer from them. Nothing here
/// writes to the database.
/// </summary>
public sealed class RagAnswerer : IRagAnswerer
{
    public const int DefaultHistoryTurns = 10;

    /// <summary>Returned verbatim when retrieval finds nothing, so out-of-scope questions never reach the model.</summary>
    public const string NoContextAnswer =
        "I don't have any catalog data that covers that question. " +
        "I can only answer from the embedded economic catalog: metric definitions, " +
        "country/region entities, and per-country series summaries (latest, min, max, " +
        "and the most recent observations). Try naming a metric (e.g. GDP, inflation, " +
        "population) and a country or region.";

    public const string DefaultSystemPrompt = """
        You are a precise assistant for an economic-data catalog of country-level macroeconomic indicators (source: DBnomics / World Bank, stored in a local SQLite catalog).

        Answer ONLY from the CONTEXT DOCUMENTS supplied in the user message.

        Rules:
        1. Use only numbers, units, years, entities, and definitions that appear in the context. Never invent, estimate, extrapolate, or recall a figure from memory.
        2. If the context does not contain the answer, say so plainly and state what is missing. A clear "the catalog does not cover that" is always better than a guess.
        3. The context contains, per (entity, metric) pair: observation count and year range, the latest value, the minimum and maximum with their years, and the most recent observations. Arbitrary historical years outside those recent points, and cross-entity aggregates or rankings, are NOT available. Say so instead of computing or guessing them.
        4. Preserve the given units exactly (e.g. "Current US$", "%"). Quote values with thousands separators as shown in the context.
        5. Cite every document you use by its doc_key in square brackets, e.g. [USA|gdp_usd]. Add no citations for facts not drawn from the context.
        6. Be concise: a short paragraph or a compact list, no preamble.
        """;

    private readonly IDocumentSearcher _searcher;
    private readonly IChatModel _llm;
    private readonly ILogger _logger;

    public int K { get; }

    public string SystemPrompt { get; }

    public string Model => _llm.Model;

    public RagAnswerer(IDocumentSearcher searcher, IChatModel llm, int k = 8, string? systemPrompt = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(searcher);
        ArgumentNullException.ThrowIfNull(llm);
        _searcher = searcher;
        _llm = llm;
        K = k > 0 ? k : 8;
        SystemPrompt = string.IsNullOrWhiteSpace(systemPrompt) ? DefaultSystemPrompt : systemPrompt;
        _logger = logger ?? Logger.None;
    }

    public async Task<IReadOnlyList<SearchHit>> RetrieveAsync(string question, int? k = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            return Array.Empty<SearchHit>();
        }

        var query = question.Trim();
        var docs = await _searcher.SearchDocumentsAsync(query, k ?? K, cancellationToken).ConfigureAwait(false);
        _logger.Information("Retrieved {Count} document(s) for query.", docs.Count);
        _logger.Debug("Query text: {Query}", query.Length > 80 ? query[..80] : query);
        return docs;
    }

    public static string FormatContext(IReadOnlyList<SearchHit> docs)
    {
        ArgumentNullException.ThrowIfNull(docs);
        var blocks = docs.Select((doc, index) =>
            $"[{index + 1}] doc_key={doc.DocKey} doc_type={doc.DocType}\n{doc.Content.Trim()}");
        return "CONTEXT DOCUMENTS:\n\n" + string.Join("\n\n", blocks);
    }

    public IReadOnlyList<ChatTurn> BuildMessages(string question, IReadOnlyList<SearchHit> docs, IReadOnlyList<ChatTurn>? history = null)
    {
        ArgumentNullException.ThrowIfNull(docs);
        var messages = new List<ChatTurn>
        {
            new("system", SystemPrompt),
            new("user", FormatContext(docs)),
        };
        messages.AddRange(Tail(history, DefaultHistoryTurns * 2));
        messages.Add(new ChatTurn("user", question));
        return messages;
    }

    public async Task<(string Text, IReadOnlyList<SearchHit> Docs)> AnswerAsync(
        string question,
        List<ChatTurn>? history = null,
        CancellationToken cancellationToken = default)
    {
        var trimmed = (question ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return (string.Empty, Array.Empty<SearchHit>());
        }

        var docs = await RetrieveAsync(trimmed, null, cancellationToken).ConfigureAwait(false);
        if (docs.Count == 0)
        {
            _logger.Information("No documents retrieved; answering without the LLM.");
            _logger.Debug("Uncovered query: {Query}", trimmed);
            return (NoContextAnswer, Array.Empty<SearchHit>());
        }

        var messages = BuildMessages(trimmed, docs, history);
        _logger.Information("Querying LLM with {Count} context document(s).", docs.Count);
        var message = await _llm.ChatAsync(messages, cancellationToken).ConfigureAwait(false);
        var text = ExtractContent(message);
        if (text.Length == 0)
        {
            text = "The model returned an empty answer for this question.";
        }

        _logger.Debug("LLM answer length: {Length} character(s).", text.Length);

        if (history is not null)
        {
            history.Add(new ChatTurn("user", trimmed));
            history.Add(new ChatTurn("assistant", text));
        }

        return (text, docs);
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

        var filtered = history.Where(turn => !string.IsNullOrEmpty(turn.Role)).ToList();
        return maxMessages > 0 && filtered.Count > maxMessages
            ? filtered.Skip(filtered.Count - maxMessages).ToList()
            : filtered;
    }
}
