namespace EconData.Core.Data;

/// <summary>A metric registry record, built from the catalog for the <c>dbe_metrics</c>/<c>dbe_sources</c> tables.</summary>
public sealed record MetricRecord(
    string MetricCode,
    string MetricName,
    string Category,
    string Frequency,
    string BaseUnit,
    string UnitNormalization,
    string Description,
    bool IsAggregatable,
    string AggregationMethod,
    IReadOnlyList<string> Synonyms,
    string Provider,
    string Dataset,
    string IndicatorCode,
    string DimensionKey,
    string EntityScope);

/// <summary>A normalized long-format observation destined for <c>dbe_observations</c>.</summary>
public sealed record ObservationRecord(
    string EntityCode,
    string EntityName,
    DateOnly StartDate,
    DateOnly EndDate,
    double Val,
    double? RawVal,
    string Unit,
    string PeriodStr,
    DateTimeOffset ExtractedAt);
