using System.Globalization;
using EconData.Core.Data;
using Parquet;
using Parquet.Schema;
using Serilog;

namespace EconData.Core.Lake;

/// <summary>
/// Silver tier: typed, deduped, unit-normalized observations as Parquet, one file per
/// metric. A metric partition is overwritten wholesale on every run, so silver is a pure
/// function of the latest transform output for that metric.
/// </summary>
public sealed class SilverTier
{
    private readonly LakePaths _paths;
    private readonly ILogger _logger;

    public SilverTier(LakePaths paths, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(logger);
        _paths = paths;
        _logger = logger;
    }

    private static readonly DataField<string> MetricCode = new("metric_code");
    private static readonly DataField<string> EntityCode = new("entity_code");
    private static readonly DataField<string> EntityName = new("entity_name");
    private static readonly DataField<string> StartDate = new("start_date");
    private static readonly DataField<string> EndDate = new("end_date");
    private static readonly DataField<double> Val = new("val");
    private static readonly DataField<double> RawVal = new("raw_val", true);
    private static readonly DataField<string> Unit = new("unit", true);
    private static readonly DataField<string> PeriodStr = new("period_str", true);
    private static readonly DataField<string> ExtractedAt = new("extracted_at");

    private static readonly ParquetSchema Schema = new(
        MetricCode, EntityCode, EntityName, StartDate, EndDate,
        Val, RawVal, Unit, PeriodStr, ExtractedAt);

    /// <summary>Overwrites the metric partition with the given observations.</summary>
    public async Task<int> WriteAsync(
        string metricCode,
        IReadOnlyList<ObservationRecord> observations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observations);
        var path = _paths.SilverFile(metricCode);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await using (var stream = File.Create(path))
        {
            await using var writer = await ParquetWriter.CreateAsync(Schema, stream, cancellationToken: cancellationToken);
            using var group = writer.CreateRowGroup();
            var count = observations.Count;

            var metric = new string[count];
            Array.Fill(metric, metricCode);
            await group.WriteAsync(MetricCode, metric);
            await group.WriteAsync(EntityCode, observations.Select(o => o.EntityCode).ToArray());
            await group.WriteAsync(EntityName, observations.Select(o => o.EntityName).ToArray());
            await group.WriteAsync(StartDate, observations.Select(o => o.StartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).ToArray());
            await group.WriteAsync(EndDate, observations.Select(o => o.EndDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).ToArray());
            await group.WriteAsync<double>(Val, observations.Select(o => o.Val).ToArray());
            await group.WriteAsync<double>(RawVal, observations.Select(o => o.RawVal).ToArray());
            await group.WriteAsync(Unit, observations.Select(o => o.Unit).ToArray());
            await group.WriteAsync(PeriodStr, observations.Select(o => o.PeriodStr).ToArray());
            await group.WriteAsync(ExtractedAt, observations
                .Select(o => o.ExtractedAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture))
                .ToArray());
        }

        _logger.Information("Silver: wrote {Count} observations for '{Metric}' to {Path}.", observations.Count, metricCode, path);
        return observations.Count;
    }

    public bool HasPartition(string metricCode) => File.Exists(_paths.SilverFile(metricCode));

    /// <summary>Reads a metric partition back for the gold load.</summary>
    public async Task<IReadOnlyList<ObservationRecord>> ReadAsync(string metricCode, CancellationToken cancellationToken = default)
    {
        var path = _paths.SilverFile(metricCode);
        if (!File.Exists(path))
        {
            return Array.Empty<ObservationRecord>();
        }

        await using var stream = File.OpenRead(path);
        await using var reader = await ParquetReader.CreateAsync(stream, cancellationToken: cancellationToken);
        var rows = new List<ObservationRecord>();
        for (var i = 0; i < reader.RowGroupCount; i++)
        {
            using var group = reader.OpenRowGroupReader(i);
            var n = (int)group.RowCount;
            var entityCode = new string[n];
            var entityName = new string[n];
            var start = new string[n];
            var end = new string[n];
            var val = new double[n];
            var rawVal = new double?[n];
            var unit = new string?[n];
            var period = new string?[n];
            var extracted = new string[n];
            await group.ReadAsync(EntityCode, entityCode, cancellationToken: cancellationToken);
            await group.ReadAsync(EntityName, entityName, cancellationToken: cancellationToken);
            await group.ReadAsync(StartDate, start, cancellationToken: cancellationToken);
            await group.ReadAsync(EndDate, end, cancellationToken: cancellationToken);
            await group.ReadAsync<double>(Val, val, cancellationToken: cancellationToken);
            await group.ReadAsync<double>(RawVal, rawVal, cancellationToken: cancellationToken);
            await group.ReadAsync(Unit, unit, cancellationToken: cancellationToken);
            await group.ReadAsync(PeriodStr, period, cancellationToken: cancellationToken);
            await group.ReadAsync(ExtractedAt, extracted, cancellationToken: cancellationToken);

            for (var r = 0; r < n; r++)
            {
                rows.Add(new ObservationRecord(
                    entityCode[r],
                    entityName[r],
                    DateOnly.ParseExact(start[r], "yyyy-MM-dd", CultureInfo.InvariantCulture),
                    DateOnly.ParseExact(end[r], "yyyy-MM-dd", CultureInfo.InvariantCulture),
                    val[r],
                    rawVal[r],
                    unit[r] ?? string.Empty,
                    period[r] ?? string.Empty,
                    DateTimeOffset.Parse(extracted[r], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal)));
            }
        }

        return rows;
    }

    public IReadOnlyList<string> Partitions()
    {
        if (!Directory.Exists(_paths.SilverDir))
        {
            return Array.Empty<string>();
        }

        return Directory.GetFiles(_paths.SilverDir, "*.parquet")
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Row count per metric partition, for the lake status page.</summary>
    public async Task<IReadOnlyDictionary<string, long>> RowCountsAsync(CancellationToken cancellationToken = default)
    {
        var counts = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var metric in Partitions())
        {
            await using var stream = File.OpenRead(_paths.SilverFile(metric));
            await using var reader = await ParquetReader.CreateAsync(stream, cancellationToken: cancellationToken);
            long total = 0;
            for (var i = 0; i < reader.RowGroupCount; i++)
            {
                using var group = reader.OpenRowGroupReader(i);
                total += group.RowCount;
            }

            counts[metric] = total;
        }

        return counts;
    }
}
