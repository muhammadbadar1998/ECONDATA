using System.Text.Json;

namespace EconData.Core.Ai;

/// <summary>The chat-completion operations the RAG layer depends on.</summary>
public interface IChatModel
{
    string Model { get; }

    Task<bool> VerifyConnectivityAsync(CancellationToken cancellationToken = default);

    Task<JsonElement> ChatAsync(IReadOnlyList<ChatTurn> messages, CancellationToken cancellationToken = default);
}
