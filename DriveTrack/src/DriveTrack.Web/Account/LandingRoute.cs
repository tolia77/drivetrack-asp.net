using DriveTrack.Domain.Identity;

namespace DriveTrack.Web.Account;

/// <summary>
/// FR-7: where a role lands after signing in, decided in one place.
/// <para>
/// Today every role lands on <c>/profile</c>, because it is the only signed-in screen that exists —
/// the dashboards belong to later epics. Writing the switch out in full anyway is what makes those
/// epics a one-line change here instead of a search for redirect literals.
/// </para>
/// </summary>
internal static class LandingRoute
{
    /// <summary>The route a signed-in user of that role is sent to.</summary>
    public static string For(UserRole role) => role switch
    {
        UserRole.Admin => "/profile",
        UserRole.Dispatcher => "/profile",
        UserRole.Driver => "/profile",
        UserRole.Client => "/profile",

        // Total, like the status map: a fifth role added without a landing screen fails loudly here
        // rather than silently sending someone to the home page.
        _ => throw new ArgumentOutOfRangeException(
            nameof(role),
            role,
            "No landing route is mapped for this role."),
    };
}
