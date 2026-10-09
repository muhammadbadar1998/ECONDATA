using System.Globalization;
using System.Text.Json;
using EconData.Core.Data;
using Microsoft.Data.Sqlite;
using Serilog;
using Serilog.Core;

namespace EconData.Core.Catalog;

/// <summary>A single semantic search hit with its metadata and cosine similarity.</summary>
public sealed record SearchHit(string DocKey, string DocType, string Content, JsonElement Metadata, double Similarity);

/// <summary>An existing embedding row (for hash comparison and orphan cleanup).</summary>
public sealed record ExistingDocument(long DocId, string DocHash);

/// <summary>
/// Persists catalog documents into <c>dbe_embeddings</c> and their vectors into a
/// sqlite-vec <c>vec0</c> table, and serves cosine kNN search.
/// </summary>
public sealed class VectorStore
{
    public const int EmbeddingDim = 768;

    private readonly SqliteConnectionFactory _factory;
    private readonly string _vecExtensionPath;
    private readonly ILogger _logger;

    public VectorStore(SqliteConnectionFactory factory, string vecExtensionPath, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentException.ThrowIfNullOrWhiteSpace(vecExtensionPath);
        _factory = factory;
        _vecExtensionPath = vecExtensionPath;
        _logger = logger ?? Logger.None;
    }

    public void EnsureVectorTable()
    {
        using var connection = OpenWithExtension();
        using var command = connection.CreateCommand();
        command.CommandText =
            $"CREATE VIRTUAL TABLE dbe_embeddings_vec USING vec0(doc_id INTEGER PRIMARY KEY, embedding float[{EmbeddingDim}] distance_metric=cosine)";
        try
        {
            command.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            // Table already present.
        }
    }

    public Dictionary<(string DocKey, string DocType), ExistingDocument> LoadExisting()
    {
        var map = new Dictionary<(string, string), ExistingDocument>();
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT doc_id, doc_key, doc_type, doc_hash FROM dbe_embeddings";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            map[(reader.GetString(1), reader.GetString(2))] = new ExistingDocument(reader.GetInt64(0), reader.GetString(3));
        }

        return map;
    }

    public void Upsert(IReadOnlyList<Document> documents, IReadOnlyList<float[]> vectors)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(vectors);
        if (documents.Count != vectors.Count)
        {
            throw new ArgumentException("Document and vector counts must match.", nameof(vectors));
        }

        EnsureVectorTable();
        using var connection = OpenWithExtension();
        using var transaction = connection.BeginTransaction();

        for (var i = 0; i < documents.Count; i++)
        {
            var document = documents[i];
            long docId;
            using (var upsert = connection.CreateCommand())
            {
                upsert.Transaction = transaction;
                upsert.CommandText =
                    "INSERT INTO dbe_embeddings (doc_key, doc_type, content, doc_hash, metadata, embedding_model, updated_at) " +
                    "VALUES (@key, @type, @content, @hash, @metadata, @model, strftime('%Y-%m-%dT%H:%M:%SZ', 'now')) " +
                    "ON CONFLICT (doc_key, doc_type) DO UPDATE SET " +
                    "content = excluded.content, doc_hash = excluded.doc_hash, metadata = excluded.metadata, " +
                    "embedding_model = excluded.embedding_model, updated_at = excluded.updated_at " +
                    "RETURNING doc_id";
                upsert.Parameters.AddWithValue("@key", document.DocKey);
                upsert.Parameters.AddWithValue("@type", document.DocType);
                upsert.Parameters.AddWithValue("@content", document.Content);
                upsert.Parameters.AddWithValue("@hash", document.DocHash);
                upsert.Parameters.AddWithValue("@metadata", document.MetadataJson);
                upsert.Parameters.AddWithValue("@model", document.EmbeddingModel);
                docId = Convert.ToInt64(upsert.ExecuteScalar(), CultureInfo.InvariantCulture);
            }

            using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM dbe_embeddings_vec WHERE doc_id = @id";
                delete.Parameters.AddWithValue("@id", docId);
                delete.ExecuteNonQuery();
            }

            using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO dbe_embeddings_vec (doc_id, embedding) VALUES (@id, @vector)";
                insert.Parameters.AddWithValue("@id", docId);
                insert.Parameters.AddWithValue("@vector", DocumentFormatting.EncodeVector(vectors[i]));
                insert.ExecuteNonQuery();
            }
        }

        transaction.Commit();
        _logger.Information("Upserted {Count} embedding vectors.", documents.Count);
    }

    public void DeleteOrphans(IReadOnlyList<(string DocKey, string DocType, long DocId)> orphans)
    {
        ArgumentNullException.ThrowIfNull(orphans);
        if (orphans.Count == 0)
        {
            return;
        }

        using var connection = OpenWithExtension();
        using var transaction = connection.BeginTransaction();
        foreach (var (_, _, docId) in orphans)
        {
            using (var deleteVector = connection.CreateCommand())
            {
                deleteVector.Transaction = transaction;
                deleteVector.CommandText = "DELETE FROM dbe_embeddings_vec WHERE doc_id = @id";
                deleteVector.Parameters.AddWithValue("@id", docId);
                deleteVector.ExecuteNonQuery();
            }

            using var deleteRow = connection.CreateCommand();
            deleteRow.Transaction = transaction;
            deleteRow.CommandText = "DELETE FROM dbe_embeddings WHERE doc_id = @id";
            deleteRow.Parameters.AddWithValue("@id", docId);
            deleteRow.ExecuteNonQuery();
        }

        transaction.Commit();
        _logger.Information("Removed {Count} orphan embedding rows.", orphans.Count);
    }

    public IReadOnlyList<SearchHit> Search(IReadOnlyList<float> queryVector, int k)
    {
        ArgumentNullException.ThrowIfNull(queryVector);
        if (k < 1)
        {
            return Array.Empty<SearchHit>();
        }

        using var connection = OpenWithExtension();
        using var command = connection.CreateCommand();
        command.CommandText =
            "WITH knn AS (SELECT doc_id, distance FROM dbe_embeddings_vec WHERE embedding MATCH @query AND k = @k) " +
            "SELECT e.doc_key, e.doc_type, e.content, e.metadata, knn.distance " +
            "FROM knn JOIN dbe_embeddings e ON e.doc_id = knn.doc_id ORDER BY knn.distance";
        command.Parameters.AddWithValue("@query", DocumentFormatting.EncodeVector(queryVector));
        command.Parameters.AddWithValue("@k", k);

        var hits = new List<SearchHit>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var metadataText = reader.GetString(3);
            var metadata = metadataText.Length == 0
                ? default
                : JsonDocument.Parse(metadataText).RootElement.Clone();
            // sqlite-vec cosine distance = 1 - cosine similarity.
            hits.Add(new SearchHit(reader.GetString(0), reader.GetString(1), reader.GetString(2), metadata, 1.0 - reader.GetDouble(4)));
        }

        return hits;
    }

    private SqliteConnection OpenWithExtension()
    {
        var connection = _factory.Open();
        connection.LoadExtension(_vecExtensionPath);
        return connection;
    }
}
