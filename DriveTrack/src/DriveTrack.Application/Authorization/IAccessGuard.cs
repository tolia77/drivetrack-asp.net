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
/// One member per question, and no more (NFR-6). <c>RequireSelf</c> answers "is this the caller's
/// own row"; <c>RequireRole</c> answers "is this caller one of these people"; <c>RequireScope</c>
/// answers "which rows of a collection may this caller be shown"; <c>RequireAssignedDriver</c>
/// answers "is this the driver carrying this parcel, or dispatch". Each arrived with the first
/// capability that had a caller for it — the scope with story 5.1, the assignment with 5.3 — and
/// none is a rewording of another: none of the first three can express "the assigned driver, or
/// dispatch, but never the client whose delivery it is", and writing that as a role test inside a
/// service would be a second place authorization lived. AD-4's "admin satisfies every check" lives
/// inside the implementation, once, so every member inherits it and none restates it.
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

    /// <summary>
    /// Answers which rows of a collection this caller may be shown — AD-3's scope predicate, and
    /// the reason a collection repository method takes a scope and has no overload that omits it.
    /// <para>
    /// The guard decides, not the service: "a driver sees only their own deliveries" is an
    /// authorization rule, and a rule written as a <c>Where</c> inside a capability would be a
    /// second place authorization lived. A service takes the answer to the repository unchanged.
    /// </para>
    /// <para>
    /// AD-4 applies here as it does to the other two members: an admin is unrestricted by rule,
    /// decided inside the implementation and nowhere else. A dispatcher is unrestricted because
    /// dispatch is the role the whole collection exists for (FR-18).
    /// </para>
    /// </summary>
    /// <returns>
    /// The narrowing to apply. Both ids null is the unrestricted case: the scope narrows nothing
    /// and the repository adds no <c>WHERE</c>.
    /// </returns>
    /// <exception cref="Common.ForbiddenException">
    /// The caller is anonymous (<c>AUTH_UNAUTHENTICATED</c>, 401), or holds a role whose scope is a
    /// row id the caller's claims do not carry (<c>AUTH_FORBIDDEN</c>, 403) — a driver with no
    /// driver claim cannot be narrowed to their own rows, and answering "unrestricted" there would
    /// disclose the whole collection.
    /// </exception>
    AccessScope RequireScope();

    /// <summary>
    /// Requires that the caller is the driver a delivery is assigned to, or runs dispatch (FR-26,
    /// FR-34).
    /// <para>
    /// The one predicate behind AD-10's single write path. A delivery's status may be advanced by
    /// the driver carrying it and by the people who dispatch it, and by nobody else — a client may
    /// never change the status of their own delivery (FR-90), which is exactly why
    /// <see cref="RequireScope"/> cannot express this: a client's scope admits their own row.
    /// </para>
    /// <para>
    /// An unassigned delivery has no driver, so no driver passes: a null
    /// <paramref name="assignedDriverId"/> never equals a caller's driver row id, and answering
    /// otherwise would let any driver advance a parcel nobody is carrying.
    /// </para>
    /// </summary>
    /// <param name="assignedDriverId">
    /// The driver the delivery is assigned to, or null when it is unassigned — or when there is no
    /// delivery at all, which is why the caller may pass this before answering 404: a driver who may
    /// not act on the row is refused before learning whether it exists.
    /// </param>
    /// <exception cref="Common.ForbiddenException">
    /// The caller is anonymous (<c>AUTH_UNAUTHENTICATED</c>, 401), or is a driver who is not the
    /// assigned one, or holds a role with no part in the lifecycle (<c>AUTH_FORBIDDEN</c>, 403).
    /// </exception>
    void RequireAssignedDriver(DriverId? assignedDriverId);
}
