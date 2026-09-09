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
/// Two members, and no more (NFR-6). <c>RequireSelf</c> answers "is this the caller's own row";
/// <c>RequireRole</c> answers "is this caller one of these people". A scope predicate arrives with
/// the first capability that has a caller for it, exactly as the role member did. AD-4's "admin
/// satisfies every check" lives inside the implementation, once, so both members inherit it and
/// neither restates it.
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

    /// <summary>
    /// Requires that the caller holds <paramref name="role"/>, or holds
    /// <see cref="UserRole.Admin"/>.
    /// <para>
    /// <c>RequireRole(Dispatcher)</c> therefore reads as "a dispatcher or an admin", which is what
    /// FR-48 asks for and what the original's <c>require_role("dispatcher")</c> meant. The override
    /// is not repeated at the call site, because it is not a property of any one capability.
    /// </para>
    /// </summary>
    /// <param name="role">The role the operation is reserved to.</param>
    /// <exception cref="Common.ForbiddenException">
    /// The caller is anonymous (<c>AUTH_UNAUTHENTICATED</c>, 401) or holds neither that role nor
    /// <see cref="UserRole.Admin"/> (<c>AUTH_FORBIDDEN</c>, 403).
    /// </exception>
    void RequireRole(UserRole role);
}
