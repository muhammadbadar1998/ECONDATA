namespace EconData.Core.Cli;

/// <summary>Resolves repository-relative paths against the directory containing the solution.</summary>
public static class CliPaths
{
    public static string Root()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "EconData.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return Directory.GetCurrentDirectory();
    }

    public static string Resolve(string root, string path)
        => Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(root, path));
}
