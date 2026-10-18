using System.Text.Json;

namespace EconData.Core.Ai;

/// <summary>
/// One chat message (OpenAI wire format). <see cref="ToolCalls"/> carries an assistant
/// turn's tool calls and <see cref="ToolCallId"/> links a tool result to its call.
/// </summary>
public sealed record ChatTurn(
    string Role,
    string Content,
    string? ToolCallId = null,
    JsonElement? ToolCalls = null);

/// <summary>Raised when Ollama embedding fails after validation and retries.</summary>
public sealed class EmbeddingException : Exception
{
    public EmbeddingException(string message)
        : base(message)
    {
    }
}

/// <summary>Raised for any chat-completion failure, with an actionable message.</summary>
public sealed class LLMException : Exception
{
    public LLMException(string message)
        : base(message)
    {
    }
}
