using DriveTrack.Domain.Identity;

namespace DriveTrack.Application.Users;

/// <summary>
/// The account capability (FR-1, FR-4, FR-5). Three methods, and no more: registration, sign-in and
/// a user reading their own profile are everything this story ships.
/// <para>
/// Two of the three are in <see cref="Authorization.PublicEntryPoints"/> because an anonymous caller
/// performs them; the third calls <see cref="Authorization.IAccessGuard"/>. There is no fourth
/// option — <c>GuardCoverageTests</c> fails the build for a public method that is neither.
/// </para>
/// </summary>
public interface IUserService
{
    /// <summary>
    /// Opens a client account and returns the session it starts (FR-1). Anonymous by design.
    /// </summary>
    Task<AuthenticatedSession> RegisterClientAsync(
        RegisterClientCommand command,
        CancellationToken cancellationToken);

    /// <summary>Exchanges credentials for a session (FR-4). Anonymous by design.</summary>
    Task<AuthenticatedSession> SignInAsync(SignInCommand command, CancellationToken cancellationToken);

    /// <summary>
    /// Reads a user's profile (FR-5). The caller must be that user, or hold
    /// <see cref="UserRole.Admin"/>.
    /// </summary>
    Task<UserProfile> GetProfileAsync(UserId userId, CancellationToken cancellationToken);
}
