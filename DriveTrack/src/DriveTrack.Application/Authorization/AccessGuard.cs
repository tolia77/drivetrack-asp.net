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
}
