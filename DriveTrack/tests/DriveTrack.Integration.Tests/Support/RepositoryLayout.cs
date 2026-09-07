namespace DriveTrack.Integration.Tests.Support;

/// <summary>
/// Locates the solution tree from the test assembly's output directory so tests can assert
/// against the checked-in project files and sources, not only against compiled output.
/// </summary>
internal static class RepositoryLayout
{
    /// <summary>The directory holding <c>DriveTrack.sln</c>.</summary>
    public static DirectoryInfo SolutionRoot { get; } = FindSolutionRoot();

    /// <summary>The <c>src</c> directory.</summary>
    public static DirectoryInfo Source { get; } =
        new(Path.Combine(SolutionRoot.FullName, "src"));

    /// <summary>Absolute path of a project file under <c>src</c>.</summary>
    public static string ProjectFile(string projectName) =>
        Path.Combine(Source.FullName, projectName, $"{projectName}.csproj");

    /// <summary>Absolute path of a project directory under <c>src</c>.</summary>
    public static string ProjectDirectory(string projectName) =>
        Path.Combine(Source.FullName, projectName);

    private static DirectoryInfo FindSolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DriveTrack.sln")))
            {
                return directory;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate DriveTrack.sln above '{AppContext.BaseDirectory}'.");
    }
}
