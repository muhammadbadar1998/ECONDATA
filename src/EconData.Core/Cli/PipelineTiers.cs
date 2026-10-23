namespace EconData.Core.Cli;

/// <summary>
/// Which lake tiers a run writes. Defaults are resolved from <c>loadToDb</c> so existing
/// callers keep their behaviour: gold is the SQLite-backed serving store historically
/// written when <c>loadToDb</c> was true.
/// </summary>
public sealed record PipelineTiers(bool Bronze, bool Silver, bool Gold)
{
    /// <summary>No lake tiers: the pre-lake contract, where only SQLite is written.</summary>
    public static PipelineTiers None { get; } = new(false, false, false);

    /// <summary>All three tiers, used by the new tier flags.</summary>
    public static PipelineTiers All { get; } = new(true, true, true);

    /// <summary>Compact form for the run audit row and UI, e.g. <c>B/S/G</c>.</summary>
    public string Label
    {
        get
        {
            var parts = new List<string>(3);
            if (Bronze)
            {
                parts.Add("B");
            }

            if (Silver)
            {
                parts.Add("S");
            }

            if (Gold)
            {
                parts.Add("G");
            }

            return string.Join("/", parts);
        }
    }
}
