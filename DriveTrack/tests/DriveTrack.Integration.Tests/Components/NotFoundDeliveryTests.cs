using System.Net;
using System.Xml.Linq;
using DriveTrack.Integration.Tests.Persistence;
using DriveTrack.Integration.Tests.Support;

namespace DriveTrack.Integration.Tests.Components;

/// <summary>
/// FR-78 asserted over HTTP, the way <c>ThemeDeliveryTests</c> asserts the stylesheet.
/// <para>
/// Reading the page's source proves it exists; it does not prove an unrouted URL reaches it. That
/// depends on <c>UseStatusCodePagesWithReExecute</c> and on the router's <c>NotFoundPage</c>, either
/// of which can be removed without touching the page — and the symptom is a blank body, which is
/// exactly what the baseline shipped and exactly what no source scan notices.
/// </para>
/// </summary>
[Collection(nameof(PostgresCollection))]
public class NotFoundDeliveryTests(PostgresFixture postgres)
{
    [Fact]
    public async Task An_unrouted_url_renders_the_not_found_page_inside_the_shell()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await ApiFactory.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            new Uri("/no-such-screen-exists", UriKind.Relative),
            cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.False(string.IsNullOrWhiteSpace(body), "An unrouted URL returned an empty body.");

        // Inside the shell, not instead of it: the navigation, the brand and the layout are still
        // there, so a caller who mistyped a URL has somewhere to go from where they landed.
        Assert.Contains("<html", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("navbar-brand", body, StringComparison.Ordinal);

        // The heading FocusOnNavigate looks for, and the way back.
        Assert.Contains("<h1", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"href=""/""", body, StringComparison.Ordinal);

        // The page's own text, read from the catalogue rather than typed here: a test that spelled
        // the Ukrainian out would have to be edited every time the wording is improved, which is how
        // assertions get weakened instead of updated.
        //
        // Decoded first, because the framework's default HTML encoder escapes every non-Latin
        // character to a numeric reference - the page really does say "Сторінку не знайдено", it
        // just says it as `&#x421;&#x442;...` on the wire.
        var text = WebUtility.HtmlDecode(body);

        Assert.Contains(UiTextValue("NotFoundTitle"), text, StringComparison.Ordinal);
        Assert.Contains(UiTextValue("BackToHome"), text, StringComparison.Ordinal);
    }

    private static string UiTextValue(string key)
    {
        var path = Path.Combine(
            RepositoryLayout.ProjectDirectory("DriveTrack.Web"), "Resources", "UiText.resx");

        var value = XDocument.Load(path)
            .Root!
            .Elements("data")
            .FirstOrDefault(element => element.Attribute("name")?.Value == key)
            ?.Element("value")
            ?.Value;

        Assert.False(string.IsNullOrEmpty(value), $"UiText.resx has no '{key}'.");

        return value!;
    }
}
