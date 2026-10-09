using System.Text.Json;
using EconData.Core.Ai;
using EconData.Core.Catalog;
using EconData.Core.Cli.Chat;
using EconData.Core.Rag;

namespace EconData.Tests;

public class ChatReplTests
{
    private static SearchHit Doc() => new(
        "USA|gdp_usd",
        "entity_metric",
        "United States ... Latest: 27,369,400,000,000 (2024)",
        Json("{}"),
        0.8123);

    [Fact]
    public void FormatSourcesIncludesKeyTypeAndSimilarity()
    {
        var text = ChatRepl.FormatSources(new[] { Doc() });
        Assert.Contains("USA|gdp_usd", text);
        Assert.Contains("entity_metric", text);
        Assert.Contains("0.8123", text);
    }

    [Fact]
    public void FormatSourcesEmpty()
    {
        Assert.Equal("  (no sources retrieved)", ChatRepl.FormatSources(Array.Empty<SearchHit>()));
    }

    [Fact]
    public async Task ReplAnswersAQuestion()
    {
        var answerer = new FakeAnswerer();
        var (code, output) = await RunRepl(answerer, "What is the latest US GDP?\n");
        Assert.Equal(0, code);
        Assert.Contains("answer to What is the latest US GDP?", output);
        Assert.Equal(new[] { "What is the latest US GDP?" }, answerer.Questions);
    }

    [Theory]
    [InlineData("exit\n")]
    [InlineData("quit\n")]
    public async Task ReplExitsOnExitCommands(string input)
    {
        var answerer = new FakeAnswerer();
        var (code, output) = await RunRepl(answerer, input);
        Assert.Equal(0, code);
        Assert.Contains("bye", output);
        Assert.Empty(answerer.Questions);
    }

    [Fact]
    public async Task ReplExitsOnEof()
    {
        var answerer = new FakeAnswerer();
        var (code, _) = await RunRepl(answerer, string.Empty);
        Assert.Equal(0, code);
        Assert.Empty(answerer.Questions);
    }

    [Fact]
    public async Task ReplResetsHistory()
    {
        var answerer = new FakeAnswerer();
        var (code, output) = await RunRepl(answerer, "first\nreset\nsecond\nexit\n");
        Assert.Equal(0, code);
        Assert.Contains("history cleared", output);
        Assert.Equal(new[] { "first", "second" }, answerer.Questions);
        Assert.Empty(answerer.Histories[0]!);
        Assert.Empty(answerer.Histories[1]!);
    }

    [Fact]
    public async Task ReplCarriesHistoryBetweenTurns()
    {
        var answerer = new FakeAnswerer();
        await RunRepl(answerer, "first\nsecond\nexit\n");
        Assert.Empty(answerer.Histories[0]!);
        Assert.Equal(2, answerer.Histories[1]!.Count);
        Assert.Equal("first", answerer.Histories[1]![0].Content);
        Assert.Equal("answer to first", answerer.Histories[1]![1].Content);
    }

    [Fact]
    public async Task ReplIgnoresBlankLines()
    {
        var answerer = new FakeAnswerer();
        await RunRepl(answerer, "\n   \nreal question\nexit\n");
        Assert.Equal(new[] { "real question" }, answerer.Questions);
    }

    [Fact]
    public async Task ReplShowSourcesFlag()
    {
        var (_, output) = await RunRepl(new FakeAnswerer(), "gdp\nexit\n", showSources: true);
        Assert.Contains("Sources:", output);
        Assert.Contains("USA|gdp_usd", output);
        Assert.Contains("0.8123", output);
    }

    [Fact]
    public async Task SourcesCommandPrintsLastSources()
    {
        var (_, output) = await RunRepl(new FakeAnswerer(), "gdp\nsources\nexit\n");
        Assert.Contains("Sources:", output);
        Assert.Contains("USA|gdp_usd (entity_metric) similarity=0.8123", output);
    }

    [Fact]
    public async Task HelpCommandListsCommands()
    {
        var (_, output) = await RunRepl(new FakeAnswerer(), "help\nexit\n");
        Assert.Contains("reset", output);
        Assert.Contains("exit", output);
    }

    [Fact]
    public async Task LlmErrorPrintsSingleLineWithoutTraceback()
    {
        var answerer = new FakeAnswerer(error: new LLMException("endpoint at http://x is down"));
        var (code, output) = await RunRepl(answerer, "gdp\nstill alive?\nexit\n");
        Assert.Equal(0, code);
        Assert.Contains("[error] endpoint at http://x is down", output);
        Assert.DoesNotContain("Traceback", output);
        Assert.DoesNotContain("at EconData", output);
        Assert.Equal(new[] { "gdp", "still alive?" }, answerer.Questions);
    }

    [Fact]
    public async Task UnexpectedErrorPrintsTypeName()
    {
        var answerer = new FakeAnswerer(error: new InvalidOperationException("bad payload"));
        var (_, output) = await RunRepl(answerer, "gdp\nexit\n");
        Assert.Contains("[error] InvalidOperationException: bad payload", output);
    }

    [Fact]
    public void ReadSystemPromptReadsAndRejectsEmpty()
    {
        var path = Path.Combine(Path.GetTempPath(), "econdata-prompt-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            File.WriteAllText(path, "Be terse.\n");
            Assert.Equal("Be terse.", ChatRepl.ReadSystemPrompt(path));

            File.WriteAllText(path, "   \n");
            Assert.Throws<InvalidOperationException>(() => ChatRepl.ReadSystemPrompt(path));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task ChatCommandFailsOnUnreadableSystemFile()
    {
        var missing = Path.Combine(Path.GetTempPath(), "econdata-missing-" + Guid.NewGuid().ToString("N") + ".txt");
        var code = await ChatCommand.RunAsync(
            new ChatOptions(8, false, missing, string.Empty),
            _ => new FakeAnswerer(),
            (_, _) => Task.FromResult<string?>(null),
            new StringReader(string.Empty),
            new StringWriter(),
            new StringWriter(),
            CancellationToken.None);
        Assert.Equal(1, code);
    }

    [Fact]
    public async Task ChatCommandQuestionModeAnswersOnce()
    {
        var output = new StringWriter();
        var answerer = new FakeAnswerer();
        var code = await ChatCommand.RunAsync(
            new ChatOptions(8, true, string.Empty, "latest US GDP"),
            _ => answerer,
            (_, _) => Task.FromResult<string?>(null),
            new StringReader(string.Empty),
            output,
            new StringWriter(),
            CancellationToken.None);

        Assert.Equal(0, code);
        Assert.Contains("answer to latest US GDP", output.ToString());
        Assert.Contains("USA|gdp_usd", output.ToString());
    }

    [Fact]
    public async Task ChatCommandExitsNonZeroWhenPreflightFails()
    {
        var error = new StringWriter();
        var code = await ChatCommand.RunAsync(
            new ChatOptions(8, false, string.Empty, string.Empty),
            _ => new FakeAnswerer(),
            (_, _) => Task.FromResult<string?>("The LLM endpoint is not reachable."),
            new StringReader(string.Empty),
            new StringWriter(),
            error,
            CancellationToken.None);

        Assert.Equal(1, code);
        Assert.Contains("not reachable", error.ToString());
    }

    private static async Task<(int Code, string Output)> RunRepl(FakeAnswerer answerer, string input, bool showSources = false)
    {
        var output = new StringWriter();
        var code = await ChatRepl.RunAsync(answerer, showSources, new StringReader(input), output, CancellationToken.None);
        return (code, output.ToString());
    }

    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private sealed class FakeAnswerer : IRagAnswerer
    {
        private readonly IReadOnlyList<SearchHit> _docs;
        private readonly Exception? _error;
        private readonly Queue<string> _answers;

        public FakeAnswerer(IReadOnlyList<SearchHit>? docs = null, Exception? error = null, IEnumerable<string>? answers = null)
        {
            _docs = docs ?? new[] { Doc() };
            _error = error;
            _answers = new Queue<string>(answers ?? Array.Empty<string>());
        }

        public string Model => "fake-model";

        public int K => 8;

        public List<string> Questions { get; } = new();

        public List<List<ChatTurn>?> Histories { get; } = new();

        public Task<(string Text, IReadOnlyList<SearchHit> Docs)> AnswerAsync(string question, List<ChatTurn>? history = null, CancellationToken cancellationToken = default)
        {
            Questions.Add(question);
            Histories.Add(history is null ? null : new List<ChatTurn>(history));
            if (_error is not null)
            {
                throw _error;
            }

            var text = _answers.Count > 0 ? _answers.Dequeue() : $"answer to {question}";
            if (history is not null)
            {
                history.Add(new ChatTurn("user", question));
                history.Add(new ChatTurn("assistant", text));
            }

            return Task.FromResult<(string, IReadOnlyList<SearchHit>)>((text, _docs));
        }
    }
}
