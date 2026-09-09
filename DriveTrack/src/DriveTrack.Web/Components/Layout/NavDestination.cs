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

    /// <summary>The driver roster, offered to a dispatcher or an admin (FR-35, FR-77).</summary>
    Drivers,

    /// <summary>The vehicle fleet, offered to a dispatcher or an admin (FR-40, FR-77).</summary>
    Vehicles,
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
/// Story 4.1 is the first to make the table do anything: the fleet screens are offered to a
/// dispatcher and an admin and to nobody else, so the rows are no longer uniform. A driver and a
/// client are still offered what every signed-in caller has. That the switch was already written
/// out in full is why this was a row rather than another <c>@if</c> in <c>NavMenu.razor</c>, which
/// is how the baseline's navigation grew.
/// </para>
/// </summary>
internal static class NavDestinations
{
    /// <summary>What every signed-in caller is offered, whatever their role.</summary>
    private static readonly IReadOnlySet<NavDestination> Personal =
        new HashSet<NavDestination> { NavDestination.Home, NavDestination.Profile };

    /// <summary>
    /// The personal destinations plus the fleet, for the two roles that run dispatch. FR-12 still
    /// holds: this decides what is worth showing, and <c>IAccessGuard</c> decides who may do it.
    /// <para>
    /// Built from <see cref="Personal"/> rather than restating its members: a destination added
    /// there later must reach these two roles as well, and a second copy of the list is exactly the
    /// drift this table exists to prevent.
    /// </para>
    /// </summary>
    private static readonly IReadOnlySet<NavDestination> Fleet =
        new HashSet<NavDestination>(Personal) { NavDestination.Drivers, NavDestination.Vehicles };

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
        UserRole.Admin => Fleet,
        UserRole.Dispatcher => Fleet,
        UserRole.Driver => Personal,
        UserRole.Client => Personal,

        // Total, like LandingRoute: a fifth role added without a navigation decision fails loudly
        // here rather than silently rendering a menu with nothing in it.
        _ => throw new ArgumentOutOfRangeException(
            nameof(role),
            role,
            "No navigation destinations are mapped for this role."),
    };
}
