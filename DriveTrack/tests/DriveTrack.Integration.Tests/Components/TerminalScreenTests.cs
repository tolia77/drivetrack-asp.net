using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DriveTrack.Integration.Tests.Persistence;
using DriveTrack.Integration.Tests.Support;

namespace DriveTrack.Integration.Tests.Components;

/// <summary>
/// The three screens that are the end of a journey rather than a step in one — refused, not there,
/// and broken — read as structure, the way <c>AnonymousScreenTests</c> reads the two forms.
/// <para>
/// <c>ShellRoutingTests</c> already asserts that each has a heading and a route back, and the two
/// delivery classes already assert that two of them arrive over HTTP at all. Neither looks at the
/// shape: all three spent the whole of the design-system work with a bare <c>&lt;h1&gt;</c> outside
/// the shared heading row, and <c>Error</c> never left the .NET scaffold — two headings, the
/// destructive-action colour on both, and the request id in a bare <c>&lt;code&gt;</c>. The suite
/// stayed green throughout. This is the class that would have noticed.
/// </para>
/// <para>
/// Rendered over HTTP rather than through <c>ComponentRenderer</c>: all three are
/// <c>[ExcludeFromInteractiveRouting]</c> (AD-14) so their markup really is the first response, and
/// <c>Error</c>'s cascading <c>HttpContext</c> — the only source of <c>RequestId</c> — exists
/// nowhere else.
/// </para>
/// <para>
/// Every claim below is scoped to an element this page rendered. The whole document is the wrong
/// unit on a page that ships inside <c>MainLayout</c>: <c>NavMenu</c> contributes sixteen anchors
/// and <c>Icon</c> thirty-three <c>&lt;path&gt;</c> elements, so a document-wide search for
/// <c>&lt;a</c> or <c>&lt;p</c> is satisfied by the shell whatever the screen does.
/// </para>
/// </summary>
public class TerminalScreenTests(PostgresFixture postgres)
{
    /// <summary>The shared heading row, its class list and what the screen put inside it.</summary>
    private static readonly Regex PageHead = new(
        @"<div\b[^>]*class=""(?<classes>[^""]*\bdt-page-head\b[^""]*)""[^>]*>(?<body>.*?)</div>",
        RegexOptions.Singleline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    /// <summary>The screen's own heading, and the classes it wears.</summary>
    private static readonly Regex Heading = new(
        @"<h1\b[^>]*\bclass=""(?<classes>[^""]*)""",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    /// <summary>A paragraph and its contents.</summary>
    private static readonly Regex Paragraph = new(
        @"<p\b[^>]*>(?<body>.*?)</p>",
        RegexOptions.Singleline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    /// <summary>A second-level heading and its contents.</summary>
    private static readonly Regex Subheading = new(
        @"<h2\b[^>]*>(?<body>.*?)</h2>",
        RegexOptions.Singleline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    /// <summary>
    /// Each screen by the route it answers on, whether its heading carries a glyph, and the
    /// catalogue key its explanation is read from.
    /// <para>
    /// The glyph column is the one real difference between the three, and it is stated per row
    /// rather than asserted uniformly because both answers are deliberate: on
    /// <c>AccessDenied</c> and <c>NotFound</c> the glyph <em>is</em> the status — a barred circle
    /// says "refused" and the magnifier "nothing here" before a word is read — while the icon set
    /// has no drawing of "something failed". <c>Warning</c> means caution, which is a different
    /// thing to say, and an approximate glyph on a failure page is worse than none.
    /// </para>
    /// </summary>
    public static TheoryData<string, bool, string> Screens() => new()
    {
        { "/access-denied", true, "AccessDeniedMessage" },
        { "/not-found", true, "NotFoundMessage" },
        { "/Error", false, "ErrorMessage" },
    };

    /// <summary>The same three by their file under <c>Components/Pages</c>.</summary>
    public static TheoryData<string> Sources() => new()
    {
        "AccessDenied.razor",
        "NotFound.razor",
        "Error.razor",
    };

    [Theory]
    [MemberData(nameof(Screens))]
    public async Task The_screen_renders_one_heading_inside_the_shared_row(
        string route,
        bool carriesGlyph,
        string messageKey)
    {
        var html = await RenderAsync(route);

        // Exactly one. `FocusOnNavigate Selector="h1"` in Routes.razor moves the keyboard to the
        // first one it finds, so a second is a coin toss about where the caller lands - and on a
        // page whose entire job is to explain a dead end, landing in the wrong place is the
        // failure repeated.
        Assert.Equal(1, SharedMarkup.Occurrences(html, "<h1"));

        var head = PageHead.Match(html);

        Assert.True(head.Success, $"{route} renders no heading row.");
        Assert.Contains("<h1", head.Groups["body"].Value, StringComparison.Ordinal);

        // `mb-4` on the row, and it is the only thing producing the gap under the title:
        // `.dt-page-head h1` zeroes the heading's own margin and `.dt-type-h1` declares none, so a
        // dropped utility leaves the explanation sitting against the heading with every other
        // assertion in this class still green.
        //
        // Read as a class token rather than as a substring of an exact class list, so a third
        // class on the wrapper is a change these screens are allowed to make.
        Assert.Matches(ClassToken("mb-4"), head.Groups["classes"].Value);

        Assert.Equal(carriesGlyph, head.Groups["body"].Value.Contains("<svg", StringComparison.Ordinal));

        // The explanation, by its value from the catalogue rather than by a tag: `<p` is also the
        // prefix of `<path`, of which the icon set emits dozens, and the request-id block on
        // `Error` is a paragraph of its own - so a tag-shaped assertion survives the explanation
        // disappearing from all three screens.
        //
        // Decoded first, because the framework's default HTML encoder writes every non-Latin
        // character as a numeric reference.
        var text = WebUtility.HtmlDecode(html);
        var message = UiTextValue(messageKey);

        Assert.Contains(
            Paragraph.Matches(text),
            paragraph => paragraph.Groups["body"].Value.Contains(message, StringComparison.Ordinal));

        // And it is a paragraph rather than a second heading. Stated about the sentence rather
        // than about the tag: `DtCard` renders an <h2> by design, so forbidding the tag would fail
        // the first of these screens to grow a titled card. What the scaffold did was set this
        // sentence large and call it a section, and that is what may not come back.
        Assert.DoesNotContain(
            Subheading.Matches(text),
            subheading => subheading.Groups["body"].Value.Contains(message, StringComparison.Ordinal));

        // The way out, scoped to the page's own anchor. `NavMenu` renders sixteen of its own on
        // every page, so a document-wide search for `<a` stays green with all three links deleted;
        // `btn-link` appears nowhere in the layout, and there is exactly one on each of these
        // screens - AccessDenied offers its two behind mutually exclusive branches.
        Assert.Equal(1, SharedMarkup.Occurrences(html, "btn-link"));

        var back = SharedMarkup.ElementWithClass(html, "a", "btn-link");

        // NFR-24: the glyph AND its word.
        Assert.Contains("<svg", back, StringComparison.Ordinal);
        Assert.True(
            SharedMarkup.IsUkrainian(SharedMarkup.TextOf(WebUtility.HtmlDecode(back))),
            $"The way out on {route} renders a glyph and no word.");
    }

    [Fact]
    public async Task The_error_page_reports_a_failure_rather_than_offering_to_delete_something()
    {
        var html = await RenderAsync("/Error");

        // `.text-danger` is token-backed - `_bootstrap-bridge.scss` maps `$danger` to
        // `$dt-action-destructive` - so NFR-29's scanner has nothing to say about it, and that is
        // exactly why this assertion has to exist somewhere else. The token behind the utility is
        // the colour the palette reserves for the button that deletes a record; a heading is not
        // an action, and the feedback family is what a failure reports in.
        //
        // Read off the heading's own class list rather than off the document: the whole point is
        // which element wears which colour, and a page-wide scan both passes when the class lands
        // somewhere else and fails the day an unrelated component uses the utility honestly.
        var heading = Heading.Match(html);

        Assert.True(heading.Success, "The error page renders no classed <h1>.");

        var classes = heading.Groups["classes"].Value;

        Assert.DoesNotMatch(ClassToken("text-danger"), classes);
        Assert.Matches(ClassToken("dt-error-heading"), classes);

        // The identifier a caller reads out to support. Its presence is asserted before the face
        // it wears: the block is behind `ShowRequestId`, and with neither `Activity.Current?.Id`
        // nor `TraceIdentifier` set the regex below would report a missing `<code>` rather than a
        // request id that never rendered.
        var text = WebUtility.HtmlDecode(html);

        Assert.Contains(UiTextValue("ErrorRequestId"), text, StringComparison.Ordinal);

        Assert.Matches(
            new Regex(
                @"<code\b[^>]*\bclass=""[^""]*\bdt-type-code\b",
                RegexOptions.None,
                TimeSpan.FromSeconds(5)),
            html);

        // The way out points at the root, and the claim is tied to the anchor: `<base href="/" />`
        // in App.razor:18 satisfies any page-wide search for the destination, so a link retargeted
        // at a signed-in screen - which would bounce an anonymous caller straight back here - would
        // never be noticed.
        Assert.Matches(
            new Regex(
                @"<a\b[^>]*\bclass=""[^""]*\bbtn-link\b[^""]*""[^>]*\bhref=""/""",
                RegexOptions.None,
                TimeSpan.FromSeconds(5)),
            html);
    }

    [Fact]
    public void The_failure_heading_wears_the_feedback_colour_rather_than_the_action_one()
    {
        // The other half of the claim above, and the half the rendered page cannot make: the class
        // is on the right element, but nothing reads what the class does. `--dt-danger` is
        // published and resolves to the same value as `action-destructive`, so swapping the rule
        // to it repaints the heading in the delete-button colour with every other assertion here
        // still green.
        var css = WithoutComments(SharedMarkup.ReadComponent("Pages", "Error.razor.css"));

        Assert.Matches(
            new Regex(
                @"\.dt-error-heading\s*\{[^}]*color:\s*var\(--dt-danger-text\)",
                RegexOptions.Singleline,
                TimeSpan.FromSeconds(5)),
            css);

        // No `::deep`, and the absence is the claim rather than an omission. The <h1> is this
        // component's own element; reached through `::deep` the rule would be aimed at something
        // another component rendered and would match nothing at all.
        //
        // Read off the declarations rather than the file, because the file explains itself.
        Assert.DoesNotContain("::deep", css, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Sources))]
    public void The_screen_stays_statically_rendered(string fileName)
    {
        // Load-bearing on all three rather than tidy. With prerendering off (AD-14) an interactive
        // version answers the redirect or the exception that led here with the document shell and
        // none of the heading, the explanation or the route back until a circuit connects - and on
        // `Error` the circuit may be the thing that failed. It is also what makes that page's
        // cascading `HttpContext` resolve at all, which is the only source of `RequestId`.
        var page = SharedMarkup.ReadComponent("Pages", fileName);

        Assert.Contains("@attribute [ExcludeFromInteractiveRouting]", page, StringComparison.Ordinal);

        // The heading row in the source, not only in the output: the rendered assertion above
        // would be satisfied by a row some future layout supplied, and the claim is that each
        // screen writes its own. Matched by class token rather than as a literal, so a third class
        // on the wrapper is not three failures.
        var head = PageHead.Match(page);

        Assert.True(head.Success, $"{fileName} writes no heading row of its own.");
        Assert.Matches(ClassToken("mb-4"), head.Groups["classes"].Value);
    }

    /// <summary>One class in a class list, bounded so a longer name does not satisfy it.</summary>
    private static Regex ClassToken(string name) => new(
        @"(?<![\w-])" + Regex.Escape(name) + @"(?![\w-])",
        RegexOptions.None,
        TimeSpan.FromSeconds(5));

    /// <summary>A stylesheet with its <c>/* … */</c> comments removed.</summary>
    private static string WithoutComments(string css) =>
        Regex.Replace(css, @"/\*.*?\*/", " ", RegexOptions.Singleline, TimeSpan.FromSeconds(5));

    /// <summary>
    /// One string from the catalogue. Read from the <c>.resx</c> rather than typed here, the way
    /// the two delivery classes read theirs: a test that spelled the Ukrainian out would have to be
    /// edited every time the wording is improved, which is how assertions get weakened instead of
    /// updated.
    /// </summary>
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

    /// <summary>One of the three, fetched anonymously over the real pipeline.</summary>
    /// <remarks>
    /// The real cookie scheme rather than the probe, because every one of these routes has to
    /// answer an anonymous caller: a host whose default scheme hands every request a principal
    /// cannot tell a page that allows anonymous callers from one that merely never asked.
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
}
