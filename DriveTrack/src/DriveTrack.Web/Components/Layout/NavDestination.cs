using DriveTrack.Domain.Identity;

namespace DriveTrack.Web.Components.Layout;

/// <summary>
/// A place the navigation can offer to go. One member per routed destination the shell links.
/// </summary>
public enum NavDestination
{
    /// <summary>The landing page, which every caller may see (FR-99).</summary>
    Home,

    /// <summary>The signed-in caller's own profile.</summary>
    Profile,

    /// <summary>The client roster (FR-46, FR-48).</summary>
    Clients,

    /// <summary>The dispatcher roster (FR-49).</summary>
    Dispatchers,
}

/// <summary>
/// FR-77: which destinations a role is offered, kept as a table rather than as markup conditionals.
/// <para>
/// FR-12 first, because it is the part that is easy to get wrong: hiding a link is a convenience,
/// never the authorization decision. <c>IAccessGuard</c> inside the service settles whether a caller
/// may do a thing, and it runs whether or not the link was ever rendered. This table only decides
/// what is worth showing.
/// </para>
/// <para>
/// The rows stopped being uniform with story 7.1, which is what the table was written for. An admin
/// administers both rosters; a dispatcher is offered the client roster because FR-48 gives them a
/// reason to open it, and not the dispatcher roster, which is admin-only work; a driver and a client
/// are offered neither.
/// </para>
/// </summary>
internal static class NavDestinations
{
    /// <summary>What an administrator is offered: every destination that exists.</summary>
    private static readonly IReadOnlySet<NavDestination> Administrator =
        new HashSet<NavDestination>
        {
            NavDestination.Home,
            NavDestination.Profile,
            NavDestination.Clients,
            NavDestination.Dispatchers,
        };

    /// <summary>What a dispatcher is offered: FR-48's roster, and nothing that administers accounts.</summary>
    private static readonly IReadOnlySet<NavDestination> Dispatcher =
        new HashSet<NavDestination>
        {
            NavDestination.Home,
            NavDestination.Profile,
            NavDestination.Clients,
        };

    /// <summary>What everyone else with a session is offered.</summary>
    private static readonly IReadOnlySet<NavDestination> SignedIn =
        new HashSet<NavDestination> { NavDestination.Home, NavDestination.Profile };

    /// <summary>
    /// What a caller with no session is offered. The landing page is <c>[AllowAnonymous]</c>, so it
    /// is the one destination that survives having no role at all; the sign-in and registration
    /// entry points are account actions rather than destinations and are rendered beside these.
    /// </summary>
    public static IReadOnlySet<NavDestination> Anonymous { get; } =
        new HashSet<NavDestination> { NavDestination.Home };

    /// <summary>The destinations a caller of that role is offered.</summary>
    /// <param name="role">The caller's single role (AD-4).</param>
    public static IReadOnlySet<NavDestination> For(UserRole role) => role switch
    {
        UserRole.Admin => Administrator,
        UserRole.Dispatcher => Dispatcher,
        UserRole.Driver => SignedIn,
        UserRole.Client => SignedIn,

        // Total, like LandingRoute: a fifth role added without a navigation decision fails loudly
        // here rather than silently rendering a menu with nothing in it.
        _ => throw new ArgumentOutOfRangeException(
            nameof(role),
            role,
            "No navigation destinations are mapped for this role."),
    };
}
