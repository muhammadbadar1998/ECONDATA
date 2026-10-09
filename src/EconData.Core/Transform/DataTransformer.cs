using System.Globalization;
using EconData.Core.Configuration;
using EconData.Core.Data;
using EconData.Core.Entities;
using EconData.Core.Extraction;
using Serilog;

namespace EconData.Core.Transform;

/// <summary>Transforms raw DBnomics series into normalized long-format observation records.</summary>
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

    /// <summary>
    /// Transforms raw series into normalized observation records: resolves entities,
    /// parses periods, applies catalog year bounds and unit normalization, skips
    /// unparseable rows, and dedupes on (entity_code, start_date) keeping the last.
    /// </summary>
    public static IReadOnlyList<ObservationRecord> TransformSeriesList(
        string metricCode,
        IReadOnlyList<RawSeries> rawSeriesList,
        MetricsCatalog catalog,
        EntityResolver? entityResolver = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(rawSeriesList);
        ArgumentNullException.ThrowIfNull(catalog);

        var log = logger ?? Serilog.Log.Logger;
        var metric = catalog.Get(metricCode);
        var provider = metric?.Provider ?? string.Empty;
        var baseUnit = metric?.EffectiveBaseUnit ?? string.Empty;
        var unitNormalization = metric?.UnitNormalization ?? string.Empty;
        var startYear = metric?.StartYear;
        var endYear = metric?.EndYear;
        var extractedAt = DateTimeOffset.UtcNow;

        var records = new List<ObservationRecord>();
        var skipped = 0;
        log.Debug("Transforming {Series} raw series for '{Metric}'...", rawSeriesList.Count, metricCode);

        foreach (var series in rawSeriesList)
        {
            var providerCode = (PickDimension(series.Dimensions, "country", "ref_area", "weo-country-code", "LOCATION", "location") ?? "UNKNOWN")
                .ToUpperInvariant();

            string entityCode;
            string entityName;
            if (entityResolver is not null)
            {
                var resolved = entityResolver.ResolveCode(provider, providerCode);
                entityCode = resolved ?? entityResolver.RegisterUnknown(
                    provider,
                    providerCode,
                    string.IsNullOrEmpty(series.SeriesName) ? null : series.SeriesName);
                entityName = entityResolver.EntityName(entityCode);
            }
            else
            {
                entityCode = providerCode;
                entityName = providerCode;
            }

            var count = Math.Min(series.Periods.Count, series.Values.Count);
            for (var i = 0; i < count; i++)
            {
                var valueToken = series.Values[i];
                if (valueToken is null)
                {
                    continue;
                }

                if (!double.TryParse(valueToken, NumberStyles.Float, CultureInfo.InvariantCulture, out var rawVal) || double.IsNaN(rawVal))
                {
                    skipped++;
                    continue;
                }

                var periodStr = (series.Periods[i] ?? string.Empty).Trim();
                var period = PeriodParser.Parse(periodStr);
                if (!period.IsValid || period.Start is null || period.End is null)
                {
                    skipped++;
                    continue;
                }

                var startDate = period.Start.Value;
                var endDate = period.End.Value;
                if (startYear is not null && startDate.Year < startYear)
                {
                    continue;
                }

                if (endYear is not null && startDate.Year > endYear)
                {
                    continue;
                }

                double val;
                try
                {
                    val = UnitNormalizer.Normalize(rawVal, unitNormalization);
                }
                catch (UnitNormalizationException ex)
                {
                    log.Warning("Skipping row for {Metric} {Entity} {Period}: {Message}", metricCode, entityCode, periodStr, ex.Message);
                    skipped++;
                    continue;
                }

                records.Add(new ObservationRecord(entityCode, entityName, startDate, endDate, val, rawVal, baseUnit, periodStr, extractedAt));
            }
        }

        var deduped = new Dictionary<(string EntityCode, DateOnly StartDate), ObservationRecord>();
        foreach (var record in records)
        {
            deduped[(record.EntityCode, record.StartDate)] = record;
        }

        var final = deduped.Values.ToList();
        if (skipped > 0)
        {
            log.Information("Skipped {Skipped} non-numeric/unparseable rows for '{Metric}'.", skipped, metricCode);
        }

        if (final.Count != records.Count)
        {
            log.Information("Deduped {Dupes} duplicate rows for '{Metric}'.", records.Count - final.Count, metricCode);
        }

        log.Debug(
            "'{Metric}' transform complete: {Kept} rows kept, {Skipped} skipped from {Series} series.",
            metricCode,
            final.Count,
            skipped,
            rawSeriesList.Count);
        return final;
    }

    private static string? PickDimension(IReadOnlyDictionary<string, string> dimensions, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (dimensions.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value))
            {
                return value;
            }
        }

        return null;
    }
}
