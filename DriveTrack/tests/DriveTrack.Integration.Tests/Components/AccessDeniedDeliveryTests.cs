using System.Net;
using System.Xml.Linq;
using DriveTrack.Integration.Tests.Persistence;
using DriveTrack.Integration.Tests.Support;

namespace DriveTrack.Integration.Tests.Components;

/// <summary>
/// FR-79 asserted over HTTP, beside <c>NotFoundDeliveryTests</c>.
/// <para>
/// The page is the destination of the cookie handler's <c>AccessDeniedPath</c> and of the route
/// view's refusal, so two redirects lead here — and neither of them would notice the page becoming
/// unroutable or losing <c>[AllowAnonymous]</c>. The second is the worse failure: without it the
/// page is refused for the same reason the caller was sent here, and the redirect loops.
/// </para>
/// </summary>
[Collection(nameof(PostgresCollection))]
public class AccessDeniedDeliveryTests(PostgresFixture postgres)
{
    [Fact]
    public async Task An_anonymous_caller_is_served_the_explanation_and_a_route_back()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await ApiFactory.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            new Uri("/access-denied", UriKind.Relative),
            cancellationToken);

        // 200 rather than a redirect: a page that bounced an anonymous caller to sign-in would be
        // the loop this page exists to break.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Contains("<h1", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"href=""/""", body, StringComparison.Ordinal);

        // Decoded, because the framework's default HTML encoder writes every non-Latin character
        // as a numeric reference. Read from the catalogue rather than typed here, so improving the
        // wording does not mean editing an assertion.
        var text = WebUtility.HtmlDecode(body);

        Assert.Contains(UiTextValue("AccessDeniedTitle"), text, StringComparison.Ordinal);
        Assert.Contains(UiTextValue("AccessDeniedMessage"), text, StringComparison.Ordinal);
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
