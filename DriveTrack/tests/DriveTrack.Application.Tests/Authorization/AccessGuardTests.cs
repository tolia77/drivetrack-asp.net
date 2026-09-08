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
    public void Reading_a_caller_that_is_not_there_throws_rather_than_answering_user_zero()
    {
        // The reason ICurrentUser throws instead of returning a default: default(UserId) is user
        // zero, and an ownership check against it would compare equal to nothing and look like it
        // had been made.
        var anonymous = new StubCurrentUser();

        Assert.Throws<InvalidOperationException>(() => anonymous.UserId);
        Assert.Throws<InvalidOperationException>(() => anonymous.Role);
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
