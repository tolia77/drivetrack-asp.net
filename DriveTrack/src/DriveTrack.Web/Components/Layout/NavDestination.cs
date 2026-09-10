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

    /// <summary>The client roster (FR-46, FR-48).</summary>
    Clients,

    /// <summary>The dispatcher roster (FR-49).</summary>
    Dispatchers,

    /// <summary>The dispatch board: every delivery, offered to a dispatcher or an admin (FR-18).</summary>
    Deliveries,

    /// <summary>
    /// The caller's own deliveries (FR-25). A personal destination, like the profile: it shows
    /// whatever the caller's own scope narrows to rather than a roster somebody administers.
    /// </summary>
    MyDeliveries,
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
    /// The personal destinations plus the caller's own deliveries, for the two roles that are a
    /// party to one (FR-25, FR-27): a driver sees what they carry, a client sees what they
    /// requested.
    /// <para>
    /// Not in <see cref="Personal"/>, and the distinction is the whole point of this table. A
    /// dispatcher and an admin are party to no delivery, so their scope narrows nothing and the
    /// screen would show them the whole board under a heading that says "mine" - which is why
    /// <c>MyDeliveries.razor</c> carries <c>[Authorize(Roles = "Driver,Client")]</c> and would send
    /// them to /access-denied. Offering a link that can only ever refuse is worse than not
    /// offering it: this table decides what is worth showing, and that is not.
    /// </para>
    /// </summary>
    private static readonly IReadOnlySet<NavDestination> Assigned =
        new HashSet<NavDestination>(Personal) { NavDestination.MyDeliveries };

    /// <summary>
    /// The personal destinations plus the fleet, for the two roles that run dispatch. FR-12 still
    /// holds: this decides what is worth showing, and <c>IAccessGuard</c> decides who may do it.
    /// <para>
    /// Built from <see cref="Personal"/> rather than restating its members: a destination added
    /// there later must reach these two roles as well, and a second copy of the list is exactly the
    /// drift this table exists to prevent.
    /// </para>
    /// </summary>
    private static readonly IReadOnlySet<NavDestination> Fleet = new HashSet<NavDestination>(Personal)
    {
        NavDestination.Drivers,
        NavDestination.Vehicles,

        // FR-18's dispatch board sits in this tier rather than the personal one: it shows every
        // delivery in the system, which is a thing to run rather than a thing to own.
        NavDestination.Deliveries,
    };

    /// <summary>
    /// What a dispatcher is offered: the fleet, plus FR-48's client roster, which they open to
    /// attach a client to a delivery. Not the dispatcher roster - administering accounts is
    /// admin-only work (FR-49).
    /// </summary>
    private static readonly IReadOnlySet<NavDestination> Dispatcher =
        new HashSet<NavDestination>(Fleet) { NavDestination.Clients };

    /// <summary>
    /// What an administrator is offered: everything a dispatcher is, plus the roster of
    /// dispatchers themselves. Built from <see cref="Dispatcher"/> for the reason
    /// <see cref="Fleet"/> is built from <see cref="Personal"/>: a destination added to either
    /// tier has to reach this one, and a restated list is how that stops happening.
    /// </summary>
    private static readonly IReadOnlySet<NavDestination> Administrator =
        new HashSet<NavDestination>(Dispatcher) { NavDestination.Dispatchers };

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
        UserRole.Driver => Assigned,
        UserRole.Client => Assigned,

        // Total, like LandingRoute: a fifth role added without a navigation decision fails loudly
        // here rather than silently rendering a menu with nothing in it.
        _ => throw new ArgumentOutOfRangeException(
            nameof(role),
            role,
            "No navigation destinations are mapped for this role."),
    };
}
