namespace DriveTrack.Web.Account;

/// <summary>
/// FR-79 / FR-80: where a caller the route view refused is sent.
/// <para>
/// The two cases are not the same failure and must not share an answer. An anonymous visitor has
/// something to do about it — sign in — so they are sent to the sign-in page. A caller who is
/// already signed in and still refused has nothing to do about it, and sending them to sign in tells
/// them they are not signed in, which is false and which they will act on by signing in again.
/// </para>
/// <para>
/// Isolated as a pure function for the same reason <see cref="LandingRoute"/> is: the decision is
/// then assertable without a browser, a circuit or a rendered page.
/// </para>
/// </summary>
internal static class AuthFailureRoute
{
    /// <summary>Where to send a caller <c>AuthorizeRouteView</c> refused.</summary>
    /// <param name="isAuthenticated">True when the refused caller already has a session.</param>
    public static string For(bool isAuthenticated) => isAuthenticated ? "/access-denied" : "/sign-in";
}
