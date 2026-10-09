namespace EconData.Core.Ai;

/// <summary>The embedding operations the catalog layer depends on.</summary>
public interface IEmbedder
{
    Task<bool> VerifyConnectivityAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default);

    Task<float[]> EmbedQueryAsync(string text, CancellationToken cancellationToken = default);
}
