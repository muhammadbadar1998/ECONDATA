using System.Text.Json.Serialization;

namespace EconData.Core.Catalog;

public enum VectorizeProgressKind
{
    BuildStarted,
    BuildCompleted,
    EmbedStarted,
    BatchCompleted,
    Completed
}

public sealed record VectorizeProgress(
    [property: JsonPropertyName("kind")] VectorizeProgressKind Kind,
    [property: JsonPropertyName("total_documents")] int TotalDocuments,
    [property: JsonPropertyName("documents_to_embed")] int DocumentsToEmbed,
    [property: JsonPropertyName("embedded")] int Embedded,
    [property: JsonPropertyName("total_batches")] int TotalBatches,
    [property: JsonPropertyName("current_batch")] int CurrentBatch,
    [property: JsonPropertyName("orphans_removed")] int OrphansRemoved);
