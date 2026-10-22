using EconData.Core.Configuration;
using EconData.Core.Data;

namespace EconData.Core.Aggregation;

/// <summary>Seam for the derived-aggregation step, enabling deterministic failure injection in tests.</summary>
public interface IDerivedAggregator
{
    DerivedResult Aggregate(
        IReadOnlyList<string> metricCodes,
        IReadOnlyList<MetricRecord> metrics,
        IReadOnlyDictionary<string, int> metricIds,
        CancellationToken cancellationToken = default);
}
