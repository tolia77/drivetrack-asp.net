using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using DriveTrack.Application.Deliveries;
using DriveTrack.Integration.Tests.Architecture;
using DriveTrack.Integration.Tests.Persistence;
using DriveTrack.Integration.Tests.Support;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace DriveTrack.Integration.Tests.Authorization;

/// <summary>
/// What makes "every endpoint" true tomorrow rather than only today.
/// <para>
/// The matrix is a table somebody wrote. Read on its own it proves that the endpoints in it behave;
/// it cannot prove that it holds every endpoint there is. These assertions close it in both
/// directions against the host's own <see cref="EndpointDataSource"/> — the routing table the
/// application actually serves — so a controller action added without a row fails the build naming
/// the route, and a row that names no route fails it too.
/// </para>
/// <para>
/// The anonymous allowlist is closed the same way. <c>Program.cs</c> registers
/// <c>AddAuthorization()</c> with no fallback policy and puts no <c>RequireAuthorization</c> on the
/// component endpoints, so an endpoint with no authorization metadata is anonymous by omission
/// rather than by decision — which is precisely the kind of thing that arrives silently. Pinning
/// the endpoints that carry <see cref="IAllowAnonymous"/>, the endpoints that carry no
/// authorization metadata at all, and the handful of routes that are neither REST nor screen, is
/// what turns that from a property of today's source into a claim a future edit has to argue with.
/// </para>
/// </summary>
public class EndpointInventoryTests(PostgresFixture postgres)
{
    /// <summary>
    /// The routes that are neither a REST endpoint nor a screen, pinned by name.
    /// <para>
    /// <c>sign-out</c> is deliberately reachable without credentials: clearing a cookie has to work
    /// for a session that has already expired, and its cross-site protection is the antiforgery
    /// token it validates by hand (FR-6). The two hub routes carry <c>ChatHub</c>'s own
    /// <c>[Authorize]</c>, naming both schemes, rather than authorization on the route —
    /// <c>HubBoundaryTests</c> is where that is asserted over the wire.
    /// </para>
    /// </summary>
    private static readonly string[] NonMatrixRoutes = ["hubs/chat", "hubs/chat/negotiate", "sign-out"];

    [Fact]
    public async Task Every_rest_endpoint_the_host_publishes_has_a_row_in_the_matrix()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var world = await AuthorizationWorld.InstanceAsync(postgres.ConnectionString, cancellationToken);

        var published = RestEndpoints(world).SelectMany(Keys).ToHashSet(StringComparer.Ordinal);
        var declared = EndpointMatrix.Rows.Select(row => row.Key).ToHashSet(StringComparer.Ordinal);

        // Both directions, the way GuardCoverageTests closes PublicEntryPoints: an endpoint with no
        // row is an unswept surface, and a row with no endpoint is a rule about a route that used
        // to exist.
        Assert.Empty(published.Except(declared, StringComparer.Ordinal).Order(StringComparer.Ordinal));
        Assert.Empty(declared.Except(published, StringComparer.Ordinal).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task The_anonymous_rest_allowlist_is_exactly_registration_and_sign_in()
    {
        // AD-2's allowlist on the HTTP side. PublicEntryPoints closes the Application half; this is
        // a different claim - a controller can be decorated [AllowAnonymous] without any service
        // method changing at all, and the coverage gate would never see it.
        var cancellationToken = TestContext.Current.CancellationToken;
        var world = await AuthorizationWorld.InstanceAsync(postgres.ConnectionString, cancellationToken);

        var anonymous = RestEndpoints(world)
            .Where(endpoint => endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .SelectMany(Keys)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["POST api/auth/register", "POST api/auth/sign-in"], anonymous);
    }

    [Fact]
    public async Task No_rest_endpoint_is_anonymous_merely_because_nobody_wrote_an_attribute()
    {
        // The other half of the same claim, and the one a fallback policy would otherwise be needed
        // for: an endpoint under /api or /proof-assets carrying neither [Authorize] nor
        // [AllowAnonymous] is open, and nothing about the source says so.
        var cancellationToken = TestContext.Current.CancellationToken;
        var world = await AuthorizationWorld.InstanceAsync(postgres.ConnectionString, cancellationToken);

        var unguarded = RestEndpoints(world)
            .Where(endpoint => endpoint.Metadata.GetMetadata<IAllowAnonymous>() is null)
            .Where(endpoint => endpoint.Metadata.GetMetadata<IAuthorizeData>() is null)
            .SelectMany(Keys)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(unguarded);
    }

    [Fact]
    public async Task The_routes_that_are_neither_rest_nor_screen_are_the_three_that_were_argued_for()
    {
        // Everything this application maps that is not a controller action, not the asset route and
        // not a Blazor page. Framework plumbing - static assets, the fallback, the circuit's own
        // /_blazor endpoints - is excluded by what it is rather than by name, so a future framework
        // adding another one of those does not fail this. A third route of *ours*, though - a
        // webhook, a health probe, a second minimal API - cannot arrive without editing this line.
        var cancellationToken = TestContext.Current.CancellationToken;
        var world = await AuthorizationWorld.InstanceAsync(postgres.ConnectionString, cancellationToken);

        var ours = Endpoints(world)
            .OfType<RouteEndpoint>()
            .Where(endpoint => !IsRest(endpoint))
            .Where(endpoint => !IsScreen(endpoint))
            .Where(endpoint => !IsFrameworkPlumbing(endpoint))

            // The probe controllers ApiFactory adds as an application part are dropped by their
            // declaring assembly, and nothing else is. IsRest admits controllers declared in
            // DriveTrack.Web, so a product controller that arrived in some *other* assembly would
            // be neither a REST endpoint above nor a probe here - and dropping every remaining
            // controller action would have let it through both gates in silence, which is the one
            // thing this file exists to prevent.
            .Where(endpoint =>
                endpoint.Metadata.GetMetadata<ControllerActionDescriptor>() is not { } action
                || action.ControllerTypeInfo.Assembly != typeof(ApiFactory).Assembly)
            .Select(Template)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(NonMatrixRoutes.Order(StringComparer.Ordinal).ToArray(), ours);
    }

    [Fact]
    public void The_one_unguarded_application_component_is_reachable_from_no_adapter()
    {
        // The guard gate's stated blind spot, asserted from the other side. GuardCoverageTests pins
        // that DeliverySideEffectRunner is the only public Application component the IL scan does
        // not cover; what makes that safe rather than merely declared is that nothing in the
        // adapter can reach it - its one caller is the hosted worker in Infrastructure. The
        // Application test project references no Web assembly, so the claim is made here.
        //
        // Read from compiled metadata rather than through reflection over loaded types: a type
        // nobody calls leaves no trace in an object graph, and a TypeRef row is exactly the trace a
        // reference does leave.
        // The interface as well as the class, and that is the half that matters. An adapter reaches
        // the runner by injecting IDeliverySideEffectRunner and calling RunAsync, which emits a
        // TypeRef named for the *interface* - so a scan that looked only for the concrete name
        // would stay green while the Web assembly called the one unguarded component in the system.
        var names = new[] { nameof(DeliverySideEffectRunner), nameof(IDeliverySideEffectRunner) };

        var infrastructure = ReferencedTypeNames(LayerAssemblies.Resolve("DriveTrack.Infrastructure"));
        var web = ReferencedTypeNames(LayerAssemblies.Resolve("DriveTrack.Web"));

        foreach (var name in names)
        {
            // The positive control: the scan can see a reference where there is one - the worker
            // names the interface and the registration names the class - so each assertion below is
            // an absence rather than a broken reader.
            Assert.Contains(name, infrastructure, StringComparer.Ordinal);
            Assert.DoesNotContain(name, web, StringComparer.Ordinal);
        }
    }

    /// <summary>Every type an assembly names in another assembly, by simple name.</summary>
    private static IReadOnlyCollection<string> ReferencedTypeNames(Assembly assembly)
    {
        using var stream = File.OpenRead(assembly.Location);
        using var reader = new PEReader(stream);

        var metadata = reader.GetMetadataReader();

        return metadata.TypeReferences
            .Select(handle => metadata.GetString(metadata.GetTypeReference(handle).Name))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static IEnumerable<Endpoint> Endpoints(AuthorizationWorld world) =>
        world.Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints;

    /// <summary>
    /// The caller-facing REST surface: this product's controllers plus the proof-asset route.
    /// <para>
    /// The probe controllers <c>ApiFactory</c> adds as an application part are excluded by their
    /// declaring assembly. They exist so the Epic 1 wire-contract tests have something to call and
    /// ship in no container, so a matrix carrying rows for them would be a matrix of the test suite
    /// rather than of the product.
    /// </para>
    /// </summary>
    private static IEnumerable<RouteEndpoint> RestEndpoints(AuthorizationWorld world) =>
        Endpoints(world).OfType<RouteEndpoint>().Where(IsRest);

    private static bool IsRest(RouteEndpoint endpoint)
    {
        if (endpoint.Metadata.GetMetadata<ControllerActionDescriptor>() is { } action)
        {
            return action.ControllerTypeInfo.Assembly == LayerAssemblies.Resolve("DriveTrack.Web");
        }

        return Template(endpoint).StartsWith("proof-assets/", StringComparison.Ordinal);
    }

    /// <summary>
    /// True for an endpoint <c>MapRazorComponents</c> produced for a routable page.
    /// <para>
    /// Matched on the metadata's type name rather than on the type: several of the framework's
    /// endpoint metadata types are internal, and a test that could only be written against the
    /// public ones would have to guess at the rest.
    /// </para>
    /// </summary>
    private static bool IsScreen(RouteEndpoint endpoint) => HasMetadata(endpoint, "ComponentTypeMetadata");

    /// <summary>
    /// True for the framework's own routes: compiled static assets, the static-file fallback, and
    /// the endpoints the interactive circuit mounts for itself.
    /// </summary>
    private static bool IsFrameworkPlumbing(RouteEndpoint endpoint)
    {
        var template = Template(endpoint);

        return HasMetadata(endpoint, "StaticAssetDescriptor")
            || HasMetadata(endpoint, "FallbackMetadata")
            || template.StartsWith("_blazor", StringComparison.Ordinal)
            || template.StartsWith("_framework", StringComparison.Ordinal);
    }

    private static bool HasMetadata(Endpoint endpoint, string typeName) =>
        endpoint.Metadata.Any(item =>
            string.Equals(item?.GetType().Name, typeName, StringComparison.Ordinal));

    private static string Template(RouteEndpoint endpoint) =>
        endpoint.RoutePattern.RawText?.TrimStart('/') ?? string.Empty;

    /// <summary>
    /// One key per verb the endpoint declares, not just the first.
    /// <para>
    /// An action written <c>[HttpGet] [HttpHead]</c>, or a <c>MapMethods</c> route naming two, is
    /// two caller-facing endpoints on one route. Keying only the first would let the rest reach the
    /// pipeline with no row in the matrix and the gate still green.
    /// </para>
    /// </summary>
    private static IEnumerable<string> Keys(RouteEndpoint endpoint)
    {
        var verbs = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
        var template = Template(endpoint);

        return verbs is null or { Count: 0 }
            ? ["ANY " + template]
            : verbs.Select(verb => verb + " " + template);
    }
}
