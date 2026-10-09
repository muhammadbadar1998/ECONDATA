using EconData.Core.Ai;
using EconData.Core.Rag;

namespace EconData.Core.Cli.Chat;

/// <summary>Arguments for the chat command.</summary>
public sealed record ChatOptions(int K, bool ShowSources, string SystemPromptPath, string Question);

/// <summary>Wires the system prompt, startup checks, and one-shot/REPL chat modes.</summary>
public static class ChatCommand
{
    public static async Task<string?> PreflightAsync(IEmbedder embedder, IChatModel llm, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(embedder);
        ArgumentNullException.ThrowIfNull(llm);

        if (!await embedder.VerifyConnectivityAsync(cancellationToken).ConfigureAwait(false))
        {
            var host = embedder is OllamaEmbedder ollama ? ollama.BaseUrl : "?";
            return $"The embedding service is not reachable (OLLAMA_HOST={host}). " +
                   "Run `ollama serve` and `ollama pull nomic-embed-text`.";
        }

        if (llm is LLMClient client && !await llm.VerifyConnectivityAsync(cancellationToken).ConfigureAwait(false))
        {
            return $"The LLM endpoint {client.BaseUrl} is not reachable. Start the server, or point " +
                   $"LLM_BASE_URL at a running OpenAI-compatible endpoint (currently model '{llm.Model}').";
        }

        return null;
    }

    public static async Task<int> RunAsync(
        ChatOptions options,
        Func<string, IRagAnswerer> answererFactory,
        Func<IRagAnswerer, CancellationToken, Task<string?>> preflight,
        TextReader input,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(answererFactory);
        ArgumentNullException.ThrowIfNull(preflight);

        var systemPrompt = RagAnswerer.DefaultSystemPrompt;
        if (!string.IsNullOrEmpty(options.SystemPromptPath))
        {
            try
            {
                systemPrompt = ChatRepl.ReadSystemPrompt(options.SystemPromptPath);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                await error.WriteLineAsync($"[error] Could not read --system prompt file: {ex.Message}").ConfigureAwait(false);
                return 1;
            }
        }

        var answerer = answererFactory(systemPrompt);

        string? problem;
        try
        {
            problem = await preflight(answerer, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await error.WriteLineAsync($"[error] Startup check failed: {ex.Message}").ConfigureAwait(false);
            return 1;
        }

        if (problem is not null)
        {
            await error.WriteLineAsync($"[error] {problem}").ConfigureAwait(false);
            return 1;
        }

        if (!string.IsNullOrEmpty(options.Question))
        {
            await ChatRepl.HandleTurnAsync(answerer, options.Question, new List<ChatTurn>(), output, options.ShowSources, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        return await ChatRepl.RunAsync(answerer, options.ShowSources, input, output, cancellationToken).ConfigureAwait(false);
    }
}
