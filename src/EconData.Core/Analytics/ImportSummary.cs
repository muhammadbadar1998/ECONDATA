namespace EconData.Core.Analytics;

/// <summary>
/// Result of an <c>import-from-sqlite</c> run. Counts are source (SQLite) vs gold,
/// compared on natural-key sets so a re-run of an idempotent import still matches.
/// </summary>
public sealed record ImportSummary(
    int EntitiesSource,
    int EntitiesGold,
    int MetricsSource,
    int MetricsGold,
    int SourcesSource,
    int SourcesGold,
    int ObservationsSource,
    int ObservationsGold,
    IReadOnlyList<string> MismatchKeys,
    bool DryRun = false)
{
    public bool EntitiesMatch => EntitiesSource == EntitiesGold;
    public bool MetricsMatch => MetricsSource == MetricsGold;
    public bool SourcesMatch => SourcesSource == SourcesGold;
    public bool ObservationsMatch => ObservationsSource == ObservationsGold;

    public bool IsMatch => EntitiesMatch && MetricsMatch && SourcesMatch && ObservationsMatch;
}
