using EconData.Core.Lake;
using Serilog;

namespace EconData.Core.Analytics;

/// <summary>
/// Aggregates bronze/silver/gold state for the Lake page, the REST endpoint, and the CLI.
/// Every count comes from the files on disk, so a fresh install reports empty tiers
/// rather than failing.
/// </summary>
public sealed class LakeStatusService
{
    private static readonly string[] GoldTables =
    {
        "dbe_entities",
        "dbe_entity_aliases",
        "dbe_metrics",
        "dbe_sources",
        "dbe_entity_membership",
        "dbe_observations",
        "dbe_derived_observations",
        "dbe_pipeline_runs",
    };

    private readonly LakePaths _paths;
    private readonly GoldTier _gold;
    private readonly ILogger _logger;

    public LakeStatusService(LakePaths paths, GoldTier gold, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(gold);
        ArgumentNullException.ThrowIfNull(logger);
        _paths = paths;
        _gold = gold;
        _logger = logger;
    }

    public async Task<LakeStatus> GetAsync(CancellationToken cancellationToken = default)
    {
        var bronze = await BronzeAsync(cancellationToken).ConfigureAwait(false);
        var silver = await SilverAsync(cancellationToken).ConfigureAwait(false);
        var (gold, tables) = Gold();
        var partitions = await new SilverTier(_paths, _logger).RowCountsAsync(cancellationToken).ConfigureAwait(false);

        return new LakeStatus(
            Directory: _paths.LakeDir,
            GoldPath: _paths.GoldPath,
            Bronze: bronze,
            Silver: silver,
            Gold: gold,
            Tables: tables,
            Partitions: partitions);
    }

    private async Task<TierStatus> BronzeAsync(CancellationToken cancellationToken)
    {
        var bronze = new BronzeTier(_paths, _logger);
        var counts = await bronze.RowCountsAsync(cancellationToken).ConfigureAwait(false);
        var files = Files(_paths.BronzeDir);
        var bytes = files.Sum(f => new FileInfo(f).Length);
        var newest = files.Select(f => File.GetLastWriteTimeUtc(f)).DefaultIfEmpty().Max();
        var metrics = files
            .Select(f => Path.GetFileName(Path.GetDirectoryName(f)))
            .Where(name => !string.IsNullOrEmpty(name))
            .Distinct(StringComparer.Ordinal)
            .Count();
        var rows = counts.Values.Sum();

        return new TierStatus(
            Tier: "bronze",
            Present: rows > 0,
            Rows: rows,
            Files: files.Count,
            Bytes: bytes,
            Newest: files.Count > 0 ? newest.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture) : null,
            Detail: rows > 0 ? $"{metrics} metrics" : "not built yet");
    }

    private async Task<TierStatus> SilverAsync(CancellationToken cancellationToken)
    {
        var silver = new SilverTier(_paths, _logger);
        var counts = await silver.RowCountsAsync(cancellationToken).ConfigureAwait(false);
        var files = Files(_paths.SilverDir);
        var bytes = files.Sum(f => new FileInfo(f).Length);
        var newest = files.Select(f => File.GetLastWriteTimeUtc(f)).DefaultIfEmpty().Max();

        return new TierStatus(
            Tier: "silver",
            Present: counts.Count > 0,
            Rows: counts.Values.Sum(),
            Files: files.Count,
            Bytes: bytes,
            Newest: files.Count > 0 ? newest.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture) : null,
            Detail: counts.Count > 0 ? $"{counts.Count} partitions" : "not built yet");
    }

    private (TierStatus Status, IReadOnlyDictionary<string, long> Tables) Gold()
    {
        var tables = new SortedDictionary<string, long>(StringComparer.Ordinal);
        if (!_gold.Exists)
        {
            return (new TierStatus("gold", false, 0, 0, 0, null, "not built yet"), tables);
        }

        long total = 0;
        try
        {
            using var connection = _gold.OpenReadOnly();
            foreach (var table in GoldTables)
            {
                using var command = connection.CreateCommand();
                command.CommandText = $"SELECT COUNT(*) FROM {table}";
                var rows = Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
                tables[table] = rows;
                total += rows;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warning(ex, "Could not read gold table counts.");
            return (new TierStatus("gold", true, 0, 1, Size(_paths.GoldPath), null, "unreadable"), tables);
        }

        var bytes = Size(_paths.GoldPath);
        var newest = File.GetLastWriteTimeUtc(_paths.GoldPath);
        return (
            new TierStatus(
                "gold",
                true,
                tables.TryGetValue("dbe_observations", out var observations) ? observations : total,
                1,
                bytes,
                newest.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
                $"{tables.Count} tables"),
            tables);
    }

    private static List<string> Files(string dir)
        => Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*.parquet", SearchOption.AllDirectories).ToList()
            : new List<string>();

    private static long Size(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;
}
