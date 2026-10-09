using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EconData.Core.Catalog;

/// <summary>
/// A catalog document destined for <c>dbe_embeddings</c>. The content hash is the
/// SHA-256 of <see cref="Content"/>, so identical content always yields the same
/// hash and re-running vectorization is a no-op.
/// </summary>
public sealed class Document
{
    public string DocKey { get; }
    public string DocType { get; }
    public string Content { get; }
    public IReadOnlyDictionary<string, object?> Metadata { get; }
    public string EmbeddingModel { get; }
    public string DocHash { get; }
    public string MetadataJson { get; }

    public Document(
        string docKey,
        string docType,
        string content,
        IReadOnlyDictionary<string, object?> metadata,
        string embeddingModel = "nomic-embed-text")
    {
        DocKey = docKey;
        DocType = docType;
        Content = content;
        Metadata = metadata;
        EmbeddingModel = embeddingModel;
        DocHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        MetadataJson = JsonSerializer.Serialize(metadata);
    }
}
