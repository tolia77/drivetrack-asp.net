using DriveTrack.Domain.Identity;

namespace DriveTrack.Application.Authorization;

/// <summary>
/// The single place a business authorization decision is made (AD-2).
/// <para>
/// Every public method on an Application service calls this, except the closed allowlist in
/// <see cref="PublicEntryPoints"/>; <c>GuardCoverageTests</c> fails the build when a method does
/// neither. A controller attribute, a component condition or a repository filter is never the
/// decision — those are conveniences layered on top of one that was already taken here.
/// </para>
/// <para>
/// It throws rather than returning a boolean, so a forgotten <c>if</c> cannot be mistaken for a
/// passed check.
/// </para>
/// <para>
/// One member, deliberately (NFR-6). <c>RequireSelf</c> is the only decision this story's services
/// need; a role member or a scope predicate arrives with the first capability that has a caller
/// for it. AD-4's "admin satisfies every check" lives inside the implementation, so the override
/// is still in exactly one place when a second member appears.
/// </para>
/// </summary>
public interface IAccessGuard
{
    /// <summary>
    /// Requires that the caller is <paramref name="userId"/>, or holds <see cref="UserRole.Admin"/>.
    /// </summary>
    /// <param name="userId">The user the operation is about.</param>
    /// <exception cref="Common.ForbiddenException">
    /// The caller is anonymous (<c>AUTH_UNAUTHENTICATED</c>, 401) or is a different, non-admin user
    /// (<c>AUTH_FORBIDDEN</c>, 403).
    /// </exception>
    void RequireSelf(UserId userId);
}
