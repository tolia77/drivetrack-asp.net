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

    // =====================================================================================
    // RequireScope (story 5.1)
    //
    // AD-3's scope predicate, tested here for the reason the other two members are: an endpoint
    // test would assert the pipeline as much as the rule, and this rule is what stops a driver
    // reading somebody else's deliveries.
    // =====================================================================================

    [Fact]
    public void An_anonymous_caller_asking_for_a_scope_is_unauthenticated_not_forbidden()
    {
        // 401, not 403, exactly as the other two members answer it: an expired cookie on a live
        // circuit is a caller with no credentials left, and FR-13 branches on this single code.
        var guard = new AccessGuard(new StubCurrentUser());

        var failure = Assert.Throws<ForbiddenException>(() => _ = guard.RequireScope());

        Assert.Equal(ErrorCode.AUTH_UNAUTHENTICATED, failure.Code);
    }

    [Theory]
    [InlineData(UserRole.Admin)]
    [InlineData(UserRole.Dispatcher)]
    public void A_caller_who_runs_dispatch_is_unrestricted(UserRole role)
    {
        // AD-4 for the admin, and FR-18 for the dispatcher: the delivery board exists for them, so
        // the predicate narrows nothing and the repository sees no WHERE.
        var guard = new AccessGuard(new StubCurrentUser(Target, role));

        var scope = guard.RequireScope();

        Assert.Null(scope.DriverId);
        Assert.Null(scope.ClientId);
    }

    [Fact]
    public void A_driver_is_narrowed_to_their_own_driver_row_and_to_nothing_else()
    {
        var guard = new AccessGuard(
            new StubCurrentUser(Target, UserRole.Driver, driverId: new DriverId(4)));

        var scope = guard.RequireScope();

        Assert.Equal(new DriverId(4), scope.DriverId);

        // The other half, and the half a single-field assertion would miss: a driver's scope must
        // not also carry a client id, or the repository would add a second WHERE and answer an
        // empty page for every driver.
        Assert.Null(scope.ClientId);
    }

    [Fact]
    public void A_client_is_narrowed_to_their_own_client_row_and_to_nothing_else()
    {
        var guard = new AccessGuard(
            new StubCurrentUser(Target, UserRole.Client, clientId: new ClientId(9)));

        var scope = guard.RequireScope();

        Assert.Equal(new ClientId(9), scope.ClientId);
        Assert.Null(scope.DriverId);
    }

    [Theory]
    [InlineData(UserRole.Driver)]
    [InlineData(UserRole.Client)]
    public void A_caller_whose_row_id_is_missing_is_forbidden_rather_than_widened(UserRole role)
    {
        // The failure worth pinning, because the plausible-looking alternative is a disclosure: a
        // driver whose claims carry no driver row id cannot be narrowed, and the only other answer
        // available - unrestricted - would hand them every delivery in the system on the strength
        // of a missing claim.
        var guard = new AccessGuard(new StubCurrentUser(Target, role));

        var failure = Assert.Throws<ForbiddenException>(() => _ = guard.RequireScope());

        Assert.Equal(ErrorCode.AUTH_FORBIDDEN, failure.Code);
    }

    [Fact]
    public void A_driver_holding_a_client_id_is_still_scoped_by_their_driver_row()
    {
        // AD-4 gives an account one role, so this state should not arise - but the two claims are
        // separate, and a guard that read whichever it found first would scope a driver by a
        // client id and show them the wrong rows. The role decides which claim is the scope.
        var guard = new AccessGuard(new StubCurrentUser(
            Target,
            UserRole.Driver,
            driverId: new DriverId(4),
            clientId: new ClientId(9)));

        var scope = guard.RequireScope();

        Assert.Equal(new DriverId(4), scope.DriverId);
        Assert.Null(scope.ClientId);
    }

    // =====================================================================================
    // RequireAssignedDriver (story 5.3)
    //
    // FR-26 and FR-34 as one predicate: the driver carrying the parcel, or dispatch, and nobody
    // else. It gets the same table the other three members have, for the same reason - this is the
    // rule that stands between a driver and somebody else's delivery.
    // =====================================================================================

    [Fact]
    public void An_anonymous_caller_asking_about_an_assignment_is_unauthenticated_not_forbidden()
    {
        // 401, not 403, exactly as the other three members answer it: FR-13's session-expiry flow
        // branches on this single code, and an expired cookie on a live circuit arrives here too.
        var guard = new AccessGuard(new StubCurrentUser());

        var failure = Assert.Throws<ForbiddenException>(
            () => guard.RequireAssignedDriver(new DriverId(4)));

        Assert.Equal(ErrorCode.AUTH_UNAUTHENTICATED, failure.Code);
    }

    [Theory]
    [InlineData(UserRole.Admin)]
    [InlineData(UserRole.Dispatcher)]
    public void Dispatch_may_advance_any_delivery_including_an_unassigned_one(UserRole role)
    {
        // AD-4 for the admin and FR-34 for the dispatcher: the lifecycle is dispatch's as much as
        // the board is. An unassigned parcel is included deliberately - a delivery with no driver
        // still has to be movable by the people who dispatch it.
        var guard = new AccessGuard(new StubCurrentUser(Target, role));

        guard.RequireAssignedDriver(new DriverId(4));
        guard.RequireAssignedDriver(null);
    }

    [Fact]
    public void The_assigned_driver_may_advance_their_own_delivery()
    {
        var guard = new AccessGuard(
            new StubCurrentUser(Target, UserRole.Driver, driverId: new DriverId(4)));

        guard.RequireAssignedDriver(new DriverId(4));
    }

    [Fact]
    public void A_driver_may_not_advance_another_drivers_delivery()
    {
        // FR-26. The failure worth pinning, because the plausible-looking mistake - comparing the
        // caller's user id against the delivery's driver id - would compare two different row
        // spaces and pass for whichever driver happened to share a number with a user.
        var guard = new AccessGuard(
            new StubCurrentUser(Target, UserRole.Driver, driverId: new DriverId(4)));

        var failure = Assert.Throws<ForbiddenException>(
            () => guard.RequireAssignedDriver(new DriverId(5)));

        Assert.Equal(ErrorCode.AUTH_FORBIDDEN, failure.Code);
    }

    [Fact]
    public void A_driver_may_not_advance_an_unassigned_delivery()
    {
        // A null assignment must not read as "anyone's". It is also the shape a delivery that does
        // not exist arrives in - the service guards on the null row before answering 404 - so this
        // is what keeps a driver from probing for delivery ids.
        var guard = new AccessGuard(
            new StubCurrentUser(Target, UserRole.Driver, driverId: new DriverId(4)));

        var failure = Assert.Throws<ForbiddenException>(() => guard.RequireAssignedDriver(null));

        Assert.Equal(ErrorCode.AUTH_FORBIDDEN, failure.Code);
    }

    [Fact]
    public void A_driver_carrying_no_driver_row_id_is_refused_rather_than_widened()
    {
        // The same disclosure risk RequireScope refuses: a driver whose claims carry no driver row
        // id cannot be matched against any assignment, and the only other answer available - pass -
        // would hand them every delivery's lifecycle on the strength of a missing claim.
        var guard = new AccessGuard(new StubCurrentUser(Target, UserRole.Driver));

        var failure = Assert.Throws<ForbiddenException>(
            () => guard.RequireAssignedDriver(new DriverId(4)));

        Assert.Equal(ErrorCode.AUTH_FORBIDDEN, failure.Code);
    }

    [Fact]
    public void A_client_may_never_advance_a_delivery_even_their_own()
    {
        // FR-90, and the reason this member exists at all: a client's scope admits their own
        // delivery, so RequireScope would let them change its status. The two questions are
        // different, and only one of them is "may this caller move the parcel".
        var guard = new AccessGuard(
            new StubCurrentUser(Target, UserRole.Client, clientId: new ClientId(9)));

        var failure = Assert.Throws<ForbiddenException>(
            () => guard.RequireAssignedDriver(new DriverId(4)));

        Assert.Equal(ErrorCode.AUTH_FORBIDDEN, failure.Code);
    }

    // =====================================================================================
    // RequireChatParticipant (story 8.1)
    //
    // FR-68 to FR-76 as one predicate with a nullable argument: a value asks "may this caller work
    // in this driver's conversation", null asks "may they see the list of conversations at all".
    // The admin case below is the one a reader will assume is a bug, so it is pinned hardest.
    // =====================================================================================

    [Fact]
    public void An_anonymous_caller_asking_about_a_conversation_is_unauthenticated_not_forbidden()
    {
        // 401, not 403, exactly as the other four members answer it: FR-13's session-expiry flow
        // branches on this single code, and an expired cookie on a live circuit arrives here too.
        var guard = new AccessGuard(new StubCurrentUser());

        var failure = Assert.Throws<ForbiddenException>(
            () => guard.RequireChatParticipant(new DriverId(4)));

        Assert.Equal(ErrorCode.AUTH_UNAUTHENTICATED, failure.Code);

        // And the roster half of the same member, which is a different question and the same answer.
        Assert.Equal(
            ErrorCode.AUTH_UNAUTHENTICATED,
            Assert.Throws<ForbiddenException>(() => guard.RequireChatParticipant(null)).Code);
    }

    [Fact]
    public void A_dispatcher_reaches_the_roster_and_every_conversation()
    {
        // FR-68 and FR-71: dispatch runs every conversation, so neither argument refuses them.
        var guard = new AccessGuard(new StubCurrentUser(Target, UserRole.Dispatcher));

        guard.RequireChatParticipant(null);
        guard.RequireChatParticipant(new DriverId(4));
        guard.RequireChatParticipant(new DriverId(5));
    }

    [Fact]
    public void A_driver_reaches_their_own_conversation_and_no_other()
    {
        // FR-69. The failure worth pinning is the same one RequireAssignedDriver pins: the
        // plausible-looking mistake is comparing the caller's user id against the thread key, which
        // compares two different row spaces and passes for whichever driver shares a number.
        var guard = new AccessGuard(
            new StubCurrentUser(Target, UserRole.Driver, driverId: new DriverId(4)));

        guard.RequireChatParticipant(new DriverId(4));

        Assert.Equal(
            ErrorCode.AUTH_FORBIDDEN,
            Assert.Throws<ForbiddenException>(
                () => guard.RequireChatParticipant(new DriverId(5))).Code);
    }

    [Fact]
    public void A_driver_is_offered_no_roster_of_conversations()
    {
        // The null argument never equals a driver's row id, which is what makes one member answer
        // both questions without a second rule written anywhere.
        var guard = new AccessGuard(
            new StubCurrentUser(Target, UserRole.Driver, driverId: new DriverId(4)));

        var failure = Assert.Throws<ForbiddenException>(() => guard.RequireChatParticipant(null));

        Assert.Equal(ErrorCode.AUTH_FORBIDDEN, failure.Code);
    }

    [Fact]
    public void A_driver_carrying_no_driver_row_id_reaches_no_conversation_at_all()
    {
        // The same disclosure risk RequireScope and RequireAssignedDriver refuse: a driver whose
        // claims carry no driver row id matches no thread, and the only other answer available -
        // pass - would hand them every conversation on the strength of a missing claim.
        var guard = new AccessGuard(new StubCurrentUser(Target, UserRole.Driver));

        Assert.Equal(
            ErrorCode.AUTH_FORBIDDEN,
            Assert.Throws<ForbiddenException>(
                () => guard.RequireChatParticipant(new DriverId(4))).Code);
    }

    [Fact]
    public void A_client_is_not_a_participant_in_chat()
    {
        var guard = new AccessGuard(
            new StubCurrentUser(Target, UserRole.Client, clientId: new ClientId(9)));

        Assert.Equal(
            ErrorCode.AUTH_FORBIDDEN,
            Assert.Throws<ForbiddenException>(
                () => guard.RequireChatParticipant(new DriverId(4))).Code);

        Assert.Equal(
            ErrorCode.AUTH_FORBIDDEN,
            Assert.Throws<ForbiddenException>(() => guard.RequireChatParticipant(null)).Code);
    }

    [Fact]
    public void An_admin_is_refused_every_conversation_and_the_roster_as_well()
    {
        // The case a reader will take for a bug, and the reason this member exists at all. AD-4
        // makes an administrator satisfy every other check in the system by rule; the PRD locks
        // them out of chat on purpose - "admins moderate rather than dispatch" - and the original's
        // defect list records "chat accepts clients and admins" as a fault to fix.
        //
        // If this test ever fails because somebody "fixed" the missing Admin arm in AccessGuard,
        // the fix is to delete the arm again, not to change this line.
        var guard = new AccessGuard(new StubCurrentUser(Target, UserRole.Admin));

        Assert.Equal(
            ErrorCode.AUTH_FORBIDDEN,
            Assert.Throws<ForbiddenException>(
                () => guard.RequireChatParticipant(new DriverId(4))).Code);

        Assert.Equal(
            ErrorCode.AUTH_FORBIDDEN,
            Assert.Throws<ForbiddenException>(() => guard.RequireChatParticipant(null)).Code);
    }

    // =====================================================================================
    // RequireDeliveryComposer (story 7.4)
    //
    // FR-89 as one predicate: may this caller compose a delivery, and for which client row. It gets
    // the same table the other members have, including the missing-claim row - this is the rule that
    // decides whose name a new delivery is attached to.
    // =====================================================================================

    [Fact]
    public void An_anonymous_caller_composing_a_delivery_is_unauthenticated_not_forbidden()
    {
        // 401, not 403, exactly as every other member answers it: FR-13's session-expiry flow
        // branches on this single code, and an expired cookie on a live circuit arrives here too.
        var guard = new AccessGuard(new StubCurrentUser());

        var failure = Assert.Throws<ForbiddenException>(() => _ = guard.RequireDeliveryComposer());

        Assert.Equal(ErrorCode.AUTH_UNAUTHENTICATED, failure.Code);
    }

    [Theory]
    [InlineData(UserRole.Admin)]
    [InlineData(UserRole.Dispatcher)]
    public void Dispatch_composes_deliveries_for_somebody_else_and_is_answered_no_client_row(
        UserRole role)
    {
        // Null rather than a refusal, and the difference is the point of the member answering a
        // value at all. A dispatcher does compose deliveries - the client is an explicit field of
        // CreateDeliveryCommand - so the honest answer to "which client row" is "none of mine". The
        // request path turns that into its own refusal; the address search simply ignores it.
        var guard = new AccessGuard(new StubCurrentUser(Target, role));

        Assert.Null(guard.RequireDeliveryComposer());
    }

    [Fact]
    public void A_client_composes_for_their_own_row_and_the_guard_names_it()
    {
        // The whole reason the member answers a value: the id a request is attached to comes from
        // here and never from the payload, so a client cannot ask on another client's behalf.
        var guard = new AccessGuard(
            new StubCurrentUser(Target, UserRole.Client, clientId: new ClientId(9)));

        Assert.Equal(new ClientId(9), guard.RequireDeliveryComposer());
    }

    [Fact]
    public void A_client_carrying_no_client_row_id_is_forbidden_rather_than_widened()
    {
        // The failure worth pinning, because the plausible-looking alternative is a disclosure of a
        // different kind: null is dispatch's answer here, so widening a claimless client to it would
        // let a broken account compose a delivery attached to nobody.
        var guard = new AccessGuard(new StubCurrentUser(Target, UserRole.Client));

        var failure = Assert.Throws<ForbiddenException>(() => _ = guard.RequireDeliveryComposer());

        Assert.Equal(ErrorCode.AUTH_FORBIDDEN, failure.Code);
    }

    [Fact]
    public void A_driver_composes_nothing_and_is_refused()
    {
        // FR-25 makes a driver a reader of the parcels they carry and never an author of one. The
        // driver row id is present precisely so this is not passing for the missing-claim reason.
        var guard = new AccessGuard(
            new StubCurrentUser(Target, UserRole.Driver, driverId: new DriverId(4)));

        var failure = Assert.Throws<ForbiddenException>(() => _ = guard.RequireDeliveryComposer());

        Assert.Equal(ErrorCode.AUTH_FORBIDDEN, failure.Code);
    }

    /// <summary>
    /// A caller with no adapter behind it. The guard depends on the port, not on a
    /// <c>ClaimsPrincipal</c>, which is what makes these tests possible without a host.
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
