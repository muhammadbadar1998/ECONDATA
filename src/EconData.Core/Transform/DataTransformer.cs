using EconData.Core.Configuration;
using EconData.Core.Data;

namespace EconData.Core.Transform;

/// <summary>Transforms catalog/raw inputs into normalized records. (Series transform lands in M4.)</summary>
public static class DataTransformer
{
    /// <summary>Converts the metric catalog into registry records including synonyms and source fields.</summary>
    public static IReadOnlyList<MetricRecord> CreateMetricsRecords(MetricsCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var records = new List<MetricRecord>(catalog.ByCode.Count);
        foreach (var (code, info) in catalog.ByCode)
        {
            records.Add(new MetricRecord(
                MetricCode: code,
                MetricName: string.IsNullOrEmpty(info.MetricName) ? code : info.MetricName,
                Category: info.Category,
                Frequency: info.Frequency,
                BaseUnit: info.EffectiveBaseUnit,
                UnitNormalization: info.UnitNormalization,
                Description: info.Description,
                IsAggregatable: string.Equals(info.EntityScope, "aggregate", StringComparison.Ordinal),
                AggregationMethod: info.Aggregation,
                Synonyms: info.Synonyms,
                Provider: info.Provider,
                Dataset: info.Dataset,
                IndicatorCode: info.IndicatorCode,
                DimensionKey: info.DimensionKey,
                EntityScope: info.EntityScope));
        }

        return records;
    }
}
