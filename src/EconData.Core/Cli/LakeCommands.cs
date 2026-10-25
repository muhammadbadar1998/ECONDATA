namespace EconData.Core.Cli;

/// <summary>
/// Canonical set of <c>lake</c> subcommands. Used by CLI usage text and by the doc-drift
/// test, so a documented command that does not exist fails the build.
/// </summary>
public static class LakeCommands
{
    private static readonly string[] Ordered =
    [
        "init",
        "status",
        "build-gold",
        "rebuild",
        "import-from-sqlite",
    ];

    public static IReadOnlyList<string> Order => Ordered;

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(Ordered, StringComparer.Ordinal);

    public static string Usage => string.Join("|", Ordered);
}
