using DriveTrack.Application.Authorization;
using DriveTrack.Application.Common;
using DriveTrack.Domain.Identity;

namespace DriveTrack.Application.Tests.Authorization;

/// <summary>
/// AD-2's one decision, and AD-4's admin override, asserted directly rather than through an
/// endpoint — the guard is the thing under test, and a test that reached it over HTTP would be
/// asserting the pipeline as much as the rule.
/// </summary>
public class AccessGuardTests
{
    private static readonly UserId Target = new(7);

    [Fact]
    public void An_anonymous_caller_is_unauthenticated_not_forbidden()
    {
        // 401, not 403, and deliberately so: FR-13's session-expiry flow branches on this single
        // code, and an expired cookie on a live circuit arrives here.
        var guard = new AccessGuard(new StubCurrentUser());

        var failure = Assert.Throws<ForbiddenException>(() => guard.RequireSelf(Target));

        Assert.Equal(ErrorCode.AUTH_UNAUTHENTICATED, failure.Code);
    }

    [Fact]
    public void A_different_non_admin_caller_is_forbidden()
    {
        var guard = new AccessGuard(new StubCurrentUser(new UserId(8), UserRole.Client));

        var failure = Assert.Throws<ForbiddenException>(() => guard.RequireSelf(Target));

        Assert.Equal(ErrorCode.AUTH_FORBIDDEN, failure.Code);
    }

    [Theory]
    [InlineData(UserRole.Client)]
    [InlineData(UserRole.Driver)]
    [InlineData(UserRole.Dispatcher)]
    public void The_caller_reading_their_own_row_passes(UserRole role)
    {
        var guard = new AccessGuard(new StubCurrentUser(Target, role));

        guard.RequireSelf(Target);
    }

    [Fact]
    public void An_admin_reading_another_user_passes()
    {
        // AD-4: admin satisfies every check by rule, never by holding a second role row. This is the
        // only place in the system where that is true, which is what keeps it auditable.
        var guard = new AccessGuard(new StubCurrentUser(new UserId(8), UserRole.Admin));

        guard.RequireSelf(Target);
    }

    [Fact]
    public void An_anonymous_caller_asking_for_a_role_is_unauthenticated_not_forbidden()
    {
        // Same 401-not-403 reasoning as RequireSelf, and it has to be stated separately: a second
        // member that answered 403 here would send an expired session down FR-13's wrong branch.
        var guard = new AccessGuard(new StubCurrentUser());

        var failure = Assert.Throws<ForbiddenException>(() => guard.RequireRole(UserRole.Dispatcher));

        Assert.Equal(ErrorCode.AUTH_UNAUTHENTICATED, failure.Code);
    }

    [Theory]
    [InlineData(UserRole.Dispatcher)]
    [InlineData(UserRole.Driver)]
    [InlineData(UserRole.Client)]
    public void A_caller_holding_the_role_passes(UserRole role)
    {
        var guard = new AccessGuard(new StubCurrentUser(Target, role));

        guard.RequireRole(role);
    }

    [Theory]
    [InlineData(UserRole.Admin)]
    [InlineData(UserRole.Dispatcher)]
    [InlineData(UserRole.Driver)]
    [InlineData(UserRole.Client)]
    public void An_admin_passes_every_role_check(UserRole required)
    {
        // AD-4, on the new member. This is why FR-48 needs no second guard member and why no
        // capability writes `RequireRole(Dispatcher) || RequireRole(Admin)`: `RequireRole(Dispatcher)`
        // already means "a dispatcher or an admin", decided here and nowhere else.
        var guard = new AccessGuard(new StubCurrentUser(new UserId(8), UserRole.Admin));

        guard.RequireRole(required);
    }

    [Theory]
    [InlineData(UserRole.Client, UserRole.Dispatcher)]
    [InlineData(UserRole.Driver, UserRole.Dispatcher)]
    [InlineData(UserRole.Dispatcher, UserRole.Admin)]
    [InlineData(UserRole.Client, UserRole.Admin)]
    public void A_caller_holding_another_role_is_forbidden(UserRole held, UserRole required)
    {
        // The row of the matrix that matters most: a dispatcher asked for Admin is refused, which
        // is what keeps every write on both administration surfaces admin-only. The two client and
        // driver rows are the fleet's whole authorization story - they are refused here, by the
        // guard, and not by a controller attribute.
        var guard = new AccessGuard(new StubCurrentUser(Target, held));

        var failure = Assert.Throws<ForbiddenException>(() => guard.RequireRole(required));

        Assert.Equal(ErrorCode.AUTH_FORBIDDEN, failure.Code);
    }

    [Fact]
    public void Holding_the_row_being_addressed_does_not_satisfy_a_role_check()
    {
        // The two members answer different questions, and conflating them is the failure worth
        // pinning: being the user an operation is about says nothing about being allowed to
        // perform an operation reserved to a role.
        var guard = new AccessGuard(new StubCurrentUser(Target, UserRole.Client));

        guard.RequireSelf(Target);

        Assert.Throws<ForbiddenException>(() => guard.RequireRole(UserRole.Dispatcher));
    }

    [Fact]
    public void Reading_a_caller_that_is_not_there_throws_rather_than_answering_user_zero()
    {
        // The reason ICurrentUser throws instead of returning a default: default(UserId) is user
        // zero, and an ownership check against it would compare equal to nothing and look like it
        // had been made.
        var anonymous = new StubCurrentUser();

        Assert.Throws<InvalidOperationException>(() => anonymous.UserId);
        Assert.Throws<InvalidOperationException>(() => anonymous.Role);
    }

    // =====================================================================================
    // RequireRole (story 4.1)
    //
    // The role member, tested directly for the same reason RequireSelf is: an endpoint test would
    // be asserting the pipeline as much as the rule, and the rule is what the fleet capability
    // rests on.
    // =====================================================================================

    [Fact]
    public void An_anonymous_caller_asked_for_a_role_is_unauthenticated_not_forbidden()
    {
        // 401, not 403, exactly as RequireSelf answers it: FR-13's session-expiry flow branches on
        // this single code, and an expired cookie on a live circuit arrives here too.
        var guard = new AccessGuard(new StubCurrentUser());

        var failure = Assert.Throws<ForbiddenException>(() => guard.RequireRole(UserRole.Dispatcher));

        Assert.Equal(ErrorCode.AUTH_UNAUTHENTICATED, failure.Code);
    }

    [Theory]
    [InlineData(UserRole.Dispatcher)]
    [InlineData(UserRole.Driver)]
    [InlineData(UserRole.Client)]
    public void The_caller_holding_the_required_role_passes(UserRole role)
    {
        var guard = new AccessGuard(new StubCurrentUser(Target, role));

        guard.RequireRole(role);
    }

    [Theory]
    [InlineData(UserRole.Dispatcher)]
    [InlineData(UserRole.Driver)]
    [InlineData(UserRole.Client)]
    public void No_role_but_admin_satisfies_a_check_for_admin(UserRole role)
    {
        // The other direction of AD-4, and the one the matrix beside it reads as covered without
        // being: admin passing every check is not the same claim as every other role failing the
        // check for admin. Without this a guard that returned early for any authenticated caller
        // would satisfy every other case in this file.
        var guard = new AccessGuard(new StubCurrentUser(Target, role));

        var failure = Assert.Throws<ForbiddenException>(() => guard.RequireRole(UserRole.Admin));

        Assert.Equal(ErrorCode.AUTH_FORBIDDEN, failure.Code);
    }

    /// <summary>
    /// A caller with no adapter behind it. The guard depends on the port, not on a
    /// <c>ClaimsPrincipal</c>, which is what makes these five tests possible without a host.
    /// </summary>
    private sealed class StubCurrentUser : ICurrentUser
    {
        private readonly UserId? _userId;
        private readonly UserRole? _role;

        public StubCurrentUser()
        {
        }

        public StubCurrentUser(UserId userId, UserRole role, DriverId? driverId = null, ClientId? clientId = null)
        {
            _userId = userId;
            _role = role;
            DriverId = driverId;
            ClientId = clientId;
        }

        public bool IsAuthenticated => _userId is not null;

        public UserId UserId => _userId ?? throw new InvalidOperationException("Anonymous caller.");

        public UserRole Role => _role ?? throw new InvalidOperationException("Anonymous caller.");

        public DriverId? DriverId { get; }

        public ClientId? ClientId { get; }
    }
}
