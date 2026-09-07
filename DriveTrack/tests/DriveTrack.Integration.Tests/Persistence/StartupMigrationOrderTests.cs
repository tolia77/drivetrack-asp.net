using DriveTrack.Integration.Tests.Support;

namespace DriveTrack.Integration.Tests.Persistence;

/// <summary>
/// AD-20's ordering guarantee: migrations are applied at container start <em>before</em> the
/// web host accepts requests. MigrationPipelineTests proves the migrator works; this proves
/// the composition root still calls it, and still calls it first. Without this, deleting the
/// await from Program.cs leaves the rest of the suite green and ships an app that serves
/// against whatever schema happens to be there.
/// </summary>
public class StartupMigrationOrderTests
{
    private static readonly string ProgramSource = File.ReadAllText(
        Path.Combine(RepositoryLayout.ProjectDirectory("DriveTrack.Web"), "Program.cs"));

    [Fact]
    public void Composition_root_runs_the_migrator()
    {
        Assert.Contains("MigrateAsync(", ProgramSource, StringComparison.Ordinal);
    }

    [Fact]
    public void Composition_root_never_uses_EnsureCreated()
    {
        // AD-20: the schema comes only from migrations.
        Assert.DoesNotContain("EnsureCreated", ProgramSource, StringComparison.Ordinal);
    }

    [Fact]
    public void No_source_file_under_src_calls_EnsureCreated()
    {
        // AD-20 says EnsureCreated appears nowhere, not only in the composition root, and
        // Infrastructure is where someone would actually write it. The leading dot is what
        // separates a call from the prose in DatabaseMigrator's documentation.
        var root = RepositoryLayout.Source.FullName;

        var offenders = Directory
            .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path, root))
            .Where(path => File.ReadAllText(path)
                .Contains(".EnsureCreated", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(root, path))
            .ToArray();

        Assert.Empty(offenders);
    }

    private static bool IsBuildOutput(string path, string root)
    {
        var segments = Path.GetRelativePath(root, path)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return segments.Any(segment =>
            segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("obj", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Migration_runs_before_the_host_starts_serving()
    {
        var migrate = ProgramSource.IndexOf("MigrateAsync(", StringComparison.Ordinal);
        var run = ProgramSource.IndexOf("app.RunAsync(", StringComparison.Ordinal);

        Assert.True(migrate >= 0, "Program.cs does not call MigrateAsync.");
        Assert.True(run >= 0, "Program.cs does not call app.RunAsync.");
        Assert.True(
            migrate < run,
            "Program.cs must await MigrateAsync before app.RunAsync, or the app can serve "
            + "requests against an unmigrated database.");
    }
}
