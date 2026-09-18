using System.Text.RegularExpressions;
using DriveTrack.Integration.Tests.Support;

namespace DriveTrack.Integration.Tests.Components;

/// <summary>
/// AD-14 and FR-78/79/80 pinned against regression: the shell's render mode, and the two dead-end
/// surfaces a routed application needs before it has any screens at all.
/// </summary>
public class ShellRoutingTests
{
    private static string WebProject => RepositoryLayout.ProjectDirectory("DriveTrack.Web");

    [Fact]
    public void The_shell_keeps_the_one_render_mode_with_prerendering_off()
    {
        // AD-14. Prerendering off is what makes FR-80's claim true: with it on, a protected page
        // renders once statically before the circuit exists, and that pass is a response body.
        var shell = SharedMarkup.ReadComponent("App.razor");

        Assert.Contains("new InteractiveServerRenderMode(prerender: false)", shell, StringComparison.Ordinal);
        Assert.Contains(@"<Routes @rendermode=""PageRenderMode"" />", shell, StringComparison.Ordinal);
        Assert.Contains(@"<HeadOutlet @rendermode=""PageRenderMode"" />", shell, StringComparison.Ordinal);

        // App.razor itself stays statically rendered: it is the document, and a document that waits
        // on a circuit has nothing to show while it waits.
        var directive = Regex.Match(
            shell,
            @"^\s*@rendermode\b",
            RegexOptions.Multiline,
            TimeSpan.FromSeconds(5));

        Assert.False(directive.Success, "App.razor declares a @rendermode of its own.");
    }

    [Fact]
    public void Neither_authorization_fragment_can_render_the_protected_component()
    {
        // FR-80. A route view or an @Body inside either fragment would put the protected screen's
        // markup into the response of the very request that refused it.
        var routes = SharedMarkup.ReadComponent("Routes.razor");

        foreach (var fragment in new[] { "Authorizing", "NotAuthorized" })
        {
            var body = Between(routes, $"<{fragment}>", $"</{fragment}>");

            Assert.DoesNotContain("RouteView", body, StringComparison.Ordinal);
            Assert.DoesNotContain("@Body", body, StringComparison.Ordinal);
            Assert.DoesNotContain("routeData", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_refused_caller_is_routed_by_the_function_that_knows_the_difference()
    {
        var routes = SharedMarkup.ReadComponent("Routes.razor");

        Assert.Contains("<RedirectOnAuthFailure />", routes, StringComparison.Ordinal);

        // The superseded component is gone rather than left beside its replacement: two redirects
        // with different answers is worse than either.
        Assert.False(
            File.Exists(Path.Combine(
                SharedMarkup.ComponentsDirectory, "Account", "RedirectToSignIn.razor")),
            "RedirectToSignIn.razor still exists beside RedirectOnAuthFailure.");
    }

    [Fact]
    public void The_cookie_handler_sends_a_forbidden_caller_to_the_access_denied_page()
    {
        // The handler's own forbidden path, which is a second door into the same failure: a caller
        // refused by an [Authorize] attribute on a page goes through the route view, and one
        // refused by the handler goes through this. Both have to arrive somewhere that says so.
        var program = File.ReadAllText(Path.Combine(WebProject, "Program.cs"));

        Assert.Contains(@"options.AccessDeniedPath = ""/access-denied"";", program, StringComparison.Ordinal);
        Assert.Contains(@"options.LoginPath = ""/sign-in"";", program, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("NotFound.razor", "/not-found")]
    [InlineData("AccessDenied.razor", "/access-denied")]
    public void A_dead_end_page_has_a_heading_and_a_way_out(string fileName, string route)
    {
        var page = SharedMarkup.ReadComponent("Pages", fileName);

        Assert.Contains($@"@page ""{route}""", page, StringComparison.Ordinal);

        // <h1> rather than <h3>: `FocusOnNavigate Selector="h1"` in Routes.razor looks for one, and
        // on a page without it a keyboard user keeps the focus the previous screen had.
        Assert.Contains("<h1", page, StringComparison.Ordinal);

        // The way out. A dead end with no link back is what the baseline shipped.
        Assert.Contains("href=", page, StringComparison.Ordinal);
        Assert.Contains(@"@Localizer[""BackToHome""]", page, StringComparison.Ordinal);

        // And it is labelled, not a bare arrow (NFR-24).
        Assert.Contains("<Icon Name=\"IconName.Back\" />", page, StringComparison.Ordinal);
    }

    [Fact]
    public void The_access_denied_page_cannot_refuse_the_caller_it_exists_to_explain_things_to()
    {
        // Without [AllowAnonymous] the page is subject to the same decision that sent the caller
        // here, and the redirect loops.
        var page = SharedMarkup.ReadComponent("Pages", "AccessDenied.razor");

        Assert.Contains("@attribute [AllowAnonymous]", page, StringComparison.Ordinal);

        // ICurrentUser.Role throws for an anonymous caller, so the signed-in route back is read
        // only behind IsAuthenticated - and the anonymous branch has a route back of its own.
        Assert.Contains("@if (Caller.IsAuthenticated)", page, StringComparison.Ordinal);
        Assert.Contains("@if (!Caller.IsAuthenticated)", page, StringComparison.Ordinal);
        Assert.Contains("LandingRoute.For(Caller.Role)", page, StringComparison.Ordinal);
    }

    [Fact]
    public void The_landing_page_sends_a_signed_in_caller_where_their_role_lands()
    {
        // FR-99. The hardcoded `profile` this replaces was a second answer to a question
        // LandingRoute already owns, and the two would have diverged the first time a role got a
        // dashboard.
        var home = SharedMarkup.ReadComponent("Pages", "Home.razor");

        Assert.Contains("LandingRoute.For(Caller.Role)", home, StringComparison.Ordinal);
        Assert.DoesNotContain(@"href=""profile""", home, StringComparison.Ordinal);

        // NFR-24: every glyph on the page is a named one from the shared set, and there are seven.
        // Three are the calls to action - the signed-in way in, and the visitor's register and sign
        // in - and the other four belong to the design the page was rebuilt on: the hero's eyebrow
        // badge and one per role card. The illustration of the dispatch board carries none of its
        // own: the create action the design draws in its head was dropped, because painted in the
        // real create colour it was indistinguishable from the button that makes a delivery and sat,
        // on a phone, directly under the page's own call to action.
        //
        // Stated as a count for the reason the navigation's is: an action drawn without a glyph is
        // markup with nothing behind it, so a new one has to be a deliberate edit here rather than
        // an empty box nobody notices.
        Assert.Equal(7, SharedMarkup.Occurrences(home, "<Icon Name="));
    }

    private static string Between(string source, string opening, string closing)
    {
        var start = source.IndexOf(opening, StringComparison.Ordinal);

        Assert.True(start >= 0, $"Routes.razor has no {opening} fragment.");

        var end = source.IndexOf(closing, start, StringComparison.Ordinal);

        Assert.True(end > start, $"Routes.razor never closes its {opening} fragment.");

        return source[(start + opening.Length)..end];
    }
}
