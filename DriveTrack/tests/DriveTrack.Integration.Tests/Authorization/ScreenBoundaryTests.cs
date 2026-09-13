using System.Net;
using System.Reflection;
using DriveTrack.Application;
using DriveTrack.Application.Authorization;
using DriveTrack.Domain.Identity;
using DriveTrack.Integration.Tests.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Testing;

namespace DriveTrack.Integration.Tests.Authorization;

/// <summary>
/// The screen half of the sweep: every routable page, and who is turned away from it.
/// <para>
/// Asserted on what the caller is told, not on which component decided it. Blazor applies a routable
/// component's <c>[Authorize]</c> as endpoint metadata, so a refused <c>GET</c> is answered before
/// the component is ever instantiated: every one of the twelve protected routes answers the cookie
/// handler's <c>302</c> to <c>/access-denied</c> when the caller is signed in and to <c>/sign-in</c>
/// when they are not (FR-79, FR-80), which was confirmed route by route and is what
/// <see cref="AssertSentToAsync"/> pins.
/// </para>
/// <para>
/// <c>Routes.razor</c> also carries a <c>NotAuthorized</c> fragment that redirects from inside the
/// circuit, and that path answers <c>200</c> with a navigation instead. It is unreachable while
/// prerendering is off (AD-14), so these tests deliberately do <em>not</em> accept it: a route that
/// started answering that way has changed how a refusal reaches the caller, and this suite is
/// meant to say so rather than to absorb it.
/// </para>
/// <para>
/// Driven over HTTP with a real <c>drivetrack.session</c> cookie, minted through the sign-in form,
/// which is the only thing in the product that writes one. A rendered component with a stubbed
/// principal would prove nothing about the router.
/// </para>
/// </summary>
public class ScreenBoundaryTests(PostgresFixture postgres)
{
    /// <summary>Every page in the table, by route.</summary>
    public static TheoryData<string> Routes
    {
        get
        {
            var data = new TheoryData<string>();

            foreach (var row in ScreenMatrix.Rows)
            {
                data.Add(row.Route);
            }

            return data;
        }
    }

    [Fact]
    public void Every_routable_page_has_a_row_and_every_row_names_a_page()
    {
        // The gate. A page that lands with no row is an unswept screen, and a row that names no
        // page is a rule about a screen that used to exist - the same two directions
        // GuardCoverageTests closes PublicEntryPoints in.
        var reflected = ScreenMatrix.ReflectedRoutes().ToHashSet(StringComparer.Ordinal);
        var declared = ScreenMatrix.Rows.Select(row => row.Route).ToHashSet(StringComparer.Ordinal);

        Assert.Empty(reflected.Except(declared, StringComparer.Ordinal).Order(StringComparer.Ordinal));
        Assert.Empty(declared.Except(reflected, StringComparer.Ordinal).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_pages_reachable_without_a_session_are_exactly_the_six_that_have_to_be()
    {
        // Read off the attributes rather than off the table, so this is a claim about the source
        // rather than a restatement of the row beside it. It matters because Program.cs registers
        // AddAuthorization() with no fallback policy and puts no RequireAuthorization on
        // MapRazorComponents: a page that simply forgot its attribute is anonymous, and nothing
        // about reading that page's source says so.
        var anonymous = ScreenMatrix.ReflectedRoutes()
            .Where(route => IsAnonymous(ScreenMatrix.ComponentFor(route)))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ScreenMatrix.AnonymousRoutes.Order(StringComparer.Ordinal).ToArray(), anonymous);
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public void The_role_list_on_the_page_is_the_one_the_table_states(string route)
    {
        var row = ScreenMatrix.Rows.Single(candidate =>
            string.Equals(candidate.Route, route, StringComparison.Ordinal));

        var declared = ScreenMatrix.DeclaredRoles(ScreenMatrix.ComponentFor(route));

        if (row.Refusal == ScreenRefusal.Router)
        {
            Assert.Equal(
                row.Allowed.Order().ToArray(),
                declared.Order().ToArray());

            return;
        }

        // The other two families carry no role list at all, and for the bare-[Authorize] family
        // that is a pinned product decision rather than an omission: the service is the refusal
        // (AD-2), and adding Roles= here would be a second, coarser copy of a rule the capability
        // already states precisely.
        Assert.Empty(declared);
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task An_anonymous_caller_is_sent_to_sign_in_or_served_an_open_page(string route)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var world = await AuthorizationWorld.InstanceAsync(postgres.ConnectionString, cancellationToken);
        var row = ScreenMatrix.Rows.Single(candidate =>
            string.Equals(candidate.Route, route, StringComparison.Ordinal));

        using var client = Client(world);
        using var response = await GetAsync(client, route, cookie: null, cancellationToken);

        if (row.Refusal == ScreenRefusal.None)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            return;
        }

        await AssertSentToAsync(response, "/sign-in", cancellationToken);
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task A_wrong_role_gets_no_screen_content_and_is_sent_to_access_denied(string route)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var world = await AuthorizationWorld.InstanceAsync(postgres.ConnectionString, cancellationToken);
        var row = ScreenMatrix.Rows.Single(candidate =>
            string.Equals(candidate.Route, route, StringComparison.Ordinal));

        using var client = Client(world);

        foreach (var role in EndpointMatrix.Roles)
        {
            using var response = await GetAsync(
                client, route, world.CookieFor(role), cancellationToken);

            if (row.Allowed.Contains(role))
            {
                // The other direction, and the reason the refusals below are not vacuous: a page
                // that turned everybody away would satisfy every assertion in this file.
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);

                continue;
            }

            await AssertSentToAsync(response, "/access-denied", cancellationToken);
        }
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public void A_page_whose_refusal_is_service_side_names_the_capability_that_takes_it(string route)
    {
        // That the row names something real, rather than that the naming is right. Three checks,
        // and it is worth being exact about their reach: the Application type and method exist, the
        // method is not on AD-2's anonymous allowlist, and IAccessGuard declares a member of the
        // named name.
        //
        // What this does NOT check is that the method calls that member - naming the wrong guard on
        // a row would keep this green. Doing better needs the IL walk in GuardCoverageTests, which
        // lives in the Application test project. The behaviour is pinned there and on the REST twin
        // in EndpointMatrix; the Guard column here is a pointer to those, not a third assertion.
        var row = ScreenMatrix.Rows.Single(candidate =>
            string.Equals(candidate.Route, route, StringComparison.Ordinal));

        if (row.Refusal != ScreenRefusal.Service)
        {
            Assert.Null(row.Service);
            Assert.Null(row.Guard);

            return;
        }

        var parts = row.Service!.Split('.');

        var type = ApplicationAssembly.Assembly
            .GetTypes()
            .Single(candidate => string.Equals(candidate.Name, parts[0], StringComparison.Ordinal));

        Assert.NotNull(type.GetMethod(parts[1], BindingFlags.Public | BindingFlags.Instance));
        Assert.DoesNotContain(row.Service, PublicEntryPoints.Methods, StringComparer.Ordinal);
        Assert.NotNull(typeof(IAccessGuard).GetMethod(row.Guard!));
    }

    private static HttpClient Client(AuthorizationWorld world) =>
        world.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            // Nothing followed and no cookie jar: where the caller is sent is the assertion, and a
            // jar would quietly carry one role's session into the next case.
            AllowAutoRedirect = false,
            HandleCookies = false,
        });

    private static async Task<HttpResponseMessage> GetAsync(
        HttpClient client,
        string route,
        string? cookie,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(route, UriKind.Relative));

        if (cookie is not null)
        {
            request.Headers.Add("Cookie", cookie);
        }

        return await client.SendAsync(request, cancellationToken);
    }

    /// <summary>
    /// The refusal as a caller experiences it: no screen, and a destination.
    /// </summary>
    /// <remarks>
    /// Every refused screen request in this product is answered <c>302</c> with a <c>Location</c>
    /// header — the router applies the page's <c>[Authorize]</c> as endpoint metadata, so the cookie
    /// handler's <c>LoginPath</c> or <c>AccessDeniedPath</c> answers before the component is ever
    /// instantiated. Both halves are asserted rather than only the destination: falling back to the
    /// body would let a <c>200</c> serving a real page satisfy this the moment that page happened to
    /// link to <c>/sign-in</c>.
    /// </remarks>
    private static async Task AssertSentToAsync(
        HttpResponseMessage response,
        string destination,
        CancellationToken cancellationToken)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var location = response.Headers.Location?.OriginalString;

        Assert.NotNull(location);
        Assert.Contains(destination, location, StringComparison.Ordinal);

        // FR-80: the protected component's markup must not reach the response while the decision is
        // being made or after it has gone against the caller. Prerendering is off (AD-14) and
        // neither authorization fragment renders a route view, so there is nothing to leak - and a
        // body with a heading in it is exactly what a regression here would produce.
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.DoesNotContain("<h1", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<main", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when nothing about the page demands a caller — whether it says so with
    /// <c>[AllowAnonymous]</c> or by carrying no attribute at all, which the host treats the same
    /// way and which is the case worth catching.
    /// </summary>
    /// <remarks>
    /// <c>[AllowAnonymous]</c> is read first because it wins: a page carrying both attributes is
    /// served to anybody, so classifying it by the presence of <c>[Authorize]</c> alone would call
    /// an open screen protected.
    /// </remarks>
    private static bool IsAnonymous(Type component) =>
        component.GetCustomAttribute<AllowAnonymousAttribute>() is not null
        || component.GetCustomAttribute<AuthorizeAttribute>() is null;
}
