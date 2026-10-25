using EconData.Core.Analytics;
using EconData.Core.Data;
using EconData.Core.Lake;

namespace EconData.Core.Cli;

/// <summary>
/// The <c>lake import-from-sqlite</c> command: flag handling, parity output, and exit codes.
/// Output goes through injected writers so the behavior is testable without a spawned process.
/// </summary>
public static class LakeImportCommand
{
    public static async Task<(int ExitCode, ImportSummary Summary)> ExecuteAsync(
        bool dryRun,
        bool writeSilver,
        GoldTier gold,
        SqliteConnectionFactory sqlite,
        SilverTier? silver,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gold);
        ArgumentNullException.ThrowIfNull(sqlite);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        if (writeSilver && silver is null)
        {
            throw new ArgumentNullException(nameof(silver), "write-silver requires a silver tier.");
        }

        var summary = gold.ImportFromSqlite(sqlite, dryRun);

        if (!dryRun && writeSilver)
        {
            var counts = await gold.WriteSilverFromSqliteAsync(sqlite, silver!, cancellationToken).ConfigureAwait(false);
            await stdout.WriteLineAsync($"Silver: wrote {counts.Count} partitions, {counts.Values.Sum()} observations.").ConfigureAwait(false);
        }

        return (Render(summary, dryRun, sqlite.DatabasePath, stdout, stderr), summary);
    }

    /// <summary>
    /// Renders the outcome and returns the process exit code. Kept separate so the mismatch
    /// branch can be exercised with a synthetic summary: after a real import parity is
    /// always repaired, so divergence cannot be produced through the public API alone.
    /// </summary>
    internal static int Render(
        ImportSummary summary,
        bool dryRun,
        string databasePath,
        TextWriter stdout,
        TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        if (dryRun)
        {
            stdout.WriteLine($"entities      source {summary.EntitiesSource,-6} dry run: nothing written");
            stdout.WriteLine($"metrics       source {summary.MetricsSource,-6} dry run: nothing written");
            stdout.WriteLine($"sources       source {summary.SourcesSource,-6} dry run: nothing written");
            stdout.WriteLine($"observations  source {summary.ObservationsSource,-6} dry run: nothing written");
            return 0;
        }

        stdout.WriteLine($"entities      source {summary.EntitiesSource,-6} gold {summary.EntitiesGold,-6} {(summary.EntitiesMatch ? "ok" : "MISMATCH")}");
        stdout.WriteLine($"metrics       source {summary.MetricsSource,-6} gold {summary.MetricsGold,-6} {(summary.MetricsMatch ? "ok" : "MISMATCH")}");
        stdout.WriteLine($"sources       source {summary.SourcesSource,-6} gold {summary.SourcesGold,-6} {(summary.SourcesMatch ? "ok" : "MISMATCH")}");
        stdout.WriteLine($"observations  source {summary.ObservationsSource,-6} gold {summary.ObservationsGold,-6} {(summary.ObservationsMatch ? "ok" : "MISMATCH")}");

        if (!summary.IsMatch)
        {
            stderr.WriteLine("Mismatch detected:");
            foreach (var key in summary.MismatchKeys.Take(5))
            {
                stderr.WriteLine($"  - {key}");
            }

            return 1;
        }

        stdout.WriteLine($"Gold imported from SQLite at {databasePath}.");
        return 0;
    }
}
