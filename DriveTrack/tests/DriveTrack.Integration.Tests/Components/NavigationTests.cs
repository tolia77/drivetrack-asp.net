using System.Text.RegularExpressions;
using DriveTrack.Domain.Identity;
using DriveTrack.Web.Account;
using DriveTrack.Web.Components.Layout;
using DriveTrack.Web.Components.Pages;

namespace DriveTrack.Integration.Tests.Components;

/// <summary>
/// FR-77 and FR-12: the navigation a role is offered, and the product mark every screen shares.
/// <para>
/// FR-12 is the one worth restating, because the test cannot: hiding a link is a convenience.
/// Whether a caller may perform an operation is settled by <c>IAccessGuard</c> inside the service,
/// which runs whether or not the link was ever rendered. Nothing below asserts otherwise, and
/// nothing below should be read as making the menu an authorization boundary.
/// </para>
/// </summary>
public class NavigationTests
{
    [Theory]
    [InlineData(UserRole.Admin)]
    [InlineData(UserRole.Dispatcher)]
    [InlineData(UserRole.Driver)]
    [InlineData(UserRole.Client)]
    public void Every_role_is_offered_somewhere_to_go(UserRole role)
    {
        // Total over the enum and never empty: a role that fell through the switch would either
        // throw on every render or - worse, if the switch had a silent default - get a navigation
        // with nothing in it and no way to tell that from "still loading".
        var destinations = NavDestinations.For(role);

        Assert.NotEmpty(destinations);
        Assert.Contains(NavDestination.Home, destinations);
    }

    [Fact]
    public void A_role_the_table_does_not_know_fails_loudly()
    {
        // The rows stopped being uniform with story 7.1, which is what the table was written for.
        // This is what makes a role added without a row an error rather than an empty menu.
        Assert.Throws<ArgumentOutOfRangeException>(() => NavDestinations.For((UserRole)999));
    }

    [Theory]
    [InlineData(UserRole.Admin, true, true)]
    [InlineData(UserRole.Dispatcher, true, false)]
    [InlineData(UserRole.Driver, false, false)]
    [InlineData(UserRole.Client, false, false)]
    public void The_two_administration_rosters_are_offered_to_the_roles_that_have_a_use_for_them(
        UserRole role,
        bool clients,
        bool dispatchers)
    {
        // FR-77 over story 7.1's two new destinations. A dispatcher is offered the client roster
        // because FR-48 gives them a reason to open it - they need it to attach a client to a
        // delivery - and is not offered the dispatcher roster, which is admin-only work.
        //
        // FR-12 still holds: this is what is worth showing, not who is allowed to do what. A
        // dispatcher who typed /clients would be served the screen and refused the moment it tried
        // to write, by IAccessGuard, inside the service.
        var destinations = NavDestinations.For(role);

        Assert.Equal(clients, destinations.Contains(NavDestination.Clients));
        Assert.Equal(dispatchers, destinations.Contains(NavDestination.Dispatchers));
    }

    [Fact]
    public void An_anonymous_caller_is_offered_the_page_that_allows_them()
    {
        // The landing page is [AllowAnonymous], so it is the one destination that survives having
        // no role. Sign-in and registration are account actions, not destinations.
        Assert.Equal(new[] { NavDestination.Home }, NavDestinations.Anonymous.ToArray());
    }

    [Fact]
    public void The_menu_renders_exactly_one_block_per_destination()
    {
        // The split this asserts is the point of the table: a C# switch decides WHICH destinations,
        // one literal @if block decides HOW each renders. Written the other way round -
        // @Localizer[destination.LabelKey] - every navigation key would look unused to
        // LocalizationTests' closed-catalogue check and the suite would fail for the wrong reason.
        var menu = SharedMarkup.ReadComponent("Layout", "NavMenu.razor");

        foreach (var destination in Enum.GetValues<NavDestination>())
        {
            Assert.Equal(
                1,
                SharedMarkup.Occurrences(menu, $"destinations.Contains(NavDestination.{destination})"));
        }

        // And no block for a destination the enum does not have.
        var referenced = Regex.Matches(
                menu,
                @"destinations\.Contains\(NavDestination\.(?<name>[A-Za-z]+)\)",
                RegexOptions.None,
                TimeSpan.FromSeconds(5))
            .Select(match => match.Groups["name"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(Enum.GetValues<NavDestination>().Length, referenced.Length);
    }

    [Fact]
    public void The_menu_reads_the_caller_once_and_only_behind_the_authenticated_check()
    {
        // ICurrentUser.Role throws for an anonymous caller, so a menu that asked for it outside the
        // authenticated branch would take down the shell on every anonymous page.
        var menu = SharedMarkup.ReadComponent("Layout", "NavMenu.razor");

        Assert.Contains("@inject ICurrentUser Caller", menu, StringComparison.Ordinal);
        Assert.Equal(1, SharedMarkup.Occurrences(menu, "Caller.Role"));
        Assert.Contains(
            "isSignedIn ? NavDestinations.For(Caller.Role) : NavDestinations.Anonymous",
            menu,
            StringComparison.Ordinal);
    }

    [Fact]
    public void No_navigation_icon_is_markup_with_nothing_behind_it()
    {
        // The scaffold rendered `bi-person-fill-nav-menu`, `bi-lock-fill-nav-menu` and
        // `bi-person-plus-fill-nav-menu` with no CSS rule for any of them: an empty box on every
        // screen, and nothing in the build said so. The shared <Icon> replaces all of it, and both
        // the dead classes and the rules that used to feed them are gone.
        var menu = SharedMarkup.ReadComponent("Layout", "NavMenu.razor");
        var stylesheet = SharedMarkup.ReadComponent("Layout", "NavMenu.razor.css");

        Assert.DoesNotContain("bi-", menu, StringComparison.Ordinal);
        Assert.DoesNotContain(@"class=""bi", menu, StringComparison.Ordinal);
        Assert.DoesNotContain(".bi-house-door-fill-nav-menu {", stylesheet, StringComparison.Ordinal);

        // Every link and action carries an icon beside its text (NFR-24): the brand mark, home,
        // profile, the two fleet screens (4.1), the dispatch board and the own-deliveries screen
        // (5.1), the two administration rosters (7.1), sign-out, sign-in and register - twelve in
        // all. The count moves with the destination table on purpose: a destination rendered
        // without a glyph is markup with nothing behind it, which is the defect this whole test
        // guards, so adding a link has to be a deliberate edit to this line rather than an empty
        // box nobody notices.
        Assert.Equal(12, SharedMarkup.Occurrences(menu, "<Icon Name="));
    }

    [Fact]
    public void The_product_name_and_mark_travel_together_in_the_shell()
    {
        // NFR-25. The brand lives in NavMenu, which MainLayout renders for every routed screen, so
        // asserting it here is asserting it everywhere.
        var menu = SharedMarkup.ReadComponent("Layout", "NavMenu.razor");
        var layout = SharedMarkup.ReadComponent("Layout", "MainLayout.razor");

        var brand = SharedMarkup.ElementWithClass(menu, "a", "navbar-brand");

        Assert.Contains("<Icon Name=", brand, StringComparison.Ordinal);
        Assert.Contains("DriveTrack", brand, StringComparison.Ordinal);

        Assert.Contains("<NavMenu />", layout, StringComparison.Ordinal);
    }

    // =====================================================================================
    // What the shell actually renders
    //
    // Everything above reads the file's text, and no arrangement of words can tell a signed-in
    // menu from an anonymous one: swap the two guards - so a signed-in caller has no way out and
    // a visitor is offered sign-out - and every source assertion still passes. These render it.
    // =====================================================================================

    [Theory]
    [InlineData(UserRole.Admin)]
    [InlineData(UserRole.Dispatcher)]
    [InlineData(UserRole.Driver)]
    [InlineData(UserRole.Client)]
    public async Task A_signed_in_caller_is_offered_every_destination_their_role_has(UserRole role)
    {
        var html = await ShellCaller.RenderAsync<NavMenu>(role);
        var links = Hrefs(html);

        // FR-77: what the table returns for that role, rendered.
        Assert.Contains("profile", links);
        Assert.Contains(string.Empty, links);

        // FR-77: the fleet screens are offered to the two roles that run dispatch and to nobody
        // else. Rendered per role rather than read off the table, because no arrangement of words
        // in the markup can tell a dispatcher's menu from a client's.
        var runsDispatch = role is UserRole.Admin or UserRole.Dispatcher;

        Assert.Equal(runsDispatch, links.Contains("drivers"));
        Assert.Equal(runsDispatch, links.Contains("vehicles"));

        // Story 5.1's two destinations, and the distinction between them: the dispatch board shows
        // every delivery in the system and belongs to the roles that run dispatch, while the
        // own-deliveries screen shows what the caller's own scope narrows to and belongs to the two
        // roles that are a party to a delivery. The two sets are complements here rather than
        // nested, and that is deliberate: a dispatcher's scope narrows nothing, so the screen would
        // show them the whole board under a heading saying "mine" - which is why it refuses them,
        // and why offering them the link would be offering a link that can only refuse.
        Assert.Equal(runsDispatch, links.Contains("deliveries"));
        Assert.Equal(!runsDispatch, links.Contains("my-deliveries"));

        // And the account action that belongs to a caller who has a session.
        Assert.Contains("action=\"/sign-out\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("sign-in", links);
        Assert.DoesNotContain("register", links);

        // The rendered half of the table: no arrangement of source text can tell an admin's menu
        // from a driver's, so each role's is rendered and read back.
        var destinations = NavDestinations.For(role);

        Assert.Equal(destinations.Contains(NavDestination.Clients), links.Contains("clients"));
        Assert.Equal(destinations.Contains(NavDestination.Dispatchers), links.Contains("dispatchers"));
    }

    [Fact]
    public async Task Only_an_administrator_is_offered_the_dispatcher_roster()
    {
        // Stated once as a flat claim as well as through the table, because "admin sees both,
        // dispatcher sees one, the other two see neither" is the sentence FR-77 actually makes and
        // a parameterised assertion can be true of the wrong table.
        var admin = Hrefs(await ShellCaller.RenderAsync<NavMenu>(UserRole.Admin));
        var dispatcher = Hrefs(await ShellCaller.RenderAsync<NavMenu>(UserRole.Dispatcher));
        var driver = Hrefs(await ShellCaller.RenderAsync<NavMenu>(UserRole.Driver));
        var client = Hrefs(await ShellCaller.RenderAsync<NavMenu>(UserRole.Client));

        Assert.Contains("clients", admin);
        Assert.Contains("dispatchers", admin);

        Assert.Contains("clients", dispatcher);
        Assert.DoesNotContain("dispatchers", dispatcher);

        foreach (var links in new[] { driver, client })
        {
            Assert.DoesNotContain("clients", links);
            Assert.DoesNotContain("dispatchers", links);
        }
    }

    [Fact]
    public async Task An_anonymous_visitor_is_offered_the_way_in_and_never_the_way_out()
    {
        var html = await ShellCaller.RenderAsync<NavMenu>(role: null);
        var links = Hrefs(html);

        Assert.Contains("sign-in", links);
        Assert.Contains("register", links);
        Assert.Contains(string.Empty, links);

        // The other half of the guard: a visitor with no session must not be offered a profile
        // they cannot open, a fleet screen they cannot reach, or a sign-out that would do nothing.
        Assert.DoesNotContain("profile", links);
        Assert.DoesNotContain("drivers", links);
        Assert.DoesNotContain("vehicles", links);
        Assert.DoesNotContain("deliveries", links);
        Assert.DoesNotContain("my-deliveries", links);
        Assert.DoesNotContain("/sign-out", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(UserRole.Admin)]
    [InlineData(UserRole.Client)]
    public async Task The_brand_is_rendered_for_every_caller(UserRole? role)
    {
        var html = await ShellCaller.RenderAsync<NavMenu>(role);
        var brand = SharedMarkup.ElementWithClass(html, "a", "navbar-brand");

        Assert.Contains("<svg", brand, StringComparison.Ordinal);
        Assert.Contains("DriveTrack", brand, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(UserRole.Admin)]
    [InlineData(UserRole.Dispatcher)]
    [InlineData(UserRole.Driver)]
    [InlineData(UserRole.Client)]
    public async Task The_landing_page_points_a_signed_in_caller_at_their_own_landing_route(UserRole role)
    {
        // FR-99, asserted against LandingRoute's own answer rather than against "/profile", so it
        // keeps testing the right thing on the day a role gets a dashboard.
        var html = await ShellCaller.RenderAsync<Home>(role);
        var links = Hrefs(html);

        Assert.Contains(LandingRoute.For(role), links);
        Assert.DoesNotContain("sign-in", links);
        Assert.DoesNotContain("register", links);
    }

    [Fact]
    public async Task The_landing_page_offers_an_anonymous_visitor_both_entry_points_with_icons()
    {
        var html = await ShellCaller.RenderAsync<Home>(role: null);
        var links = Hrefs(html);

        Assert.Contains("sign-in", links);
        Assert.Contains("register", links);

        // NFR-24: each call to action carries an icon AND its text.
        foreach (var target in new[] { "sign-in", "register" })
        {
            var cta = Regex.Match(
                html,
                $"<a[^>]*href=\"{target}\"[^>]*>(?<body>.*?)</a>",
                RegexOptions.Singleline,
                TimeSpan.FromSeconds(5));

            Assert.True(cta.Success, $"The landing page has no link to '{target}'.");
            Assert.Contains("<svg", cta.Groups["body"].Value, StringComparison.Ordinal);

            var text = SharedMarkup.TextOf(cta.Groups["body"].Value);

            Assert.True(SharedMarkup.IsUkrainian(text), $"The '{target}' action reads '{text}'.");
        }
    }

    /// <summary>Every <c>href</c> in the rendered markup.</summary>
    private static string[] Hrefs(string html) =>
    [
        .. Regex.Matches(html, "href=\"(?<href>[^\"]*)\"", RegexOptions.None, TimeSpan.FromSeconds(5))
            .Select(match => match.Groups["href"].Value),
    ];

    [Fact]
    public void The_navigation_still_collapses_behind_its_toggler_on_a_narrow_viewport()
    {
        // NFR-22 for the shell. The toggler rule also holds NFR-29's single recorded literal-colour
        // exemption, which DesignTokenTests asserts is the only one - so it is left byte-identical
        // and this only checks that the mechanism it drives is still there.
        var stylesheet = SharedMarkup.ReadComponent("Layout", "NavMenu.razor.css");

        Assert.Contains(".navbar-toggler:checked ~ .nav-scrollable", stylesheet, StringComparison.Ordinal);
        Assert.Contains("M4 7h22M4 15h22M4 23h22", stylesheet, StringComparison.Ordinal);
    }
}
