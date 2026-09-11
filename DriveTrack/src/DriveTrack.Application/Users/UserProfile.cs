using DriveTrack.Domain.Identity;

namespace DriveTrack.Application.Users;

/// <summary>
/// A user's own profile (FR-5). A DTO, so <c>ApplicationUser</c>, <c>Client</c> and <c>Driver</c>
/// never cross an adapter boundary (AD-17).
/// </summary>
/// <param name="UserId">The user this profile describes.</param>
/// <param name="FirstName">Given name.</param>
/// <param name="LastName">Family name.</param>
/// <param name="Email">The address the account signs in with.</param>
/// <param name="Role">The single role (AD-4).</param>
/// <param name="LicenseNumber">
/// The driving licence number, present only when <paramref name="Role"/> is
/// <see cref="UserRole.Driver"/> — one of the two fields a subtype row contributes to this shape.
/// </param>
/// <param name="PhoneNumber">
/// The contact number in E.164 form, present only when <paramref name="Role"/> is
/// <see cref="UserRole.Client"/> — DR-3 gives no other role a row to store one in. It is here
/// because FR-87 lets a client edit it, and a screen cannot offer to edit what it cannot show.
/// </param>
public sealed record UserProfile(
    UserId UserId,
    string FirstName,
    string LastName,
    string Email,
    UserRole Role,
    string? LicenseNumber,
    string? PhoneNumber);
