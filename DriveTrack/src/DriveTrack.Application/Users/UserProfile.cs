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
/// <see cref="UserRole.Driver"/> — the one field a subtype row contributes to this shape.
/// </param>
public sealed record UserProfile(
    UserId UserId,
    string FirstName,
    string LastName,
    string Email,
    UserRole Role,
    string? LicenseNumber);
