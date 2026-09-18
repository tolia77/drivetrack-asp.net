using System.Net;
using System.Text.RegularExpressions;
using DriveTrack.Integration.Tests.Persistence;
using DriveTrack.Integration.Tests.Support;

namespace DriveTrack.Integration.Tests.Components;

/// <summary>
/// The two screens a visitor reaches before they have an account, read as structure rather than as
/// behaviour.
/// <para>
/// <c>AuthenticationTests</c> already drives both forms end to end through the real POST, and
/// <c>SubmitGuardTests</c> already decides what a guard on them would mean. Neither of them looks
/// at the shape of the page: both screens spent the whole of Spec A inside four raw Bootstrap
/// wrappers guessing at a width, with a bare <c>&lt;h1&gt;</c> outside the shared heading row, and
/// the suite stayed green throughout. This is the class that would have noticed.
/// </para>
/// <para>
/// Rendered over HTTP rather than through <c>ComponentRenderer</c>, because that is the only place
/// these screens exist as themselves: they are <c>[ExcludeFromInteractiveRouting]</c> so their
/// markup really is in the first response (AD-14), and their <c>EditForm</c>s want the form
/// mapping context only the real endpoint puts behind them.
/// </para>
/// </summary>
public class AnonymousScreenTests(PostgresFixture postgres)
{
    /// <summary>The shared heading row, its class list and what this screen put inside it.</summary>
    private static readonly Regex PageHead = new(
        @"<div\b[^>]*class=""(?<classes>[^""]*\bdt-page-head\b[^""]*)""[^>]*>(?<body>.*?)</div>",
        RegexOptions.Singleline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    /// <summary>A call to <c>DtCard</c>, with whatever it was handed.</summary>
    private static readonly Regex CardTag = new(
        @"<DtCard\b(?<attributes>[^>]*)>",
        RegexOptions.None,
        TimeSpan.FromSeconds(5));

    /// <summary>
    /// The rendered half: the route each screen answers on, the wrapper its width is bounded on,
    /// the hook on its submit and the sibling screen it offers a way to. A table rather than two
    /// near-identical runs of assertions, because the claim is precisely that both screens start
    /// the same way.
    /// </summary>
    public static TheoryData<string, string, string, string> Rendered() => new()
    {
        { "/sign-in", "dt-sign-in-panel", "dt-sign-in-submit", "/register" },
        { "/register", "dt-register-panel", "dt-register-submit", "/sign-in" },
    };

    /// <summary>The same two screens by their file under <c>Components/Account</c>.</summary>
    public static TheoryData<string, string, string> Sources() => new()
    {
        { "SignIn", "dt-sign-in-panel", "dt-sign-in-submit" },
        { "Register", "dt-register-panel", "dt-register-submit" },
    };

    /// <summary>Each screen's stylesheet and the wrapper it bounds.</summary>
    public static TheoryData<string, string> Stylesheets() => new()
    {
        { "SignIn", "dt-sign-in-panel" },
        { "Register", "dt-register-panel" },
    };

    /// <summary>
    /// Every box on the two forms and the token a password manager should fill it by. Stated per
    /// box rather than counted, because the failure worth catching is the plausible wrong token -
    /// <c>current-password</c> on a registration box offers the password of whoever last signed in
    /// on this browser, and every count of "six boxes carry one" is satisfied by it.
    /// </summary>
    public static TheoryData<string, string, string> AutocompleteTokens() => new()
    {
        { "/sign-in", "sign-in-email", "username" },
        { "/sign-in", "sign-in-password", "current-password" },
        { "/register", "register-first-name", "given-name" },
        { "/register", "register-last-name", "family-name" },
        { "/register", "register-email", "email" },
        { "/register", "register-phone", "tel" },
        { "/register", "register-password", "new-password" },
        { "/register", "register-password-confirmation", "new-password" },
    };

    [Theory]
    [MemberData(nameof(Rendered))]
    public async Task The_screen_renders_anonymously_as_one_heading_over_a_titleless_card(
        string route,
        string wrapper,
        string submit,
        string sibling)
    {
        var html = await RenderAsync(route);

        // Exactly one, and it is the screen's own. `FocusOnNavigate Selector="h1"` in Routes.razor
        // moves the keyboard to the first one it finds, so a second is a coin toss about where a
        // visitor lands - and DtCard renders an <h2> precisely so it can never become that second.
        // Counted rather than merely found, because the titleless card is the decision under test:
        // a `Title` added later would put the screen's own name on the page twice.
        Assert.Equal(1, SharedMarkup.Occurrences(html, "<h1"));

        var head = PageHead.Match(html);

        Assert.True(head.Success, $"{route} renders no heading row.");
        Assert.Contains("<h1", head.Groups["body"].Value, StringComparison.Ordinal);

        // `mb-4` on the row, and it is the only thing left producing the gap under the title:
        // `.dt-page-head h1` zeroes the heading's own margin and `.dt-type-h1` declares none, so a
        // dropped utility leaves the form sitting against the heading with every other assertion
        // in this class still green.
        Assert.Contains("mb-4", head.Groups["classes"].Value, StringComparison.Ordinal);

        // And the heading is bare. Not because there is no navigation to echo - AccessDenied and
        // NotFound are anonymous too and both carry a glyph - but because those two report a
        // status and the glyph is that status, while this is a form whose content is the boxes
        // below. A glyph here would be decoration.
        Assert.DoesNotContain("<svg", head.Groups["body"].Value, StringComparison.Ordinal);

        // The wrapper really holds the card, rather than the class appearing somewhere on the
        // page: a closing </div> moved above the card would leave a `Contains` satisfied and the
        // width bound wrapped around nothing.
        var panel = PhoneLayoutTests.BodyOf(html, wrapper);

        Assert.Contains("dt-card__body", panel, StringComparison.Ordinal);
        Assert.Contains("<form", panel, StringComparison.Ordinal);

        // No head on it: DtCard's <h2> would name the screen a second time and cannot stand in for
        // the <h1> above it in any case.
        Assert.DoesNotContain("dt-card__head", panel, StringComparison.Ordinal);

        // The stopgap, gone from the rendered page rather than only from the source.
        Assert.DoesNotContain(@"class=""card""", panel, StringComparison.Ordinal);
        Assert.DoesNotContain("col-md-8", panel, StringComparison.Ordinal);
        Assert.DoesNotContain("col-lg-5", panel, StringComparison.Ordinal);

        // The way to the other form, inside the panel with it. A visitor here has no navigation
        // menu - the shell offers the anonymous entry points and nothing else - so this link is
        // the only offered route between the two screens, and it is pinned the way
        // AccessDeniedDeliveryTests pins that page's way out.
        Assert.Contains($@"href=""{sibling}""", panel, StringComparison.Ordinal);

        // NFR-24: the action carries a glyph AND its word. Neither screen had a `dt-*` hook until
        // now, so nothing in the suite could name these two buttons except by their colour.
        var button = SharedMarkup.ElementWithClass(html, "button", submit);

        Assert.Contains("<svg", button, StringComparison.Ordinal);
        Assert.True(
            SharedMarkup.IsUkrainian(SharedMarkup.TextOf(WebUtility.HtmlDecode(button))),
            $"The submit on {route} renders a glyph and no word.");
    }

    [Theory]
    [MemberData(nameof(AutocompleteTokens))]
    public async Task Every_box_on_the_anonymous_forms_names_what_a_password_manager_should_fill_it_with(
        string route,
        string id,
        string token)
    {
        // These two forms are the only ones in the product a password manager is asked to fill:
        // the six boxes behind the signed-in dialogs already carry their token, and these eight
        // were the set that did not. Without one a manager guesses from the surrounding labels,
        // and the guess it makes on a registration form is the password it stored for whoever
        // last signed in on this browser.
        var html = await RenderAsync(route);

        var box = Regex.Match(
            html,
            @"<input\b[^>]*\bid=""" + Regex.Escape(id) + @"""[^>]*>",
            RegexOptions.None,
            TimeSpan.FromSeconds(5));

        Assert.True(box.Success, $"{route} renders no box with id '{id}'.");
        Assert.Contains($@"autocomplete=""{token}""", box.Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refused_post_renders_its_refusal_inside_the_panel_it_is_about()
    {
        // The half of these screens no test rendered at all: what a refusal looks like. The
        // banner used to sit above the panel at the full width of the main column, which put a
        // sentence about a 30rem form on a measure belonging to the page - and moving it is a
        // change nothing in the suite could see, because nothing in the suite ever posted a form
        // and then read the page back for its shape.
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var factory = await ApiFactory.CreateAsync(
            postgres.ConnectionString, cancellationToken, useProbeAuthentication: false);

        // A refusal that names no field, which is the banner's whole reason for existing:
        // AUTH_INVALID_CREDENTIALS cannot go under either box without telling an attacker which of
        // the two was wrong (FR-4).
        var refusedSignIn = await PostAsync(
            factory,
            "/sign-in",
            "signIn",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Form.Email"] = "nobody@drivetrack.test",
                ["Form.Password"] = "Wrong-Passw0rd",
            },
            cancellationToken);

        var signInPanel = PhoneLayoutTests.BodyOf(refusedSignIn, "dt-sign-in-panel");

        Assert.Contains("dt-banner__body", signInPanel, StringComparison.Ordinal);

        // And a refusal that does name one, which lands under the box it is about rather than in
        // the banner - the same panel, one level further in.
        var refusedRegistration = await PostAsync(
            factory,
            "/register",
            "register",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Form.FirstName"] = "Олена",
                ["Form.LastName"] = "Петренко",
                ["Form.Email"] = Guid.NewGuid().ToString("N")[..12] + "@drivetrack.test",
                ["Form.PhoneNumber"] = "+380441234567",
                ["Form.Password"] = "Passw0rd-Test",
                ["Form.PasswordConfirmation"] = "Something-Else-1",
            },
            cancellationToken);

        var registerPanel = PhoneLayoutTests.BodyOf(refusedRegistration, "dt-register-panel");

        Assert.Contains("dt-field-error", registerPanel, StringComparison.Ordinal);
        Assert.Contains("dt-card__body", registerPanel, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Sources))]
    public void The_screen_stays_statically_rendered_with_a_hooked_submit(
        string file,
        string wrapper,
        string submit)
    {
        var screen = SharedMarkup.ReadComponent("Account", file + ".razor");

        // The key `SubmitGuardTests` reads these two files by, and the only part of that
        // arrangement worth restating here. That class records the exemption *before* it looks for
        // a `disabled` binding, and says so in as many words: ordered the other way, adding a
        // guard - "a harmless change, and a safer one" - would drop the file from an exemption set
        // asserted by equality and fail a test for getting better. So a binding is permitted, and
        // nothing here forbids one. What is not permitted is losing the attribute: without it
        // these become interactive screens with unguarded submits, which that class reports as
        // offenders, and they also fall out of the set - two failures away from one deleted line.
        Assert.Contains("@attribute [ExcludeFromInteractiveRouting]", screen, StringComparison.Ordinal);
        Assert.Contains("@attribute [AllowAnonymous]", screen, StringComparison.Ordinal);

        // The hook is on the submit rather than merely somewhere in the file.
        Assert.Matches(
            new Regex(
                @"<button\b[^>]*\b" + Regex.Escape(submit) + @"\b[^>]*type=""submit""",
                RegexOptions.Singleline,
                TimeSpan.FromSeconds(5)),
            screen);

        // Titleless in the call rather than only in the output. Asserted as the absence of a
        // `Title` rather than as the exact spelling `<DtCard>`: the precedent this follows,
        // `ProofCapture.razor:49`, is `<DtCard Class="dt-proof-summary">` - titleless without
        // being attribute-free - so a hook added here later is a change these screens are allowed
        // to make and an exact-spelling assertion would refuse it.
        var card = CardTag.Match(screen);

        Assert.True(card.Success, $"{file}.razor calls no DtCard.");
        Assert.DoesNotContain("Title", card.Groups["attributes"].Value, StringComparison.Ordinal);

        // And the rule in the stylesheet has something to hang from. A scoped selector whose
        // element was renamed matches nothing and reports nothing.
        Assert.Contains(wrapper, screen, StringComparison.Ordinal);

        // The four Bootstrap wrappers are gone rather than left beside their replacement for a
        // later reader to work out which of the two is in charge of the width.
        Assert.DoesNotContain("col-md-", screen, StringComparison.Ordinal);
        Assert.DoesNotContain("col-lg-", screen, StringComparison.Ordinal);
        Assert.DoesNotContain(@"class=""card""", screen, StringComparison.Ordinal);
        Assert.DoesNotContain(@"class=""card-body""", screen, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Stylesheets))]
    public void The_panel_is_bounded_on_the_published_token_rather_than_on_a_column_guess(
        string file,
        string wrapper)
    {
        var css = WithoutComments(SharedMarkup.ReadComponent("Account", file + ".razor.css"));

        // `max-width` rather than `width`, which is what lets the panel fill a viewport narrower
        // than the bound instead of overflowing it - and `dialog-narrow` rather than a figure,
        // which is both NFR-29's rule and the honest reading: a sign-in panel is the same kind of
        // object as the form dialog the token is published for.
        Assert.Matches(
            new Regex(
                @"\." + Regex.Escape(wrapper) + @"\s*\{[^}]*max-width:\s*var\(--dt-dialog-narrow\)",
                RegexOptions.Singleline,
                TimeSpan.FromSeconds(5)),
            css);

        // No `::deep`, and the absence is the claim rather than an omission. The bound is on this
        // screen's own wrapper; reached through `::deep` onto DtCard's <div> it would be one
        // component deciding how wide another component's element is.
        //
        // Read off the declarations rather than the file, because the file explains itself: both
        // stylesheets say in prose why they do not reach into the card, and a scan that counted
        // that sentence would fail a stylesheet for stating its own reasoning.
        Assert.DoesNotContain("::deep", css, StringComparison.Ordinal);
    }

    /// <summary>A stylesheet with its <c>/* … */</c> comments removed.</summary>
    private static string WithoutComments(string css) =>
        Regex.Replace(css, @"/\*.*?\*/", " ", RegexOptions.Singleline, TimeSpan.FromSeconds(5));

    /// <summary>One of the two screens, fetched anonymously over the real pipeline.</summary>
    /// <remarks>
    /// The real cookie scheme rather than the probe, because "anonymously" is the whole claim: a
    /// host whose default scheme hands every request a principal cannot tell a page that allows
    /// anonymous callers from one that merely never asked.
    /// </remarks>
    private async Task<string> RenderAsync(string route)
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var factory = await ApiFactory.CreateAsync(
            postgres.ConnectionString, cancellationToken, useProbeAuthentication: false);

        using var client = factory.CreateClient();
        using var response = await client.GetAsync(new Uri(route, UriKind.Relative), cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    /// <summary>
    /// Posts one of the forms and answers the page it re-rendered. Its own copy rather than
    /// <c>AuthenticationTests</c>' - that class keeps a reader for the cookie and the redirect,
    /// which is the half this one has no opinion about.
    /// </summary>
    private static async Task<string> PostAsync(
        ApiFactory factory,
        string route,
        string handler,
        IReadOnlyDictionary<string, string> fields,
        CancellationToken cancellationToken)
    {
        using var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
        });

        using var page = await client.GetAsync(new Uri(route, UriKind.Relative), cancellationToken);

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);

        var html = await page.Content.ReadAsStringAsync(cancellationToken);

        var token = Regex.Match(
            html,
            @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""",
            RegexOptions.None,
            TimeSpan.FromSeconds(5));

        Assert.True(token.Success, $"{route} rendered no antiforgery token.");

        var cookies = page.Headers.TryGetValues("Set-Cookie", out var headers)
            ? string.Join("; ", headers.Select(value => value.Split(';')[0]))
            : string.Empty;

        using var form = new FormUrlEncodedContent(
            new Dictionary<string, string>(fields, StringComparer.Ordinal)
            {
                ["_handler"] = handler,
                ["__RequestVerificationToken"] = token.Groups[1].Value,
            });

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(route, UriKind.Relative))
        {
            Content = form,
        };

        if (cookies.Length > 0)
        {
            request.Headers.Add("Cookie", cookies);
        }

        using var response = await client.SendAsync(request, cancellationToken);

        // The refusal is re-rendered in place, not redirected to.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }
}
