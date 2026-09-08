namespace DriveTrack.Application.Authorization;

/// <summary>
/// The claim names both authentication schemes issue and both read back.
/// <para>
/// They live in Application because the cookie scheme is written by the Blazor adapter and the
/// bearer token by Infrastructure, and the two must agree exactly: a caller signed in through one
/// and read back through the other has to come out as the same <see cref="ICurrentUser"/>. Putting
/// the names in the layer both reference is what makes that agreement a compile-time fact rather
/// than a pair of string literals somebody has to keep in step.
/// </para>
/// <para>
/// These are names, not types: nothing here names <c>ClaimsPrincipal</c>, so AD-22's ban on the
/// adapter's caller shape leaking into Application still holds.
/// </para>
/// </summary>
public static class DriveTrackClaimTypes
{
    /// <summary>The user row's id, as an invariant integer string.</summary>
    public const string UserId = "drivetrack/user-id";

    /// <summary>The single role (AD-4), as a <see cref="Domain.Identity.UserRole"/> member name.</summary>
    public const string Role = "drivetrack/role";

    /// <summary>The driver row's id. Absent unless the user is a driver.</summary>
    public const string DriverId = "drivetrack/driver-id";

    /// <summary>The client row's id. Absent unless the user is a client.</summary>
    public const string ClientId = "drivetrack/client-id";

    /// <summary>The display name, so the shell can greet a signed-in user without a round trip.</summary>
    public const string Name = "drivetrack/name";

    /// <summary>The sign-in address.</summary>
    public const string Email = "drivetrack/email";
}
