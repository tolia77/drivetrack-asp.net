using System.Net;
using System.Text.RegularExpressions;
using DriveTrack.Integration.Tests.Persistence;
using DriveTrack.Integration.Tests.Support;

namespace DriveTrack.Integration.Tests.Design;

/// <summary>
/// AD-28 delivered rather than merely declared.
/// <para>
/// Every other assertion about the theme reads it off the developer's disk: that
/// <c>wwwroot/css/app.css</c> exists, that its text carries the right custom properties, that
/// <c>App.razor</c>'s source links it. None of that is the claim the story actually makes. The
/// stylesheet is build output - gitignored, produced by the Sass step, and reaching the browser
/// only by way of the static web asset manifest and <c>MapStaticAssets</c>. Comment out that one
/// call and every disk-reading test stays green while the application serves an unstyled page.
/// </para>
/// <para>
/// So this asks the running application the question instead: fetch the shell, take the
/// stylesheet it actually links, and fetch that. The href is fingerprinted at runtime, which is
/// the other half of why it cannot be asserted from source.
/// </para>
/// </summary>
[Collection(nameof(PostgresCollection))]
public class ThemeDeliveryTests(PostgresFixture postgres)
{
    private static readonly Regex StylesheetHref = new(
        @"href\s*=\s*[""'](?<href>[^""']*css/app[^""']*\.css)[""']",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    [Fact]
    public async Task The_theme_the_shell_links_is_served_and_carries_the_tokens()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await ApiFactory.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        using var shellResponse = await client.GetAsync(
            new Uri("/does-not-exist", UriKind.Relative),
            cancellationToken);

        var shell = await shellResponse.Content.ReadAsStringAsync(cancellationToken);
        var href = StylesheetHref.Match(shell);

        Assert.True(
            href.Success,
            "The rendered shell links no css/app*.css. `@Assets` resolves to nothing when the "
                + "Sass output is missing from the static web asset manifest, and it fails "
                + $"silently rather than throwing. Shell was:{Environment.NewLine}{shell}");

        using var themeResponse = await client.GetAsync(
            new Uri(href.Groups["href"].Value.TrimStart('/'), UriKind.Relative),
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, themeResponse.StatusCode);

        var theme = await themeResponse.Content.ReadAsStringAsync(cancellationToken);

        // A semantic token rather than a `--bs-` one: this asserts OUR layer arrived, not merely
        // that some stylesheet did.
        Assert.Contains("--dt-action-create", theme, StringComparison.Ordinal);
    }
}
