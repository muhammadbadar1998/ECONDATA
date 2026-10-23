using System.Globalization;
using System.Text;

namespace EconData.Core.Lake;

/// <summary>
/// Resolves tier paths under the lake directory and derives deterministic run ids.
/// Bronze is append-only (one file per run per metric); silver is a whole-partition
/// overwrite per metric; gold is a single DuckDB file.
/// </summary>
public sealed class LakePaths
{
    public LakePaths(string lakeDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lakeDir);
        LakeDir = Path.GetFullPath(lakeDir);
    }

    public string LakeDir { get; }

    public string BronzeDir => Path.Combine(LakeDir, "bronze");
    public string SilverDir => Path.Combine(LakeDir, "silver");
    public string GoldDir => Path.Combine(LakeDir, "gold");
    public string GoldPath => Path.Combine(GoldDir, "econdata.duckdb");

    public string BronzeFile(string provider, string dataset, string metric, string runId)
        => Path.Combine(BronzeDir, Sanitize(provider), Sanitize(dataset), Sanitize(metric), Sanitize(runId) + ".parquet");

    public string SilverFile(string metric) => Path.Combine(SilverDir, Sanitize(metric) + ".parquet");

    /// <summary>Sortable run id: ISO timestamp plus a short random suffix, unique per run.</summary>
    public static string NewRunId()
    {
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff", CultureInfo.InvariantCulture);
        return stamp + "-" + Guid.NewGuid().ToString("N")[..8];
    }

    public bool GoldExists => File.Exists(GoldPath);

    public bool SilverExists
        => Directory.Exists(SilverDir) && Directory.GetFiles(SilverDir, "*.parquet").Length > 0;

    public bool BronzeExists
        => Directory.Exists(BronzeDir) && Directory.GetFiles(BronzeDir, "*.parquet", SearchOption.AllDirectories).Length > 0;

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(BronzeDir);
        Directory.CreateDirectory(SilverDir);
        Directory.CreateDirectory(GoldDir);
    }

    public static LakePaths Resolve(string root, string lakeDir)
        => new(Path.IsPathRooted(lakeDir) ? lakeDir : Path.GetFullPath(Path.Combine(root, lakeDir)));

    private static string Sanitize(string segment)
    {
        var builder = new StringBuilder(segment.Length);
        foreach (var c in segment)
        {
            builder.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.' ? c : '_');
        }

        return builder.ToString();
    }
}
