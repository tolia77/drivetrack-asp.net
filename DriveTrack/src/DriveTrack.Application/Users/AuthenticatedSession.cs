using DriveTrack.Domain.Identity;

namespace DriveTrack.Application.Users;

/// <summary>
/// What a successful registration or sign-in returns (FR-1, FR-4): who the caller now is, and the
/// bearer token the REST adapter will accept from them.
/// <para>
/// A DTO, not an entity (AD-17). The Blazor adapter ignores <see cref="AccessToken"/> and writes an
/// authentication cookie instead — the same session, carried by whichever credential the adapter
/// deals in. The subtype row ids travel here for exactly that reason: the cookie has to be able to
/// carry the same claims the bearer token does, or the same person would come back as a different
/// <c>ICurrentUser</c> depending on which adapter they arrived through.
/// </para>
/// </summary>
/// <param name="UserId">The signed-in user.</param>
/// <param name="FirstName">Given name, for greeting them.</param>
/// <param name="LastName">Family name.</param>
/// <param name="Email">The address they signed in with.</param>
/// <param name="Role">Their single role (AD-4), which decides their landing route (FR-7).</param>
/// <param name="AccessToken">The signed bearer token for <c>/api/*</c>.</param>
/// <param name="DriverId">The driver row id, or null when the user is not a driver.</param>
/// <param name="ClientId">The client row id, or null when the user is not a client.</param>
public sealed record AuthenticatedSession(
    UserId UserId,
    string FirstName,
    string LastName,
    string Email,
    UserRole Role,
    string AccessToken,
    DriverId? DriverId,
    ClientId? ClientId);
