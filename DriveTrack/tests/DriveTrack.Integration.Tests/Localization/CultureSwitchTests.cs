using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DriveTrack.Application.Common;
using DriveTrack.Domain.Identity;
using DriveTrack.Integration.Tests.Components;
using DriveTrack.Integration.Tests.Persistence;
using DriveTrack.Integration.Tests.Support;
using DriveTrack.Web.Account;
using DriveTrack.Web.Components.Layout;
using DriveTrack.Web.Components.Shared;
using Microsoft.AspNetCore.Mvc.Testing;

namespace DriveTrack.Integration.Tests.Localization;

/// <summary>
/// The language a user chose, end to end: the endpoint that records it, the screens that follow it,
/// and the four ways the endpoint can be asked for something it must refuse.
/// <para>
/// Everything here runs against <c>ApiFactory</c>, which boots the real <c>Program.cs</c>: the real
/// localization middleware, the real provider chain, the real antiforgery. A switch asserted against
/// a hand-built pipeline would prove only that the test author understood the framework.
/// </para>
/// </summary>
public class CultureSwitchTests(PostgresFixture postgres)
{
    private const string SetCulture = "/set-culture";

    /// <summary>The sign-in page: statically rendered (AD-14), so its markup is in the response.</summary>
    /// <remarks>
    /// The page every one of these tests reads back, and it is chosen rather than convenient: with
    /// prerendering off (AD-14) a routed screen's markup is not in the first response at all, so a
    /// statically rendered page is the only place the HTML a switch produces can actually be read.
    /// It is also the page that matters most: somebody who cannot read the sign-in screen cannot
    /// sign in to change anything.
    /// </remarks>
    private const string StaticPage = "/sign-in";

    private static readonly CultureInfo Ukrainian = CultureInfo.GetCultureInfo("uk-UA");
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    private static readonly Regex TokenInput = new(
        "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"",
        RegexOptions.None,
        TimeSpan.FromSeconds(5));

    private static readonly Regex HtmlLanguage = new(
        "<html[^>]*\\slang=\"([^\"]*)\"",
        RegexOptions.None,
        TimeSpan.FromSeconds(5));

    [Fact]
    public async Task A_visitor_who_has_chosen_nothing_is_served_Ukrainian_whatever_their_browser_asks()
    {
        // The first row of the matrix, and the one that has to keep holding: this change must be
        // invisible to everybody who does not go looking for it.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await ApiFactory.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(StaticPage, UriKind.Relative));
        request.Headers.Add("Accept-Language", "en-US,en;q=0.9");

        using var response = await client.SendAsync(request, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await BodyAsync(response, cancellationToken);

        Assert.Equal("uk", LanguageOf(html));
        Assert.Contains(Catalogue("UiText.resx", "SignInTitle"), html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Choosing_English_writes_a_cookie_that_outlives_the_browser_session_and_returns_the_page_in_English()
    {
        // The acceptance criterion, driven the way a browser drives it: read the page, post the
        // form it rendered, follow the redirect, read the page back.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await ApiFactory.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = Browser(factory);

        using var switched = await SwitchAsync(client, "en-US", StaticPage, cancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, switched.StatusCode);
        Assert.Equal(StaticPage, switched.Headers.Location?.OriginalString);

        var written = SetCultureCookie(switched);

        Assert.NotNull(written);
        Assert.Contains("en-US", Uri.UnescapeDataString(written), StringComparison.Ordinal);

        // "and a new browser session": a session cookie would put the reader back into Ukrainian
        // the next morning with nothing on screen to explain why. A relative lifetime rather than an
        // absolute one, because the endpoint has no clock it is allowed to read (AD-13).
        Assert.Contains("max-age=", written, StringComparison.OrdinalIgnoreCase);

        // The name the host writes and the name the provider reads have to be the same string, and
        // they are declared in two files - so the one the browser is actually sent is asserted here.
        // House convention rather than the framework's .AspNetCore.Culture.
        Assert.StartsWith("drivetrack.culture=", written, StringComparison.Ordinal);

        // The attributes, because every one of them is a decision the endpoint makes in silence.
        // Path=/ or the choice applies to one directory; Lax or a top-level navigation from another
        // site arrives without it; HttpOnly because nothing in the browser reads it.
        Assert.Contains("path=/", written, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", written, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", written, StringComparison.OrdinalIgnoreCase);

        // Not secure over plain HTTP, which is the SameAsRequest default DW-6 gives the session
        // cookie and the compose stack needs: a Secure cookie on an http:// origin is one the
        // browser discards, and the reader's choice would not survive the redirect.
        Assert.DoesNotContain("secure", written, StringComparison.OrdinalIgnoreCase);

        using var page = await client.GetAsync(new Uri(StaticPage, UriKind.Relative), cancellationToken);
        var html = await BodyAsync(page, cancellationToken);

        // Both halves: the screen is in English, and the document says so. Assistive technology and
        // a browser's translation offer read the second one, and nothing else in the suite would
        // notice it still saying uk over an English page.
        Assert.Equal("en", LanguageOf(html));
        Assert.Contains(Catalogue("UiText.en.resx", "SignInTitle"), html, StringComparison.Ordinal);
        Assert.DoesNotContain(Catalogue("UiText.resx", "SignInTitle"), html, StringComparison.Ordinal);

        // And the choice survives a further navigation rather than being a one-page effect.
        using var again = await client.GetAsync(new Uri(StaticPage, UriKind.Relative), cancellationToken);

        Assert.Equal("en", LanguageOf(await BodyAsync(again, cancellationToken)));
    }

    [Fact]
    public async Task Switching_back_to_Ukrainian_works_the_same_way()
    {
        // The other direction, which is not symmetric for free: Ukrainian is also the fallback, so
        // an endpoint that silently wrote nothing for uk-UA would look identical here until the day
        // somebody defaulted the product to English.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await ApiFactory.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = Browser(factory);

        using (var toEnglish = await SwitchAsync(client, "en-US", StaticPage, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Redirect, toEnglish.StatusCode);
        }

        using (var toUkrainian = await SwitchAsync(client, "uk-UA", StaticPage, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Redirect, toUkrainian.StatusCode);
            Assert.Contains("uk-UA", Uri.UnescapeDataString(SetCultureCookie(toUkrainian)!), StringComparison.Ordinal);
        }

        using var page = await client.GetAsync(new Uri(StaticPage, UriKind.Relative), cancellationToken);
        var html = await BodyAsync(page, cancellationToken);

        Assert.Equal("uk", LanguageOf(html));
        Assert.Contains(Catalogue("UiText.resx", "SignInTitle"), html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_culture_nobody_supports_is_refused_without_a_cookie_and_without_a_500()
    {
        // de-DE parses, constructs and formats perfectly - it is a real culture with no catalogue.
        // Written to the cookie it would leave every later request falling back to Ukrainian while
        // the browser held a language the product never had.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await ApiFactory.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = Browser(factory);

        using var response = await SwitchAsync(client, "de-DE", StaticPage, cancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Null(SetCultureCookie(response));

        using var page = await client.GetAsync(new Uri(StaticPage, UriKind.Relative), cancellationToken);

        Assert.Equal("uk", LanguageOf(await BodyAsync(page, cancellationToken)));
    }

    [Fact]
    public async Task A_post_carrying_no_antiforgery_token_is_refused_and_writes_nothing()
    {
        // Without the explicit ValidateRequestAsync this endpoint would take a cross-site form's
        // word for what language somebody reads their screens in. Harmless-sounding, and still a
        // page a user cannot read served by their own session.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await ApiFactory.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = Browser(factory);

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["culture"] = "en-US",
            ["returnUrl"] = StaticPage,
        });

        using var response = await client.PostAsync(new Uri(SetCulture, UriKind.Relative), form, cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(SetCultureCookie(response));
    }

    [Fact]
    public async Task A_post_whose_body_is_not_a_form_is_refused_rather_than_throwing()
    {
        // ReadFormAsync throws on a request carrying no form content type, and an unhandled throw
        // in a minimal-API delegate is a 500 - for a malformed request that has a perfectly good
        // 400 waiting for it. The token goes in the header, which is where antiforgery looks when
        // there is no form to look in, so this reaches the body read rather than stopping short of
        // it: without the content-type check the answer here is 500.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await ApiFactory.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = Browser(factory);

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(SetCulture, UriKind.Relative))
        {
            Content = new StringContent("{\"culture\":\"en-US\"}", Encoding.UTF8, "application/json"),
        };

        request.Headers.Add("RequestVerificationToken", await TokenAsync(client, cancellationToken));

        using var response = await client.SendAsync(request, cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(SetCultureCookie(response));
    }

    [Fact]
    public async Task A_return_address_that_is_not_local_is_refused_and_the_caller_goes_to_the_root()
    {
        // The open-redirect row. The return address arrives in the form, which means it arrives
        // from whoever built the form - so an unchecked redirect would let any site borrow this
        // origin to make its own hop look legitimate.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await ApiFactory.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = Browser(factory);

        // The last three are the ones IsLocalUrl alone would not settle: "~/deliveries" is an
        // app-relative form nothing here expands, and the two backslash spellings are how a browser
        // is persuaded to read a path as an authority.
        var hostileUrls = new[]
        {
            "https://evil.test",
            "//evil.test/path",
            "https://evil.test/x?y=1",
            "~/deliveries",
            "/\\evil.test",
            "\\\\evil.test",

            // Not an open redirect but the same check's job: a return address carrying CR/LF would
            // reach Location verbatim, and the header writer throws on it - a 500 out of the one
            // guard that exists to keep this endpoint from being abused.
            "/deliveries\r\nX-Injected: 1",
        };

        foreach (var hostile in hostileUrls)
        {
            using var response = await SwitchAsync(client, "en-US", hostile, cancellationToken);

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal("/", response.Headers.Location?.OriginalString);
        }
    }

    [Fact]
    public async Task A_local_return_address_is_honoured_so_the_switch_leaves_the_reader_where_they_were()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await ApiFactory.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = Browser(factory);

        using var response = await SwitchAsync(client, "en-US", "/deliveries?overdue=true", cancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/deliveries?overdue=true", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task The_error_envelope_speaks_the_language_the_cookie_chose()
    {
        // NFR-3's wire message and NFR-4's field messages both come out of ErrorMessages, so this is
        // the whole REST surface's language in one request. Asserted against the catalogue's own
        // values rather than against a quoted sentence: a reworded entry should not fail this test,
        // and an untranslated one must.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await ApiFactory.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var ukrainian = await EnvelopeAsync(client, culture: null, cancellationToken);
        var english = await EnvelopeAsync(client, "en-US", cancellationToken);

        Assert.Equal(
            Catalogue("ErrorMessages.resx", nameof(ErrorCode.COMMON_VALIDATION_FAILED)),
            ukrainian.Message);

        Assert.Equal(
            Catalogue("ErrorMessages.en.resx", nameof(ErrorCode.COMMON_VALIDATION_FAILED)),
            english.Message);

        // The probe's two field errors: one keyed PERSISTENCE_CHECK_VIOLATION, one keyed
        // COMMON_VALIDATION_FAILED. Both have to move with the culture, not only the headline.
        Assert.NotEmpty(english.Fields);

        // The key sets first, and as sets: indexing the Ukrainian dictionary by an English key the
        // loop below found would throw KeyNotFoundException, which says nothing about which field
        // went missing. A count comparison has the same hole.
        Assert.Equal(
            ukrainian.Fields.Keys.Order(StringComparer.Ordinal).ToArray(),
            english.Fields.Keys.Order(StringComparer.Ordinal).ToArray());

        foreach (var (field, message) in english.Fields)
        {
            Assert.NotEqual(ukrainian.Fields[field], message);
            Assert.False(
                message.Any(character => character is >= 'Ѐ' and <= 'ӿ'),
                $"The English envelope's field message for '{field}' is still Ukrainian: {message}");
        }
    }

    [Fact]
    public async Task A_screen_names_a_refused_field_in_the_reader_s_own_language()
    {
        // The other catalogue the envelope does not reach: FieldNames is read by DtFailureBanner
        // alone, so nothing over HTTP can see whether it was translated. Rendered under each
        // culture through the harness's culture parameter.
        var failures = new Dictionary<string, object?>
        {
            ["Failures"] = (IReadOnlyList<ScreenFailure>)
            [
                new ScreenFailure("PackageWeightKg", nameof(ErrorCode.COMMON_VALIDATION_FAILED)),
            ],
        };

        var ukrainian = await ComponentRenderer.RenderAsync<DtFailureBanner>(failures);
        var english = await ComponentRenderer.RenderAsync<DtFailureBanner>(failures, culture: English);

        Assert.Contains(Catalogue("FieldNames.resx", "PackageWeightKg"), ukrainian, StringComparison.Ordinal);
        Assert.Contains(Catalogue("ErrorMessages.resx", nameof(ErrorCode.COMMON_VALIDATION_FAILED)), ukrainian, StringComparison.Ordinal);

        Assert.Contains(Catalogue("FieldNames.en.resx", "PackageWeightKg"), english, StringComparison.Ordinal);
        Assert.Contains(Catalogue("ErrorMessages.en.resx", nameof(ErrorCode.COMMON_VALIDATION_FAILED)), english, StringComparison.Ordinal);

        var banner = SharedMarkup.TextOf(SharedMarkup.ElementWithClass(ukrainian, "div", "alert-danger"));

        Assert.False(SharedMarkup.HasLatinWord(banner), banner);
        Assert.True(SharedMarkup.IsUkrainian(banner), banner);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(UserRole.Client)]
    public async Task The_navigation_offers_both_languages_and_posts_them_to_the_endpoint_that_records_them(
        UserRole? role)
    {
        // The join every other test here leaves open: the endpoint is exercised by a form this test
        // file builds, and the control is rendered by a component nothing posts. Swap the two button
        // values, drop the antiforgery token, or point the form somewhere else, and every assertion
        // above still passes while the control on the screen does nothing.
        //
        // Rendered for an anonymous visitor as well as a signed-in one, because the visitor is the
        // one who needs it most: somebody who cannot read the sign-in page cannot sign in to change
        // the language.
        // Rendered somewhere other than the root, because the root is the one location for which a
        // component that reads the current path and one that reads nothing produce the same markup.
        const string path = "/deliveries";

        var html = await ShellCaller.RenderAsync<NavMenu>(role, path: path);

        var form = SharedMarkup.ElementWithClass(html, "nav", "flex-column");

        Assert.Contains($"action=\"{SetCulture}\"", html, StringComparison.Ordinal);
        Assert.Contains("method=\"post\"", form, StringComparison.Ordinal);

        // The value, not merely the field. An absolute URI here - NavigationManager.Uri unconverted
        // - is refused by the endpoint's local-URL check and silently sends every switch to the
        // root, which is a page the reader was not on and no assertion about the name would notice.
        Assert.Contains(
            $"name=\"returnUrl\" value=\"{path}\"",
            html,
            StringComparison.Ordinal);

        // The token itself is asserted against the source rather than the render: the harness stubs
        // AntiforgeryStateProvider with one that answers null, so <AntiforgeryToken /> emits nothing
        // here. That a real token reaches a real page is proven over HTTP by every other test in
        // this file, each of which reads one off /sign-in and posts it.
        Assert.Contains(
            "<AntiforgeryToken />",
            SharedMarkup.ReadComponent("Layout", "NavMenu.razor"),
            StringComparison.Ordinal);

        // Both cultures, spelled exactly as Program.cs matches them: a button carrying "en" or "uk"
        // would be refused by the endpoint and the page would come back unchanged.
        Assert.Contains("name=\"culture\" value=\"uk-UA\"", html, StringComparison.Ordinal);
        Assert.Contains("name=\"culture\" value=\"en-US\"", html, StringComparison.Ordinal);

        // Both languages are named as their own speakers write them, and both catalogues carry the
        // same two words - so these two assertions hold under either culture, which is the point:
        // an English reader looking at a Ukrainian page can still find "English" on it.
        Assert.Contains(Catalogue("UiText.resx", "LanguageUkrainian"), html, StringComparison.Ordinal);
        Assert.Contains(Catalogue("UiText.resx", "LanguageEnglish"), html, StringComparison.Ordinal);

        // Which language is in force, on the button that is in force. Without this, inverting
        // IsUkrainian breaks nothing a test can see - the endonyms are culture-invariant, so the
        // marker is the only thing in this control that moves with the culture.
        Assert.Contains("aria-current=\"true\"", ButtonFor("uk-UA", html), StringComparison.Ordinal);
        Assert.DoesNotContain("aria-current", ButtonFor("en-US", html), StringComparison.Ordinal);

        var english = await ShellCaller.RenderAsync<NavMenu>(role, English, path);

        Assert.Contains("aria-current=\"true\"", ButtonFor("en-US", english), StringComparison.Ordinal);
        Assert.DoesNotContain("aria-current", ButtonFor("uk-UA", english), StringComparison.Ordinal);

        // And the rest of the control did follow the culture, which the endonyms cannot show: the
        // form's accessible name is ordinary UI text and is translated.
        Assert.Contains(Catalogue("UiText.resx", "Language"), html, StringComparison.Ordinal);
        Assert.Contains(Catalogue("UiText.en.resx", "Language"), english, StringComparison.Ordinal);
    }

    /// <summary>The opening tag of the switcher button carrying the given culture value.</summary>
    /// <param name="culture">The culture the button posts.</param>
    /// <param name="html">The rendered menu.</param>
    private static string ButtonFor(string culture, string html)
    {
        var match = Regex.Match(
            html,
            "<button[^>]*value=\"" + Regex.Escape(culture) + "\"[^>]*>",
            RegexOptions.None,
            TimeSpan.FromSeconds(5));

        Assert.True(match.Success, $"The menu rendered no switcher button for {culture}.");

        return match.Value;
    }

    [Fact]
    public void Each_culture_formats_a_date_and_a_number_its_own_way()
    {
        // NFR-15, now a two-sided claim. Asserted against known values rather than against
        // CurrentCulture, so this fails on a machine whose ICU data is missing rather than passing
        // because the invariant culture happened to be in scope - the failure mode the Dockerfile.prod
        // scan in LocalizationTests exists to catch in the image.
        var date = new DateOnly(2026, 9, 16);

        Assert.Equal("16.09.2026", date.ToString("d", Ukrainian));
        Assert.Equal("9/16/2026", date.ToString("d", English));

        Assert.Equal("1,234.5", 1234.5m.ToString("N1", English));

        // Ukrainian groups with a non-breaking space, whose exact code point is an ICU decision, so
        // the assertion is on the parts either side of it rather than on the character itself.
        var grouped = 1234.5m.ToString("N1", Ukrainian);

        Assert.StartsWith("1", grouped, StringComparison.Ordinal);
        Assert.EndsWith("234,5", grouped, StringComparison.Ordinal);
        Assert.DoesNotContain(".", grouped, StringComparison.Ordinal);
    }

    /// <summary>A client with a cookie jar and no automatic redirects, which is what a browser is here.</summary>
    private static HttpClient Browser(ApiFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    /// <summary>
    /// Reads the statically rendered page for its antiforgery token, then posts the switch exactly
    /// as the form in <c>NavMenu</c> posts it.
    /// </summary>
    private static async Task<HttpResponseMessage> SwitchAsync(
        HttpClient client,
        string culture,
        string returnUrl,
        CancellationToken cancellationToken)
    {
        var token = await TokenAsync(client, cancellationToken);

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["culture"] = culture,
            ["returnUrl"] = returnUrl,
            ["__RequestVerificationToken"] = token,
        });

        return await client.PostAsync(new Uri(SetCulture, UriKind.Relative), form, cancellationToken);
    }

    /// <summary>Reads an antiforgery token off the statically rendered page, into this client's cookie jar.</summary>
    private static async Task<string> TokenAsync(HttpClient client, CancellationToken cancellationToken)
    {
        using var page = await client.GetAsync(new Uri(StaticPage, UriKind.Relative), cancellationToken);

        var match = TokenInput.Match(await page.Content.ReadAsStringAsync(cancellationToken));

        Assert.True(match.Success, "The statically rendered page carried no antiforgery token.");

        return match.Groups[1].Value;
    }

    /// <summary>The <c>Set-Cookie</c> header for the culture cookie, or null when none was written.</summary>
    private static string? SetCultureCookie(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var cookies)
            ? cookies.FirstOrDefault(value =>
                value.StartsWith(CultureCookie.Name + "=", StringComparison.Ordinal)
                && !value.StartsWith(CultureCookie.Name + "=;", StringComparison.Ordinal))
            : null;

    private static async Task<(string Message, Dictionary<string, string> Fields)> EnvelopeAsync(
        HttpClient client,
        string? culture,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri("/api/probe/validation", UriKind.Relative));

        if (culture is not null)
        {
            request.Headers.Add("Cookie", CultureCookie.Header(culture));
        }

        using var response = await client.SendAsync(request, cancellationToken);
        var envelope = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken);

        Assert.NotNull(envelope);

        var error = envelope.RootElement.GetProperty("error");

        var fields = error.GetProperty("fields")
            .EnumerateObject()
            .ToDictionary(
                property => property.Name,
                property => string.Join(" ", property.Value.EnumerateArray().Select(entry => entry.GetString())),
                StringComparer.Ordinal);

        return (error.GetProperty("message").GetString()!, fields);
    }

    /// <summary>
    /// The value a named catalogue holds for a key, read from the checked-in file rather than
    /// quoted here: a reworded entry must not fail these tests, and an untranslated one must.
    /// </summary>
    /// <param name="fileName">The catalogue, neutral or <c>.en</c>, under <c>Resources/</c>.</param>
    /// <param name="key">The key to read.</param>
    private static string Catalogue(string fileName, string key)
    {
        var path = Path.Combine(
            RepositoryLayout.ProjectDirectory("DriveTrack.Web"),
            "Resources",
            fileName);

        var value = XDocument.Load(path)
            .Root!
            .Elements("data")
            .Where(element => string.Equals(element.Attribute("name")?.Value, key, StringComparison.Ordinal))
            .Select(element => element.Element("value")?.Value)
            .FirstOrDefault();

        Assert.True(value is not null, $"{Path.GetFileName(path)} has no entry for {key}.");

        return value!;
    }

    /// <summary>
    /// The response body with its character references resolved. The shell ships no widened
    /// <c>HtmlEncoder</c>, so every Cyrillic letter leaves as <c>&amp;#x412;</c> and a plain
    /// substring assertion for a Ukrainian sentence would fail against a page that carries it.
    /// </summary>
    private static async Task<string> BodyAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken) =>
        WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(cancellationToken));

    private static string LanguageOf(string html)
    {
        var match = HtmlLanguage.Match(html);

        Assert.True(match.Success, "The response carried no <html lang=\"…\"> attribute.");

        return match.Groups[1].Value;
    }
}
