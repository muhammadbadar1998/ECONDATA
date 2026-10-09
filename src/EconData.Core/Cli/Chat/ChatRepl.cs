using EconData.Core.Ai;
using EconData.Core.Catalog;
using EconData.Core.Rag;

namespace EconData.Core.Cli.Chat;

/// <summary>Interactive read-only REPL helpers for the catalog assistant.</summary>
public static class ChatRepl
{
    public const string Banner =
        "EconData catalog assistant (vector RAG over dbe_embeddings)\n" +
        "Type a question and press Enter. Commands: help, sources, reset, exit";

    private static readonly HashSet<string> ExitCommands = new(StringComparer.Ordinal)
    {
        "exit", "quit", ":q", "/exit", "/quit",
    };

    public static string FormatSources(IReadOnlyList<SearchHit> docs)
    {
        ArgumentNullException.ThrowIfNull(docs);
        if (docs.Count == 0)
        {
            return "  (no sources retrieved)";
        }

        var lines = docs.Select((doc, index) =>
            $"  [{index + 1}] {doc.DocKey} ({doc.DocType}) similarity={doc.Similarity:F4}");
        return string.Join("\n", lines);
    }

    public static string ReadSystemPrompt(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var text = File.ReadAllText(path).Trim();
        if (text.Length == 0)
        {
            throw new InvalidOperationException($"System prompt file '{path}' is empty.");
        }

        return text;
    }

    /// <summary>Answer one question, print it, and record it in <paramref name="history"/>. Errors print as a single line.</summary>
    public static async Task<(string? Text, IReadOnlyList<SearchHit> Docs)> HandleTurnAsync(
        IRagAnswerer answerer,
        string question,
        List<ChatTurn> history,
        TextWriter output,
        bool showSources = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(answerer);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(output);

        string text;
        IReadOnlyList<SearchHit> docs;
        try
        {
            (text, docs) = await answerer.AnswerAsync(question, history, cancellationToken).ConfigureAwait(false);
        }
        catch (LLMException ex)
        {
            await output.WriteLineAsync($"[error] {ex.Message}").ConfigureAwait(false);
            return (null, Array.Empty<SearchHit>());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await output.WriteLineAsync($"[error] {ex.GetType().Name}: {ex.Message}").ConfigureAwait(false);
            return (null, Array.Empty<SearchHit>());
        }

        await output.WriteLineAsync(text).ConfigureAwait(false);
        if (showSources)
        {
            await output.WriteLineAsync().ConfigureAwait(false);
            await output.WriteLineAsync("Sources:").ConfigureAwait(false);
            await output.WriteLineAsync(FormatSources(docs)).ConfigureAwait(false);
        }

        return (text, docs);
    }

    public static async Task<int> RunAsync(
        IRagAnswerer answerer,
        bool showSources,
        TextReader input,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(answerer);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        var history = new List<ChatTurn>();
        IReadOnlyList<SearchHit> lastDocs = Array.Empty<SearchHit>();

        await output.WriteLineAsync(Banner).ConfigureAwait(false);
        await output.WriteLineAsync($"model={answerer.Model}  top_k={answerer.K}").ConfigureAwait(false);
        await output.WriteLineAsync().ConfigureAwait(false);

        while (true)
        {
            try
            {
                await output.WriteAsync("you> ").ConfigureAwait(false);
                await output.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ObjectDisposedException or IOException)
            {
                break;
            }

            var line = await input.ReadLineAsync().ConfigureAwait(false);
            if (line is null)
            {
                await output.WriteLineAsync().ConfigureAwait(false);
                break;
            }

            var question = line.Trim();
            if (question.Length == 0)
            {
                continue;
            }

            var lowered = question.ToLowerInvariant();
            if (ExitCommands.Contains(lowered))
            {
                await output.WriteLineAsync("bye").ConfigureAwait(false);
                break;
            }

            if (lowered == "help")
            {
                await output.WriteLineAsync("Commands: help, sources (last answer), reset (clear history), exit/quit").ConfigureAwait(false);
                continue;
            }

            if (lowered == "reset")
            {
                history.Clear();
                await output.WriteLineAsync("conversation history cleared").ConfigureAwait(false);
                continue;
            }

            if (lowered == "sources")
            {
                await output.WriteLineAsync("Sources:").ConfigureAwait(false);
                await output.WriteLineAsync(FormatSources(lastDocs)).ConfigureAwait(false);
                continue;
            }

            var before = history.Count;
            var (text, docs) = await HandleTurnAsync(answerer, question, history, output, showSources, cancellationToken).ConfigureAwait(false);
            if (text is not null)
            {
                lastDocs = docs;
            }
            else if (before != history.Count)
            {
                history.RemoveRange(before, history.Count - before);
            }
        }

        return 0;
    }
}
