using System.Text.Json;
using EconData.Core.Ai;
using EconData.Core.Catalog;
using EconData.Core.Rag;

namespace EconData.Tests;

public class RagAnswererTests
{
    private const string UsaGdpContent =
        "United States\nGross Domestic Product (Current USD)\n" +
        "Metric code: gdp_usd\nFrequency: Annual Unit: Current US$\n" +
        "Observations: 65, range 1960–2024\nLatest: 27,369,400,000,000 (2024)";

    private static SearchHit DocUsaGdp() => new("USA|gdp_usd", "entity_metric", UsaGdpContent, Json("{}"), 0.81);

    private static SearchHit DocMetric() => new("gdp_growth", "metric", "GDP Growth Rate (Annual %)", Json("{}"), 0.62);

    [Fact]
    public async Task RetrieveReturnsTopKDocuments()
    {
        var searcher = new FakeSearcher();
        var answerer = new RagAnswerer(searcher, new FakeChat(), k: 1);

        var docs = await answerer.RetrieveAsync("latest US GDP");

        Assert.Equal(new[] { "USA|gdp_usd" }, docs.Select(doc => doc.DocKey));
        Assert.Equal(new[] { ("latest US GDP", 1) }, searcher.Calls);
    }

    [Fact]
    public async Task RetrieveExplicitKOverridesDefault()
    {
        var searcher = new FakeSearcher();
        var answerer = new RagAnswerer(searcher, new FakeChat(), k: 8);

        await answerer.RetrieveAsync("gdp", k: 1);

        Assert.Equal(1, searcher.Calls[0].K);
    }

    [Fact]
    public async Task RetrieveBlankQuestionShortCircuits()
    {
        var searcher = new FakeSearcher();
        var answerer = new RagAnswerer(searcher, new FakeChat());

        Assert.Empty(await answerer.RetrieveAsync("   "));
        Assert.Empty(searcher.Calls);
    }

    [Fact]
    public void BuildMessagesStructure()
    {
        var answerer = new RagAnswerer(new FakeSearcher(), new FakeChat());
        var messages = answerer.BuildMessages("What is the latest US GDP?", new[] { DocUsaGdp(), DocMetric() });

        Assert.Equal(new[] { "system", "user", "user" }, messages.Select(message => message.Role));
        Assert.Equal(answerer.SystemPrompt, messages[0].Content);
        Assert.Contains("CONTEXT DOCUMENTS", messages[1].Content);
        Assert.Contains("[1] doc_key=USA|gdp_usd doc_type=entity_metric", messages[1].Content);
        Assert.Contains("Latest: 27,369,400,000,000 (2024)", messages[1].Content);
        Assert.Contains("[2] doc_key=gdp_growth doc_type=metric", messages[1].Content);
        Assert.Equal("What is the latest US GDP?", messages[^1].Content);
    }

    [Fact]
    public void SystemPromptStatesGroundingRules()
    {
        var prompt = new RagAnswerer(new FakeSearcher(), new FakeChat()).SystemPrompt.ToLowerInvariant();
        Assert.Contains("only from the context", prompt);
        Assert.Contains("never invent", prompt);
        Assert.Contains("doc_key", prompt);
        Assert.Contains("not available", prompt);
    }

    [Fact]
    public void CustomSystemPromptIsUsed()
    {
        var answerer = new RagAnswerer(new FakeSearcher(), new FakeChat(), systemPrompt: "Custom prompt.");
        var messages = answerer.BuildMessages("q", new[] { DocUsaGdp() });
        Assert.Equal("Custom prompt.", messages[0].Content);
    }

    [Fact]
    public void BuildMessagesIncludesHistoryInOrder()
    {
        var history = new List<ChatTurn>
        {
            new("user", "hi"),
            new("assistant", "hello"),
        };
        var messages = new RagAnswerer(new FakeSearcher(), new FakeChat()).BuildMessages("and now?", new[] { DocUsaGdp() }, history);

        Assert.Equal(new[] { "system", "user", "user", "assistant", "user" }, messages.Select(message => message.Role));
        Assert.Equal("hi", messages[2].Content);
        Assert.Equal("hello", messages[3].Content);
        Assert.Equal("and now?", messages[4].Content);
    }

    [Fact]
    public void BuildMessagesCapsHistory()
    {
        var history = new List<ChatTurn>();
        for (var i = 0; i < 40; i++)
        {
            history.Add(new ChatTurn(i % 2 == 0 ? "user" : "assistant", $"turn {i}"));
        }

        var messages = new RagAnswerer(new FakeSearcher(), new FakeChat()).BuildMessages("q", new[] { DocUsaGdp() }, history);

        Assert.Equal(2 + 20 + 1, messages.Count);
        Assert.Equal("turn 39", messages[^2].Content);
    }

    [Fact]
    public async Task AnswerReturnsTextAndDocs()
    {
        var chat = new FakeChat();
        var answerer = new RagAnswerer(new FakeSearcher(), chat);

        var (text, docs) = await answerer.AnswerAsync("What is the latest US GDP?");

        Assert.Contains("[USA|gdp_usd]", text);
        Assert.Equal(new[] { "USA|gdp_usd", "gdp_growth" }, docs.Select(doc => doc.DocKey));
        Assert.Single(chat.Messages);
    }

    [Fact]
    public async Task AnswerPromptContainsRetrievedDocs()
    {
        var chat = new FakeChat();
        await new RagAnswerer(new FakeSearcher(), chat).AnswerAsync("gdp?");

        var sent = chat.Messages[0];
        Assert.Equal("system", sent[0].Role);
        Assert.Contains("USA|gdp_usd", sent[1].Content);
    }

    [Fact]
    public async Task AnswerAppendsExchangeToHistory()
    {
        var history = new List<ChatTurn>();
        await new RagAnswerer(new FakeSearcher(), new FakeChat()).AnswerAsync("first question", history);
        Assert.Equal(new[] { "user", "assistant" }, history.Select(turn => turn.Role));
        Assert.Equal("first question", history[0].Content);
    }

    [Fact]
    public async Task AnswerWithoutHistoryLeavesItUntouched()
    {
        var history = new List<ChatTurn>();
        await new RagAnswerer(new FakeSearcher(), new FakeChat()).AnswerAsync("q");
        await new RagAnswerer(new FakeSearcher(), new FakeChat()).AnswerAsync("q2", history);
        Assert.Equal(2, history.Count);
    }

    [Fact]
    public async Task EmptyRetrievalDoesNotCallLlm()
    {
        var chat = new FakeChat();
        var answerer = new RagAnswerer(new FakeSearcher(Array.Empty<SearchHit>()), chat);

        var (text, docs) = await answerer.AnswerAsync("what is the weather in Paris?");

        Assert.Equal(RagAnswerer.NoContextAnswer, text);
        Assert.Empty(docs);
        Assert.Empty(chat.Messages);
    }

    [Fact]
    public async Task BlankQuestionIsRejectedWithoutLlmCall()
    {
        var chat = new FakeChat();
        var (text, docs) = await new RagAnswerer(new FakeSearcher(), chat).AnswerAsync("   ");
        Assert.Equal(string.Empty, text);
        Assert.Empty(docs);
        Assert.Empty(chat.Messages);
    }

    [Fact]
    public async Task LlmErrorPropagates()
    {
        await Assert.ThrowsAsync<LLMException>(() => new RagAnswerer(new FakeSearcher(), new ThrowingChat()).AnswerAsync("q"));
    }

    [Fact]
    public async Task EmptyLlmContentGetsFallbackText()
    {
        var (text, _) = await new RagAnswerer(new FakeSearcher(), new FakeChat(content: string.Empty)).AnswerAsync("q");
        Assert.Contains("empty answer", text);
    }

    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private sealed class FakeSearcher : IDocumentSearcher
    {
        private readonly IReadOnlyList<SearchHit> _docs;

        public FakeSearcher(IReadOnlyList<SearchHit>? docs = null)
            => _docs = docs ?? new[] { DocUsaGdp(), DocMetric() };

        public List<(string Query, int K)> Calls { get; } = new();

        public Task<IReadOnlyList<SearchHit>> SearchDocumentsAsync(string query, int k = 10, CancellationToken cancellationToken = default)
        {
            Calls.Add((query, k));
            return Task.FromResult<IReadOnlyList<SearchHit>>(_docs.Take(k).ToList());
        }
    }

    private sealed class FakeChat : IChatModel
    {
        private readonly string _content;

        public FakeChat(string content = "US GDP was 27,369,400,000,000 in 2024 [USA|gdp_usd].")
            => _content = content;

        public string Model => "fake-model";

        public List<IReadOnlyList<ChatTurn>> Messages { get; } = new();

        public Task<bool> VerifyConnectivityAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<JsonElement> ChatAsync(IReadOnlyList<ChatTurn> messages, CancellationToken cancellationToken = default)
        {
            Messages.Add(messages);
            return Task.FromResult(Json("{\"role\":\"assistant\",\"content\":" + JsonSerializer.Serialize(_content) + "}"));
        }

        public Task<JsonElement> ChatAsync(IReadOnlyList<ChatTurn> messages, JsonElement? tools, CancellationToken cancellationToken = default)
            => ChatAsync(messages, cancellationToken);
    }

    private sealed class ThrowingChat : IChatModel
    {
        public string Model => "boom";

        public Task<bool> VerifyConnectivityAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task<JsonElement> ChatAsync(IReadOnlyList<ChatTurn> messages, CancellationToken cancellationToken = default)
            => throw new LLMException("endpoint down");

        public Task<JsonElement> ChatAsync(IReadOnlyList<ChatTurn> messages, JsonElement? tools, CancellationToken cancellationToken = default)
            => ChatAsync(messages, cancellationToken);
    }
}
