using System.Net;
using System.Text.RegularExpressions;
using DriveTrack.Integration.Tests.Persistence;
using DriveTrack.Integration.Tests.Support;

namespace DriveTrack.Integration.Tests.Components;

/// <summary>
/// NFR-26: Leaflet 1.9.4, vendored and pinned.
/// <para>
/// There is no front-end package manager here, so "pinned" has to mean something a build can check.
/// The banner in the shipped file is the version, the paths the module names are the vendored ones,
/// and no CDN host appears anywhere — a map that fetched its library over the public internet would
/// work on a developer's machine and show a blank rectangle in a network the customer controls.
/// </para>
/// </summary>
public class MapAssetTests
{
    private static string LeafletDirectory { get; } = Path.Combine(
        RepositoryLayout.ProjectDirectory("DriveTrack.Web"), "wwwroot", "lib", "leaflet");

    [Theory]
    [InlineData("leaflet-src.esm.js")]
    [InlineData("leaflet.css")]
    [InlineData("images/layers.png")]
    [InlineData("images/layers-2x.png")]
    [InlineData("images/marker-icon.png")]
    [InlineData("images/marker-icon-2x.png")]
    [InlineData("images/marker-shadow.png")]
    public void The_vendored_asset_is_on_disk(string relativePath)
    {
        // The images are named individually because Leaflet asks for them by name at run time: a
        // missing marker-icon-2x.png is a broken image on a retina screen and nothing anywhere else.
        var file = new FileInfo(Path.Combine(LeafletDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar)));

        Assert.True(file.Exists, $"Leaflet asset '{relativePath}' is missing from wwwroot/lib/leaflet/.");
        Assert.True(file.Length > 0, $"Leaflet asset '{relativePath}' is empty.");
    }

    [Fact]
    public void The_shipped_library_is_the_pinned_version()
    {
        var bundle = File.ReadAllText(Path.Combine(LeafletDirectory, "leaflet-src.esm.js"));

        var banner = Regex.Match(
            bundle,
            @"Leaflet (?<version>\d+\.\d+\.\d+)",
            RegexOptions.None,
            TimeSpan.FromSeconds(5));

        Assert.True(banner.Success, "The vendored Leaflet bundle carries no version banner.");
        Assert.Equal("1.9.4", banner.Groups["version"].Value);

        // Vendored verbatim except for the source map reference: shipping a sourceMappingURL for a
        // file that is not there produces a 404 in every developer's console.
        Assert.DoesNotContain("sourceMappingURL", bundle, StringComparison.Ordinal);
    }

    [Fact]
    public void The_module_loads_the_vendored_copy_and_never_a_cdn()
    {
        var module = SharedMarkup.ReadShared("DtMap.razor.js");

        Assert.Contains(@"""/lib/leaflet/leaflet-src.esm.js""", module, StringComparison.Ordinal);
        Assert.Contains(@"""/lib/leaflet/leaflet.css""", module, StringComparison.Ordinal);
        Assert.Contains(@"""/lib/leaflet/images/""", module, StringComparison.Ordinal);

        foreach (var host in new[] { "unpkg.com", "cdnjs", "jsdelivr", "cdn.leafletjs", "leafletjs.com" })
        {
            Assert.DoesNotContain(host, module, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void The_stylesheet_is_injected_at_run_time_so_the_shell_keeps_two_links()
    {
        // DesignTokenTests pins App.razor to exactly two <link rel="stylesheet"> elements, and
        // folding leaflet.css into app.scss would drag its relative url(images/...) references into
        // wwwroot/css/, which is git-ignored Sass output. Injecting it is what reconciles the two.
        var module = SharedMarkup.ReadShared("DtMap.razor.js");
        var shell = SharedMarkup.ReadComponent("App.razor");

        Assert.Contains("document.head.appendChild(link)", module, StringComparison.Ordinal);
        Assert.Contains("document.getElementById(stylesheetId)", module, StringComparison.Ordinal);
        Assert.DoesNotContain("leaflet", shell, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Shared/DtDialog.razor", "Components/Shared/DtDialog.razor.js")]
    [InlineData("Shared/DtMap.razor", "Components/Shared/DtMap.razor.js")]
    // Story 6.1's two. The signature pad is a shared component like the two above it; the capture
    // panel is a screen, which is why the component argument is a path under Components/ rather
    // than a bare file name - a collocated module is not a privilege of Components/Shared/.
    [InlineData("Shared/DtSignaturePad.razor", "Components/Shared/DtSignaturePad.razor.js")]
    [InlineData("Pages/ProofCapture.razor", "Components/Pages/ProofCapture.razor.js")]
    public void The_component_imports_the_module_at_the_path_that_is_served(string component, string served)
    {
        // The served route above and the string the component hands to import() have to be the
        // same path. They are written in two files and nothing else compares them.
        var source = SharedMarkup.ReadComponent(component.Split('/'));

        Assert.Contains($"private const string ModulePath = \"./{served}\";", source, StringComparison.Ordinal);
    }

    [Fact]
    public void The_vendored_stylesheet_keeps_its_own_image_paths()
    {
        // Served from beside the images, so `url(images/marker-icon.png)` in Leaflet's own CSS
        // resolves. This is the reason the file lives in wwwroot/lib/leaflet/ rather than anywhere
        // more convenient - and wwwroot is also the one tree NFR-29's scan never reads, which is
        // what makes shipping third-party CSS unmodified safe here and nowhere else.
        var stylesheet = File.ReadAllText(Path.Combine(LeafletDirectory, "leaflet.css"));

        Assert.Contains("images/", stylesheet, StringComparison.Ordinal);
        Assert.Contains(".leaflet-container", stylesheet, StringComparison.Ordinal);
    }
}

/// <summary>
/// The other half of NFR-26, and of the two collocated modules: an asset has to be
/// <em>served</em>, not merely present.
/// <para>
/// Everything above reads the developer's disk. None of it would notice the file being excluded
/// from the static web asset manifest, which is how it actually reaches a browser — the same gap
/// <c>ThemeDeliveryTests</c> exists to close for the compiled theme.
/// </para>
/// </summary>
public class MapAssetDeliveryTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData("lib/leaflet/leaflet.css")]
    [InlineData("lib/leaflet/leaflet-src.esm.js")]
    [InlineData("lib/leaflet/images/marker-icon.png")]
    // The collocated modules the two components import at run time. Reading them off disk proves
    // they were written; only fetching them proves they are reachable at the path the component
    // asks for. Rename either file - or drop it from the static asset manifest - and every dialog
    // and every map is dead on arrival with the rest of this class still green.
    [InlineData("Components/Shared/DtDialog.razor.js")]
    [InlineData("Components/Shared/DtMap.razor.js")]
    // Story 6.1's two, for the same reason and with a sharper consequence: a signature pad that
    // cannot import its module is a canvas nobody can draw on, and a capture panel that cannot
    // import its own reads no coordinates - so FR-119's capture becomes impossible, silently, with
    // every other test in the solution still green.
    [InlineData("Components/Shared/DtSignaturePad.razor.js")]
    [InlineData("Components/Pages/ProofCapture.razor.js")]
    public async Task The_vendored_asset_is_served(string path)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await ApiFactory.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var content = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        Assert.NotEmpty(content);
    }
}
