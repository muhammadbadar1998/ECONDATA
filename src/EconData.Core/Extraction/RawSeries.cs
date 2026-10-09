namespace EconData.Core.Extraction;

/// <summary>
/// A raw DBnomics series document. Period and value tokens are kept as text so
/// the transformer can apply the same float-parsing/skip semantics as the Python
/// pipeline (JSON null becomes a <c>null</c> token; non-numeric strings are skipped).
/// </summary>
public sealed record RawSeries(
    IReadOnlyDictionary<string, string> Dimensions,
    IReadOnlyList<string?> Periods,
    IReadOnlyList<string?> Values,
    string? SeriesName);
