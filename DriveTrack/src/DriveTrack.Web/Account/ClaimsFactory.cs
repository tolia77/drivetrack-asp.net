using System.Globalization;
using System.Security.Claims;
using DriveTrack.Application.Authorization;
using DriveTrack.Application.Users;

namespace DriveTrack.Web.Account;

/// <summary>
/// The one place that turns a session into the claims a scheme issues.
/// <para>
/// The bearer token is signed in Infrastructure and the cookie is written here, and the two must
/// carry the same claim names and the same values: a person signed in through the Blazor form and
/// the same person signed in through <c>/api/auth/sign-in</c> have to come back as the same
/// <see cref="ICurrentUser"/>. The names live in <c>DriveTrackClaimTypes</c>, in the layer both
/// sides reference; this file is the cookie half of that agreement.
/// </para>
/// </summary>
internal static class ClaimsFactory
{
    /// <summary>
    /// The identity's authentication type. It has to be non-empty for
    /// <c>ClaimsIdentity.IsAuthenticated</c> to be true, which is what <c>&lt;AuthorizeView&gt;</c>
    /// and <c>[Authorize]</c> read.
    /// </summary>
    public const string AuthenticationType = "DriveTrack";

    /// <summary>Builds the principal the cookie scheme signs in.</summary>
    public static ClaimsPrincipal Build(AuthenticatedSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        var claims = new List<Claim>
        {
            new(DriveTrackClaimTypes.UserId, session.UserId.Value.ToString(CultureInfo.InvariantCulture)),
            new(DriveTrackClaimTypes.Role, session.Role.ToString()),
            new(DriveTrackClaimTypes.Name, session.FirstName + " " + session.LastName),
            new(DriveTrackClaimTypes.Email, session.Email),
        };

        if (session.DriverId is { } driverId)
        {
            claims.Add(new Claim(
                DriveTrackClaimTypes.DriverId,
                driverId.Value.ToString(CultureInfo.InvariantCulture)));
        }

        if (session.ClientId is { } clientId)
        {
            claims.Add(new Claim(
                DriveTrackClaimTypes.ClientId,
                clientId.Value.ToString(CultureInfo.InvariantCulture)));
        }

        // The name and role claim types are named explicitly so User.Identity.Name and the role
        // checks read the claims this file writes rather than the framework's defaults, which are
        // the long WS-Federation URIs nothing here issues.
        var identity = new ClaimsIdentity(
            claims,
            AuthenticationType,
            DriveTrackClaimTypes.Name,
            DriveTrackClaimTypes.Role);

        return new ClaimsPrincipal(identity);
    }
}
