namespace EconData.Core.Catalog;

/// <summary>Semantic document search over the embedded catalog (implemented by <see cref="Vectorizer"/>).</summary>
public interface IDocumentSearcher
{
    Task<IReadOnlyList<SearchHit>> SearchDocumentsAsync(string query, int k = 10, CancellationToken cancellationToken = default);
}
