using System.Xml.Linq;
using DriveTrack.Integration.Tests.Support;

namespace DriveTrack.Integration.Tests.Architecture;

/// <summary>
/// AD-1: the dependency direction is <c>Domain</c> → nothing, <c>Application</c> → Domain,
/// <c>Infrastructure</c> → Application and Domain, <c>Web</c> → Application (plus the
/// Infrastructure reference its composition root needs). An arrow not in that list is forbidden.
/// <para>
/// The declared graph is read from the project files because the C# compiler omits an
/// assembly reference that no type actually uses — so compiled metadata can prove a
/// reference is <em>absent</em> but not that a permitted one is <em>declared</em>. Both
/// checks run: the project files pin the declared graph exactly, and compiled metadata
/// confirms no forbidden assembly was linked in anyway.
/// </para>
/// </summary>
public class LayeringTests
{
    private const string Domain = "DriveTrack.Domain";
    private const string Application = "DriveTrack.Application";
    private const string Infrastructure = "DriveTrack.Infrastructure";
    private const string Web = "DriveTrack.Web";

    private static readonly string[] AllLayers = [Domain, Application, Infrastructure, Web];

    /// <summary>Path, relative to the Web project, of the one file AD-1 exempts.</summary>
    private static readonly string CompositionRoot = "Program.cs";

    /// <summary>The only project-to-project arrows AD-1 permits.</summary>
    private static readonly Dictionary<string, string[]> AllowedReferences = new()
    {
        [Domain] = [],
        [Application] = [Domain],
        [Infrastructure] = [Application, Domain],
        [Web] = [Application, Infrastructure],
    };

    [Theory]
    [InlineData(Domain)]
    [InlineData(Application)]
    [InlineData(Infrastructure)]
    [InlineData(Web)]
    public void Project_declares_exactly_the_references_AD1_permits(string project)
    {
        var declared = DeclaredProjectReferences(project);

        Assert.Equal(
            AllowedReferences[project].Order().ToArray(),
            declared.Order().ToArray());
    }

    [Fact]
    public void Domain_carries_no_package_reference()
    {
        // AD-1's root layer stays free of infrastructure concerns, which starts with taking
        // no third-party dependency at all.
        var document = XDocument.Load(RepositoryLayout.ProjectFile(Domain));

        var packages = document.Descendants()
            .Where(e => e.Name.LocalName == "PackageReference")
            .Select(e => e.Attribute("Include")?.Value ?? string.Empty)
            .ToArray();

        Assert.Empty(packages);
    }

    [Theory]
    [InlineData(Domain)]
    [InlineData(Application)]
    [InlineData(Infrastructure)]
    [InlineData(Web)]
    public void Compiled_assembly_links_no_forbidden_layer(string project)
    {
        var assembly = LayerAssemblies.Resolve(project);
        var allowed = AllowedReferences[project];

        var linkedLayers = assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .Where(name => name is not null && AllLayers.Contains(name))
            .Select(name => name!)
            .ToArray();

        var forbidden = linkedLayers.Except(allowed).ToArray();

        Assert.Empty(forbidden);
    }

    [Fact]
    public void Only_ProgramCs_in_Web_names_an_Infrastructure_namespace()
    {
        // AD-1: Web/Program.cs is the composition root and the single file allowed to know
        // that Infrastructure exists.
        var webDirectory = RepositoryLayout.ProjectDirectory(Web);

        var offenders = new List<string>();

        var sources = Directory
            .EnumerateFiles(webDirectory, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                        || path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
            .Where(path => !IsBuildOutput(path, webDirectory));

        foreach (var path in sources)
        {
            var relative = Path.GetRelativePath(webDirectory, path);

            // The exemption is the composition root at the project root, not any file that
            // happens to be called Program.cs somewhere down the tree.
            if (relative.Equals(CompositionRoot, StringComparison.Ordinal))
            {
                continue;
            }

            if (File.ReadAllText(path).Contains("DriveTrack.Infrastructure", StringComparison.Ordinal))
            {
                offenders.Add(relative);
            }
        }

        Assert.Empty(offenders);
    }

    private static bool IsBuildOutput(string path, string projectDirectory)
    {
        var relative = Path.GetRelativePath(projectDirectory, path);
        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return segments.Any(segment =>
            segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("obj", StringComparison.OrdinalIgnoreCase));
    }

    private static string[] DeclaredProjectReferences(string project)
    {
        var document = XDocument.Load(RepositoryLayout.ProjectFile(project));

        return document.Descendants()
            .Where(element => element.Name.LocalName == "ProjectReference")
            .Select(element => element.Attribute("Include")?.Value ?? string.Empty)
            .Select(include => Path.GetFileNameWithoutExtension(include.Replace('\\', '/')))
            .Where(name => !string.IsNullOrEmpty(name))
            .Distinct()
            .ToArray();
    }
}
