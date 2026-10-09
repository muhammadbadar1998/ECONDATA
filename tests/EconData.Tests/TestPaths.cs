namespace EconData.Tests;

internal static class TestPaths
{
    internal static string RepoRoot { get; } = LocateRepoRoot();

    internal static string ConfigFile(string name) => Path.Combine(RepoRoot, "config", name);

    private static string LocateRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "EconData.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate repo root (EconData.slnx).");
    }
}
