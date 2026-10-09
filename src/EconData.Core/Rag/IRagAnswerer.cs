using EconData.Core.Ai;
using EconData.Core.Catalog;

namespace EconData.Core.Rag;

/// <summary>The read-only question-answering surface the chat CLI depends on.</summary>
public interface IRagAnswerer
{
    string Model { get; }

    int K { get; }

    Task<(string Text, IReadOnlyList<SearchHit> Docs)> AnswerAsync(
        string question,
        List<ChatTurn>? history = null,
        CancellationToken cancellationToken = default);
}
