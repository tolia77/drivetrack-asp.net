using System.Reflection;
using DriveTrack.Domain.Identity;
using DriveTrack.Integration.Tests.Architecture;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;

// The guard's member names are referred to with nameof rather than with strings a rename would
// leave behind; the alias only keeps the reference short at the call sites below.
using Guard = DriveTrack.Application.Authorization.IAccessGuard;

namespace DriveTrack.Integration.Tests.Authorization;

/// <summary>How a screen refuses a caller it does not serve.</summary>
internal enum ScreenRefusal
{
    /// <summary>
    /// Nobody is refused. FR-99's landing page, the two account forms, and the three dead ends a
    /// refused or lost caller is sent to — which must not themselves refuse anyone, or a refusal
    /// becomes a loop.
    /// </summary>
    None,

    /// <summary>
    /// <c>[Authorize(Roles = …)]</c>: the router refuses before the component is instantiated, and
    /// the caller is sent to <c>/access-denied</c>.
    /// </summary>
    Router,

    /// <summary>
    /// A bare <c>[Authorize]</c>, on purpose. The page renders for any signed-in caller and the
    /// service it calls is what refuses — pinned by <c>AdministrationScreenTests</c> and deliberately
    /// not changed here (AD-2). The refusal itself is asserted where the capability is reachable:
    /// on the REST route that calls the same service method, in <c>EndpointMatrix</c>, for five of
    /// the six — and over the hub, in <c>HubBoundaryTests</c>, for <c>/chat</c>, whose
    /// <c>ChatService</c> has no REST route at all and therefore no row in that table.
    /// </summary>
    Service,
}

/// <summary>
/// One row per routable page, and the two-directional gate that keeps the table honest.
/// <para>
/// A screen's <c>@attribute</c> is a decision nobody can audit by reading one file: the absence of
/// one reads exactly like the absence of a need for one, and <c>Program.cs</c> registers no fallback
/// policy and puts no <c>RequireAuthorization</c> on <c>MapRazorComponents</c> — so a page with no
/// attribute is anonymous, silently. The table below is the statement of intent, and
/// <c>ScreenBoundaryTests</c> holds it against both the reflected routes and the live host.
/// </para>
/// </summary>
internal static class ScreenMatrix
{
    /// <summary>The six pages an anonymous visitor may reach, and there are exactly six.</summary>
    public static readonly string[] AnonymousRoutes =
        ["/", "/Error", "/access-denied", "/not-found", "/register", "/sign-in"];

    /// <summary>Every routable page, one row each.</summary>
    public static readonly IReadOnlyList<ScreenRow> Rows =
    [
        // ---- FR-99, FR-1, FR-4, FR-79, FR-80: the pages with no caller to judge. -------------
        Open("/"),
        Open("/sign-in"),
        Open("/register"),
        Open("/access-denied"),
        Open("/not-found"),
        Open("/Error"),

        // ---- Refused at the router, by the role list on the page itself. ---------------------
        Router("/deliveries", UserRole.Dispatcher, UserRole.Admin),
        Router("/drivers", UserRole.Dispatcher, UserRole.Admin),
        Router("/vehicles", UserRole.Dispatcher, UserRole.Admin),
        Router("/shifts", UserRole.Dispatcher, UserRole.Admin),
        Router("/my-shifts", UserRole.Driver),
        Router("/my-deliveries", UserRole.Driver, UserRole.Client),

        // ---- Bare [Authorize] on purpose: the service is the refusal (AD-2). -----------------
        // Each row names the capability the first load calls and the guard member inside it that
        // does the refusing, so "the service refuses instead" is a checkable pairing rather than a
        // sentence in a comment.
        Service("/profile", "UserService.GetProfileAsync", nameof(Guard.RequireSelf)),
        Service("/chat", "ChatService.ListThreadsAsync", nameof(Guard.RequireChatParticipant)),
        Service("/reviews", "ReviewService.ListAsync", nameof(Guard.RequireRole)),
        Service("/clients", "ClientAdministrationService.ListAsync", nameof(Guard.RequireRole)),
        Service("/dispatchers", "DispatcherAdministrationService.ListAsync", nameof(Guard.RequireRole)),
        Service("/notifications", "NotificationLogService.ListAsync", nameof(Guard.RequireRole)),
    ];

    /// <summary>
    /// The routes the Web assembly actually publishes: <c>[Route]</c> on a component type.
    /// <para>
    /// Reflection over the assembly rather than a scan of the <c>.razor</c> files, because
    /// <c>@page</c> is a source form and the attribute is what the router reads.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> ReflectedRoutes() =>
    [
        .. LayerAssemblies.Resolve("DriveTrack.Web")
            .GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false })
            .Where(type => typeof(IComponent).IsAssignableFrom(type))
            .SelectMany(type => type.GetCustomAttributes<RouteAttribute>())
            .Select(route => route.Template)
            .Order(StringComparer.Ordinal),
    ];

    /// <summary>The component type behind a route, for reading its authorization attributes.</summary>
    public static Type ComponentFor(string route) =>
        LayerAssemblies.Resolve("DriveTrack.Web")
            .GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false })
            .Where(type => typeof(IComponent).IsAssignableFrom(type))
            .Single(type => type.GetCustomAttributes<RouteAttribute>()
                .Any(attribute => string.Equals(attribute.Template, route, StringComparison.Ordinal)));

    /// <summary>The roles the page's own attribute names, or empty when it names none.</summary>
    public static IReadOnlyList<UserRole> DeclaredRoles(Type component) =>
    [
        .. component
            .GetCustomAttributes<AuthorizeAttribute>()
            .Select(attribute => attribute.Roles)
            .Where(roles => !string.IsNullOrWhiteSpace(roles))
            .SelectMany(roles => roles!.Split(','))
            .Select(role => Enum.Parse<UserRole>(role.Trim())),
    ];

    private static ScreenRow Open(string route) =>
        new(route, ScreenRefusal.None, [.. EndpointMatrix.Roles], Service: null, Guard: null);

    private static ScreenRow Router(string route, params UserRole[] allowed) =>
        new(route, ScreenRefusal.Router, allowed, Service: null, Guard: null);

    private static ScreenRow Service(string route, string service, string guard) =>
        new(route, ScreenRefusal.Service, [.. EndpointMatrix.Roles], service, guard);
}

/// <summary>One routable page, as the sweep reads it.</summary>
/// <param name="Route">The route template, exactly as the component's <c>[Route]</c> declares it.</param>
/// <param name="Refusal">Who takes the refusal: nobody, the router, or the service.</param>
/// <param name="Allowed">
/// The roles the page itself admits. Every one of the four for a page whose refusal is service-side:
/// the screen renders and the capability behind it answers, which is the decision
/// <c>AdministrationScreenTests</c> pins and this story leaves alone.
/// </param>
/// <param name="Service">
/// <c>Type.Method</c> of the Application method the page's first load calls, for a page whose
/// refusal is service-side. Null otherwise.
/// </param>
/// <param name="Guard">The <c>IAccessGuard</c> member that method calls. Null otherwise.</param>
internal sealed record ScreenRow(
    string Route,
    ScreenRefusal Refusal,
    IReadOnlyList<UserRole> Allowed,
    string? Service,
    string? Guard);
