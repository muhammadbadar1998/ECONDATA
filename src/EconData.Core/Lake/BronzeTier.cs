using System.Text.Json;
using EconData.Core.Configuration;
using EconData.Core.Extraction;
using EconData.Core.Transform;
using Parquet;
using Parquet.Schema;
using Serilog;

namespace EconData.Core.Lake;

/// <summary>
/// Bronze tier: raw provider payloads as Parquet, append-only. One file per metric per run,
/// never rewritten, so a rebuild can always re-derive silver from what was actually fetched.
/// </summary>
public sealed class BronzeTier
{
    private readonly LakePaths _paths;
    private readonly ILogger _logger;

    public BronzeTier(LakePaths paths, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(logger);
        _paths = paths;
        _logger = logger;
    }

    private static readonly DataField<string> MetricCode = new("metric_code");
    private static readonly DataField<string> Provider = new("provider");
    private static readonly DataField<string> Dataset = new("dataset");
    private static readonly DataField<string> IndicatorCode = new("indicator_code");
    private static readonly DataField<string> SeriesName = new("series_name", true);
    private static readonly DataField<string> DimensionsJson = new("dimensions_json");
    private static readonly DataField<string> PeriodsJson = new("periods_json");
    private static readonly DataField<string> ValuesJson = new("values_json");
    private static readonly DataField<string> RunIdField = new("run_id");
    private static readonly DataField<string> FetchedAt = new("fetched_at");

    private static readonly ParquetSchema Schema = new(
        MetricCode, Provider, Dataset, IndicatorCode, SeriesName,
        DimensionsJson, PeriodsJson, ValuesJson, RunIdField, FetchedAt);

    /// <summary>Appends one bronze file holding every raw series fetched for this metric.</summary>
    public async Task<string> AppendAsync(
        string metricCode,
        IReadOnlyList<RawSeries> series,
        MetricsCatalog catalog,
        string runId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(catalog);

        var metric = catalog.Get(metricCode);
        var provider = metric?.Provider ?? "UNKNOWN";
        var dataset = metric?.Dataset ?? "UNKNOWN";
        var indicator = metric?.IndicatorCode ?? metricCode;
        var path = _paths.BronzeFile(provider, dataset, metricCode, runId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var fetchedAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);

        await WriteAsync(path, series, metricCode, provider, dataset, indicator, runId, fetchedAt, cancellationToken);
        _logger.Information("Bronze: wrote {Count} series for '{Metric}' to {Path}.", series.Count, metricCode, path);
        return path;
    }

    /// <summary>Reads bronze back for every run of one metric, oldest first.</summary>
    public async Task<IReadOnlyList<RawSeries>> ReadAsync(string metricCode, CancellationToken cancellationToken = default)
    {
        var files = BronzeFiles(metricCode).OrderBy(f => f, StringComparer.Ordinal).ToList();
        var series = new List<RawSeries>();
        foreach (var file in files)
        {
            series.AddRange(await ReadFileAsync(file, cancellationToken));
        }

        return series;
    }

    public IReadOnlyList<string> BronzeFiles(string metricCode)
    {
        if (!Directory.Exists(_paths.BronzeDir))
        {
            return Array.Empty<string>();
        }

        return Directory.GetFiles(_paths.BronzeDir, "*.parquet", SearchOption.AllDirectories)
            .Where(f => string.Equals(Path.GetFileName(Path.GetDirectoryName(f)), metricCode, StringComparison.Ordinal))
            .ToList();
    }

    private static async Task WriteAsync(
        string path,
        IReadOnlyList<RawSeries> series,
        string metricCode,
        string provider,
        string dataset,
        string indicator,
        string runId,
        string fetchedAt,
        CancellationToken cancellationToken)
    {
        await using var stream = File.Create(path);
        await using var writer = await ParquetWriter.CreateAsync(Schema, stream, cancellationToken: cancellationToken);
        using var group = writer.CreateRowGroup();
        var count = series.Count;

        await group.WriteAsync(MetricCode, Repeat(metricCode, count));
        await group.WriteAsync(Provider, Repeat(provider, count));
        await group.WriteAsync(Dataset, Repeat(dataset, count));
        await group.WriteAsync(IndicatorCode, Repeat(indicator, count));
        await group.WriteAsync(SeriesName, series.Select(s => s.SeriesName).ToArray());
        await group.WriteAsync(DimensionsJson, series.Select(s => JsonSerializer.Serialize(s.Dimensions)).ToArray());
        await group.WriteAsync(PeriodsJson, series.Select(s => JsonSerializer.Serialize(s.Periods)).ToArray());
        await group.WriteAsync(ValuesJson, series.Select(s => JsonSerializer.Serialize(s.Values)).ToArray());
        await group.WriteAsync(RunIdField, Repeat(runId, count));
        await group.WriteAsync(FetchedAt, Repeat(fetchedAt, count));
    }

    private static async Task<IReadOnlyList<RawSeries>> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        await using var reader = await ParquetReader.CreateAsync(stream, cancellationToken: cancellationToken);
        var result = new List<RawSeries>();
        for (var i = 0; i < reader.RowGroupCount; i++)
        {
            using var group = reader.OpenRowGroupReader(i);
            var n = (int)group.RowCount;
            var dims = new string[n];
            var periods = new string[n];
            var values = new string[n];
            var names = new string?[n];
            await group.ReadAsync(DimensionsJson, dims, cancellationToken: cancellationToken);
            await group.ReadAsync(PeriodsJson, periods, cancellationToken: cancellationToken);
            await group.ReadAsync(ValuesJson, values, cancellationToken: cancellationToken);
            await group.ReadAsync(SeriesName, names, cancellationToken: cancellationToken);

            for (var r = 0; r < n; r++)
            {
                result.Add(new RawSeries(
                    JsonSerializer.Deserialize<Dictionary<string, string>>(dims[r]) ?? new Dictionary<string, string>(),
                    JsonSerializer.Deserialize<List<string?>>(periods[r]) ?? new List<string?>(),
                    JsonSerializer.Deserialize<List<string?>>(values[r]) ?? new List<string?>(),
                    names[r]));
            }
        }

        return result;
    }

    private static string[] Repeat(string value, int count)
    {
        var items = new string[count];
        Array.Fill(items, value);
        return items;
    }
}
