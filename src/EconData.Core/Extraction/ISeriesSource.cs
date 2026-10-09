using EconData.Core.Configuration;

namespace EconData.Core.Extraction;

/// <summary>The series-fetching seam the pipeline runner depends on (implemented by <see cref="DBnomicsExtractor"/>).</summary>
public interface ISeriesSource
{
    Task<IReadOnlyList<RawSeries>> FetchMetricSeriesAsync(
        string metricCode,
        MetricsCatalog catalog,
        int limit = 1000,
        CancellationToken cancellationToken = default);
}
