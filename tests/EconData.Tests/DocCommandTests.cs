using System.Text.RegularExpressions;
using EconData.Core.Cli;

namespace EconData.Tests;

/// <summary>
/// Documentation drift guard: every <c>lake</c> subcommand written in the shipped docs must
/// exist in <see cref="LakeCommands"/>. Only real command forms are matched (a
/// <c>-- lake &lt;sub&gt;</c> invocation or a <c>`lake &lt;sub&gt;`</c> code span), so prose
/// such as "lake tiers" and shell <c>export ... lake ...</c> lines are ignored.
/// </summary>
public class DocCommandTests
{
    private static readonly string[] Docs =
    [
        "README.md",
        "CHANGELOG.md",
        "EXPLAINER.md",
        "AGENTS.md",
        Path.Combine("docs", "DEPLOY.md"),
        Path.Combine("docs", "DEV_GUIDE.md"),
    ];

    private static readonly Regex CliForm = new(@"--[ \t]+lake[ \t]+([a-z][a-z0-9-]*)", RegexOptions.Compiled);
    private static readonly Regex SpanForm = new(@"`lake[ \t]+([a-z][a-z0-9-]*)", RegexOptions.Compiled);

    [Fact]
    public void DocumentedLakeSubcommands_AllExist()
    {
        var unknown = new List<string>();

        foreach (var doc in Docs)
        {
            var path = Path.Combine(TestPaths.RepoRoot, doc);
            if (!File.Exists(path))
            {
                continue;
            }

            var text = File.ReadAllText(path);
            foreach (Match match in CliForm.Matches(text).Concat(SpanForm.Matches(text)))
            {
                var command = match.Groups[1].Value;
                if (!LakeCommands.All.Contains(command))
                {
                    unknown.Add($"{doc}: lake {command}");
                }
            }
        }

        Assert.Empty(unknown);
    }

    [Fact]
    public void LakeCommands_ContainsImplementedSubcommands()
    {
        Assert.Contains("init", LakeCommands.All);
        Assert.Contains("status", LakeCommands.All);
        Assert.Contains("build-gold", LakeCommands.All);
        Assert.Contains("rebuild", LakeCommands.All);
        Assert.Contains("import-from-sqlite", LakeCommands.All);
    }
}
