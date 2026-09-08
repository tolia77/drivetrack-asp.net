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
/// The set is uniform across roles today because <c>Profile</c> is the only authenticated screen
/// that exists — the dashboards, delivery lists and fleet screens are Epics 4 and later. Writing the
/// switch out in full anyway is what makes those epics a row here instead of another
/// <c>@if</c> in <c>NavMenu.razor</c>, which is how the baseline's navigation grew.
/// </para>
/// </summary>
internal static class NavDestinations
{
    /// <summary>Everything an authenticated caller can currently reach.</summary>
    private static readonly IReadOnlySet<NavDestination> Everything =
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
        UserRole.Admin => Everything,
        UserRole.Dispatcher => Everything,
        UserRole.Driver => Everything,
        UserRole.Client => Everything,

        // Total, like LandingRoute: a fifth role added without a navigation decision fails loudly
        // here rather than silently rendering a menu with nothing in it.
        _ => throw new ArgumentOutOfRangeException(
            nameof(role),
            role,
            "No navigation destinations are mapped for this role."),
    };
}
