using DriveTrack.Application.Common;
using DriveTrack.Domain.Identity;

namespace DriveTrack.Application.Authorization;

/// <summary>
/// AD-2's guard over AD-22's caller. The whole authorization vocabulary of this story is these few
/// lines, which is the point: one implementation, one admin override, one place to read when
/// asking who may do what.
/// </summary>
public sealed class AccessGuard(ICurrentUser currentUser) : IAccessGuard
{
    /// <inheritdoc />
    public void RequireSelf(UserId userId)
    {
        if (!currentUser.IsAuthenticated)
        {
            // 401, not 403: FR-13's session-expiry flow branches on this single code, and an
            // expired cookie on a live circuit reaches exactly here.
            throw new ForbiddenException(
                ErrorCode.AUTH_UNAUTHENTICATED,
                "An anonymous caller attempted an operation reserved to user "
                    + userId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".");
        }

        // AD-4: admin passes by rule, never by holding a second role row. This is the only place in
        // the system that says so.
        if (currentUser.Role == UserRole.Admin)
        {
            return;
        }

        if (currentUser.UserId == userId)
        {
            return;
        }

        throw new ForbiddenException(
            ErrorCode.AUTH_FORBIDDEN,
            "User "
                + currentUser.UserId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " attempted an operation reserved to user "
                + userId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".");
    }

    /// <inheritdoc />
    public void RequireRole(UserRole role)
    {
        if (!currentUser.IsAuthenticated)
        {
            // 401 for the same reason RequireSelf answers 401: an expired cookie on a live circuit
            // is not a caller who lacks permission, it is a caller who has none left.
            throw new ForbiddenException(
                ErrorCode.AUTH_UNAUTHENTICATED,
                "An anonymous caller attempted an operation reserved to role " + role + ".");
        }

        // AD-4 again, and still the only place: admin passes by rule, so no capability has to
        // remember to write `RequireRole(Dispatcher) || RequireRole(Admin)`.
        if (currentUser.Role == UserRole.Admin)
        {
            return;
        }

        if (currentUser.Role == role)
        {
            return;
        }

        throw new ForbiddenException(
            ErrorCode.AUTH_FORBIDDEN,
            "User "
                + currentUser.UserId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " holds role " + currentUser.Role
                + " and attempted an operation reserved to role " + role + ".");
    }

    /// <inheritdoc />
    public AccessScope RequireScope()
    {
        if (!currentUser.IsAuthenticated)
        {
            // 401 for the same reason the other two members answer 401: an expired cookie on a
            // live circuit is a caller who has no credentials left, not one who lacks permission.
            throw new ForbiddenException(
                ErrorCode.AUTH_UNAUTHENTICATED,
                "An anonymous caller asked for the rows they are allowed to see.");
        }

        // AD-4 once more, and still the only place it is written: an admin is unrestricted by rule.
        // A dispatcher is unrestricted because dispatch is the role the collection exists for.
        if (currentUser.Role is UserRole.Admin or UserRole.Dispatcher)
        {
            return new AccessScope(null, null);
        }

        if (currentUser.Role == UserRole.Driver)
        {
            // Refused rather than widened. A driver whose claims carry no driver row id cannot be
            // narrowed to their own rows, and the only other answer available - unrestricted -
            // would hand them the whole collection on the strength of a missing claim.
            return currentUser.DriverId is { } driverId
                ? new AccessScope(driverId, null)
                : throw new ForbiddenException(
                    ErrorCode.AUTH_FORBIDDEN,
                    "User "
                        + currentUser.UserId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + " holds role Driver and carries no driver row id, so the rows they may "
                        + "see cannot be determined.");
        }

        if (currentUser.Role == UserRole.Client)
        {
            return currentUser.ClientId is { } clientId
                ? new AccessScope(null, clientId)
                : throw new ForbiddenException(
                    ErrorCode.AUTH_FORBIDDEN,
                    "User "
                        + currentUser.UserId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + " holds role Client and carries no client row id, so the rows they may "
                        + "see cannot be determined.");
        }

        // Total over the enum, like the navigation table: a fifth role added without a scope
        // decision is refused here rather than silently reading as unrestricted.
        throw new ForbiddenException(
            ErrorCode.AUTH_FORBIDDEN,
            "No row scope is defined for role " + currentUser.Role + ".");
    }

    /// <inheritdoc />
    public void RequireAssignedDriver(DriverId? assignedDriverId)
    {
        if (!currentUser.IsAuthenticated)
        {
            // 401 for the reason every other member answers 401: an expired cookie on a live
            // circuit is a caller with no credentials left, and FR-13 branches on this one code.
            throw new ForbiddenException(
                ErrorCode.AUTH_UNAUTHENTICATED,
                "An anonymous caller attempted to act on a delivery's lifecycle.");
        }

        // AD-4 once more, in the one place it is ever written. A dispatcher stands beside the admin
        // here because FR-34 gives dispatch the lifecycle as much as it gives it the board.
        if (currentUser.Role is UserRole.Admin or UserRole.Dispatcher)
        {
            return;
        }

        if (currentUser.Role == UserRole.Driver)
        {
            // Both halves have to be present and equal. A null assignment never matches, so an
            // unassigned parcel - and a delivery that does not exist at all, which reaches here as
            // the same null - is refused rather than handed to whichever driver asked first.
            if (assignedDriverId is { } assigned && currentUser.DriverId == assigned)
            {
                return;
            }

            throw new ForbiddenException(
                ErrorCode.AUTH_FORBIDDEN,
                "User "
                    + currentUser.UserId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " holds role Driver and is not the driver this delivery is assigned to.");
        }

        // Total over the enum, like RequireScope: a Client lands here, and so would a fifth role
        // added without a decision about its part in the lifecycle. FR-90 is this line - a client
        // may add a note and read the timeline, and may never change a status.
        throw new ForbiddenException(
            ErrorCode.AUTH_FORBIDDEN,
            "Role " + currentUser.Role + " has no part in a delivery's status lifecycle.");
    }

    /// <inheritdoc />
    public ClientId? RequireDeliveryComposer()
    {
        if (!currentUser.IsAuthenticated)
        {
            // 401 for the reason every other member answers 401: an expired cookie on a live
            // circuit is a caller with no credentials left, and FR-13 branches on this one code.
            throw new ForbiddenException(
                ErrorCode.AUTH_UNAUTHENTICATED,
                "An anonymous caller attempted to compose a delivery.");
        }

        // AD-4 once more, and still decided in this one file. A dispatcher stands beside the admin
        // because composing a delivery is what FR-14 gives dispatch. Null is the honest answer to
        // "which client row": they compose for somebody else, and the client is a field of the
        // command they send rather than a property of who they are.
        if (currentUser.Role is UserRole.Admin or UserRole.Dispatcher)
        {
            return null;
        }

        if (currentUser.Role == UserRole.Client)
        {
            // Refused rather than widened, exactly as RequireScope refuses a claimless caller. The
            // only other answer available here is null, which is dispatch's answer - so widening
            // would let an account with a broken claim compose a delivery attached to nobody.
            return currentUser.ClientId is { } clientId
                ? clientId
                : throw new ForbiddenException(
                    ErrorCode.AUTH_FORBIDDEN,
                    "User "
                        + currentUser.UserId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + " holds role Client and carries no client row id, so the row a request "
                        + "would be attached to cannot be determined.");
        }

        // Total over the enum, like RequireScope and RequireAssignedDriver. A Driver lands here -
        // FR-25 makes them a reader of deliveries and never an author of one - and so would a fifth
        // role added without a decision about whether it composes anything.
        throw new ForbiddenException(
            ErrorCode.AUTH_FORBIDDEN,
            "Role " + currentUser.Role + " does not compose deliveries.");
    }

    /// <inheritdoc />
    public void RequireChatParticipant(DriverId? thread)
    {
        if (!currentUser.IsAuthenticated)
        {
            // 401 for the reason every other member answers 401: an expired cookie on a live
            // circuit is a caller with no credentials left, and FR-13 branches on this one code.
            throw new ForbiddenException(
                ErrorCode.AUTH_UNAUTHENTICATED,
                "An anonymous caller attempted to reach a driver's conversation.");
        }

        // FR-68 and FR-71: dispatch runs every conversation, so a dispatcher passes for the roster
        // (a null thread) and for each thread alike.
        if (currentUser.Role == UserRole.Dispatcher)
        {
            return;
        }

        if (currentUser.Role == UserRole.Driver)
        {
            // DriverId? against DriverId?, and both halves have to be present and equal. A null
            // thread is the roster, which a driver has no use for and is not offered; a driver
            // whose claims carry no driver row id matches no thread and is refused rather than
            // widened, exactly as RequireScope refuses them.
            if (thread is { } requested && currentUser.DriverId == requested)
            {
                return;
            }

            throw new ForbiddenException(
                ErrorCode.AUTH_FORBIDDEN,
                "User "
                    + currentUser.UserId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " holds role Driver and is not a participant in that conversation.");
        }

        // Total over the enum, and the one place AD-4's admin override deliberately does not apply.
        // A Client lands here because chat has two participants and a client is not one of them; an
        // Admin lands here because the PRD says so - "admins moderate rather than dispatch" - which
        // is a product decision rather than a gap, and is why there is no Admin arm above.
        throw new ForbiddenException(
            ErrorCode.AUTH_FORBIDDEN,
            "Role " + currentUser.Role + " is not a participant in chat.");
    }
}
