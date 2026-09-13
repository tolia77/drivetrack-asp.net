using System.Globalization;
using System.Net;
using System.Text.Json;
using DriveTrack.Application.Common;
using DriveTrack.Domain.Deliveries;
using DriveTrack.Integration.Tests.Deliveries;
using DriveTrack.Integration.Tests.Fleet;
using DriveTrack.Integration.Tests.Identity;
using DriveTrack.Integration.Tests.Persistence;
using DriveTrack.Integration.Tests.Reviews;

namespace DriveTrack.Integration.Tests.Authorization;

/// <summary>
/// Right role, wrong owner — the half of the boundary the role matrix cannot reach by construction.
/// <para>
/// <c>RequireScope</c>, <c>RequireShiftOwner</c>, <c>RequireReviewOwner</c> and
/// <c>RequireAssignedDriver</c> all admit the caller's <em>role</em> and then refuse the caller's
/// <em>row</em>. A table keyed on roles says a driver may change a delivery's status, which is true;
/// what it cannot say is that this driver may not change <em>that</em> delivery's.
/// </para>
/// <para>
/// <b>The refusal is not the same code everywhere, and that is the design.</b> The members that
/// compare an owner id refuse with <c>AUTH_FORBIDDEN</c>. The members that answer a <em>scope</em>
/// narrow in the query instead, so a row outside it is answered <c>COMMON_NOT_FOUND</c> — a client
/// asking about somebody else's parcel gets the same answer as one asking about a parcel that never
/// existed, which is AD-3's whole point and a weaker disclosure than a 403 would be. Each case below
/// says which it expects and why.
/// </para>
/// <para>
/// Every attempt here is refused, so nothing in this file writes — which is what lets it run beside
/// <c>EndpointBoundaryTests</c>' row-count snapshot. The "row unchanged" assertions read the state
/// back through dispatch's own routes, before and after.
/// </para>
/// </summary>
public class OwnershipBoundaryTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Another_driver_cannot_move_the_parcel_they_are_not_carrying()
    {
        // FR-34 through RequireAssignedDriver: a null assignment never matches, and neither does
        // another driver's. The status is read back because a 403 answered after the write would
        // look identical on the wire.
        var cancellationToken = TestContext.Current.CancellationToken;
        var world = await AuthorizationWorld.InstanceAsync(postgres.ConnectionString, cancellationToken);

        var before = await StatusAsync(world, cancellationToken);

        using (var response = await DeliveryApi.ChangeStatusAsync(
                   world.Client,
                   world.DriverB.Token,
                   world.DeliveryId,
                   DeliveryStatus.Failed,
                   cancellationToken,
                   "не моє"))
        {
            await FleetApi.AssertFailureAsync(
                response, HttpStatusCode.Forbidden, ErrorCode.AUTH_FORBIDDEN, cancellationToken);
        }

        Assert.Equal(before, await StatusAsync(world, cancellationToken));
    }

    [Fact]
    public async Task Another_driver_cannot_capture_the_proof_of_a_parcel_they_are_not_carrying()
    {
        // The same predicate on the evidence (FR-119). A second capture would be refused as a
        // duplicate too, so the assertion is the code rather than the outcome: AUTH_FORBIDDEN says
        // the guard stopped it, PROOF_ALREADY_CAPTURED would say the uniqueness rule did.
        var cancellationToken = TestContext.Current.CancellationToken;
        var world = await AuthorizationWorld.InstanceAsync(postgres.ConnectionString, cancellationToken);

        var before = await ProofAsync(world, cancellationToken);

        using (var response = await ProofApi.CaptureAsync(
                   world.Client,
                   world.DriverB.Token,
                   world.DeliveryId,
                   cancellationToken,
                   signature: ProofApi.Png))
        {
            await FleetApi.AssertFailureAsync(
                response, HttpStatusCode.Forbidden, ErrorCode.AUTH_FORBIDDEN, cancellationToken);
        }

        // The read-back every other case in this file does, and the one this one was missing: a
        // capture that stored its bytes and only then refused would leave the status assertion
        // above green, and the row-count snapshot next door replays the matrix rather than this.
        Assert.Equal(before, await ProofAsync(world, cancellationToken));
    }

    [Fact]
    public async Task Another_driver_is_told_the_parcel_does_not_exist_rather_than_refused()
    {
        // The three routes behind RequireScope. A driver's scope is their own rows, so the delivery
        // is narrowed out of the query and the answer is the one a caller gets for a row that never
        // existed. Asserting 404 here rather than 403 is the point: the weaker answer is the more
        // private one, and a change to 403 would start disclosing that the row exists.
        var cancellationToken = TestContext.Current.CancellationToken;
        var world = await AuthorizationWorld.InstanceAsync(postgres.ConnectionString, cancellationToken);

        var before = await TimelineCountAsync(world, cancellationToken);

        using (var timeline = await DeliveryApi.TimelineAsync(
                   world.Client, world.DriverB.Token, world.DeliveryId, cancellationToken))
        {
            await FleetApi.AssertFailureAsync(
                timeline, HttpStatusCode.NotFound, ErrorCode.COMMON_NOT_FOUND, cancellationToken);
        }

        using (var note = await DeliveryApi.AddNoteAsync(
                   world.Client, world.DriverB.Token, world.DeliveryId, "не моє", cancellationToken))
        {
            await FleetApi.AssertFailureAsync(
                note, HttpStatusCode.NotFound, ErrorCode.COMMON_NOT_FOUND, cancellationToken);
        }

        using (var proof = await ProofApi.ReadAsync(
                   world.Client, world.DriverB.Token, world.DeliveryId, cancellationToken))
        {
            await FleetApi.AssertFailureAsync(
                proof, HttpStatusCode.NotFound, ErrorCode.COMMON_NOT_FOUND, cancellationToken);
        }

        Assert.Equal(before, await TimelineCountAsync(world, cancellationToken));
    }

    [Fact]
    public async Task Another_client_reaches_neither_the_delivery_nor_its_proof()
    {
        // FR-25 and FR-122 from the other party's side. The own-deliveries list is narrowed rather
        // than refused - a client with no deliveries has an empty list, not a 403 - so the claim is
        // about what is in it.
        var cancellationToken = TestContext.Current.CancellationToken;
        var world = await AuthorizationWorld.InstanceAsync(postgres.ConnectionString, cancellationToken);

        using (var mine = await FleetApi.SendAsync(
                   world.Client,
                   HttpMethod.Get,
                   "/api/deliveries/mine",
                   world.ClientB.Token,
                   body: null,
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, mine.StatusCode);

            var rows = (await FleetApi.DataAsync(mine, cancellationToken)).EnumerateArray().ToArray();

            Assert.DoesNotContain(rows, row => row.GetProperty("id").GetInt32() == world.DeliveryId);
        }

        using var proof = await ProofApi.ReadAsync(
            world.Client, world.ClientB.Token, world.DeliveryId, cancellationToken);

        await FleetApi.AssertFailureAsync(
            proof, HttpStatusCode.NotFound, ErrorCode.COMMON_NOT_FOUND, cancellationToken);
    }

    [Fact]
    public async Task The_owning_client_does_reach_their_own_delivery_and_its_proof()
    {
        // The other direction, and the reason the refusals above are not vacuous: a route that
        // answered 404 to everybody would satisfy every assertion in this file.
        //
        // Both halves of the refusal above, not just the proof. The own-deliveries list is the one
        // that is narrowed rather than refused, so "the other client's parcel is not in it" is
        // satisfied by a list that is empty for everybody - and only this read says it is not.
        var cancellationToken = TestContext.Current.CancellationToken;
        var world = await AuthorizationWorld.InstanceAsync(postgres.ConnectionString, cancellationToken);

        using (var mine = await FleetApi.SendAsync(
                   world.Client,
                   HttpMethod.Get,
                   "/api/deliveries/mine",
                   world.ClientA.Token,
                   body: null,
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, mine.StatusCode);

            var rows = (await FleetApi.DataAsync(mine, cancellationToken)).EnumerateArray().ToArray();

            Assert.Contains(rows, row => row.GetProperty("id").GetInt32() == world.DeliveryId);
        }

        using var proof = await ProofApi.ReadAsync(
            world.Client, world.ClientA.Token, world.DeliveryId, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, proof.StatusCode);
    }

    [Fact]
    public async Task A_stranger_cannot_fetch_the_signature_bytes_of_a_parcel_that_is_not_theirs()
    {
        // GET /proof-assets/{id} is the one route by which stored bytes leave this system, and the
        // endpoint matrix can only reach it at an id no row holds - where a 404 for everybody says
        // nothing at all about a real asset. This is the same route at the captured signature.
        //
        // RequireScope narrows the asset query rather than comparing an owner, so a stranger is
        // answered exactly as they would be for an asset that was never captured (DR-14, AD-3).
        var cancellationToken = TestContext.Current.CancellationToken;
        var world = await AuthorizationWorld.InstanceAsync(postgres.ConnectionString, cancellationToken);

        // The owner first, so the refusals below are a decision rather than a broken route or an
        // emptied store.
        using (var owner = await ProofApi.AssetAsync(
                   world.Client, world.ProofAssetId, cancellationToken, token: world.ClientA.Token))
        {
            Assert.Equal(HttpStatusCode.OK, owner.StatusCode);
            Assert.Equal(ProofApi.Png, await owner.Content.ReadAsByteArrayAsync(cancellationToken));
        }

        foreach (var stranger in new[] { world.ClientB.Token, world.DriverB.Token })
        {
            using var response = await ProofApi.AssetAsync(
                world.Client, world.ProofAssetId, cancellationToken, token: stranger);

            await FleetApi.AssertFailureAsync(
                response, HttpStatusCode.NotFound, ErrorCode.COMMON_NOT_FOUND, cancellationToken);
        }
    }

    [Fact]
    public async Task Another_user_cannot_read_or_change_an_account_that_is_not_theirs()
    {
        // RequireSelf, which the endpoint matrix reaches only at a user id nobody holds - where
        // every role but the administrator is refused and there is no account to disclose anyway.
        // This is the case that matters: a real account, and a signed-in caller who is not it.
        var cancellationToken = TestContext.Current.CancellationToken;
        var world = await AuthorizationWorld.InstanceAsync(postgres.ConnectionString, cancellationToken);

        var account = "/api/users/" + world.ClientAUserId.ToString(CultureInfo.InvariantCulture);

        var attempts = new (HttpMethod Method, string Path, object? Body)[]
        {
            (HttpMethod.Get, account, null),
            (HttpMethod.Post, account + "/password", new
            {
                currentPassword = FleetApi.Password,
                newPassword = "Intruder-Passw0rd",
                newPasswordConfirmation = "Intruder-Passw0rd",
            }),
            (HttpMethod.Post, account + "/email", new { email = FleetApi.UniqueEmail() }),
        };

        foreach (var (method, path, body) in attempts)
        {
            using var response = await FleetApi.SendAsync(
                world.Client, method, path, world.ClientB.Token, body, cancellationToken);

            await FleetApi.AssertFailureAsync(
                response, HttpStatusCode.Forbidden, ErrorCode.AUTH_FORBIDDEN, cancellationToken);
        }

        // And client A's credentials still work, which is the half a status code cannot give: a
        // password change or an address change that had gone through would have ended exactly this.
        await FleetApi.SignInAsync(
            world.Client, world.ClientA.Email, FleetApi.Password, cancellationToken);
    }

    [Fact]
    public async Task Another_driver_cannot_read_or_change_a_shift_that_is_not_theirs()
    {
        // RequireShiftOwner compares DriverId? against DriverId? and refuses rather than narrows,
        // so all four of these are 403 - including the read, which is the difference between this
        // member and RequireShiftScope.
        var cancellationToken = TestContext.Current.CancellationToken;
        var world = await AuthorizationWorld.InstanceAsync(postgres.ConnectionString, cancellationToken);

        var before = await ShiftAsync(world, cancellationToken);

        using (var read = await ShiftApi.GetAsync(
                   world.Client, world.DriverB.Token, world.ShiftId, cancellationToken))
        {
            await FleetApi.AssertFailureAsync(
                read, HttpStatusCode.Forbidden, ErrorCode.AUTH_FORBIDDEN, cancellationToken);
        }

        using (var edit = await ShiftApi.EditAsync(
                   world.Client,
                   world.DriverB.Token,
                   world.ShiftId,
                   cancellationToken,
                   startedAt: new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero)))
        {
            await FleetApi.AssertFailureAsync(
                edit, HttpStatusCode.Forbidden, ErrorCode.AUTH_FORBIDDEN, cancellationToken);
        }

        using (var ended = await ShiftApi.EndAsync(
                   world.Client, world.DriverB.Token, world.DriverA.DriverId, cancellationToken))
        {
            await FleetApi.AssertFailureAsync(
                ended, HttpStatusCode.Forbidden, ErrorCode.AUTH_FORBIDDEN, cancellationToken);
        }

        using (var deleted = await ShiftApi.DeleteAsync(
                   world.Client, world.DriverB.Token, world.ShiftId, cancellationToken))
        {
            await FleetApi.AssertFailureAsync(
                deleted, HttpStatusCode.Forbidden, ErrorCode.AUTH_FORBIDDEN, cancellationToken);
        }

        // Still there, still open, still starting when it started.
        Assert.Equal(before, await ShiftAsync(world, cancellationToken));
    }

    [Fact]
    public async Task Another_client_cannot_edit_or_delete_a_review_they_did_not_write()
    {
        // RequireReviewOwner: the author, or an administrator moderating. A second client is
        // neither, and the comparison is ClientId? against ClientId? - so this is 403 rather than
        // the 404 the scoped reads answer.
        var cancellationToken = TestContext.Current.CancellationToken;
        var world = await AuthorizationWorld.InstanceAsync(postgres.ConnectionString, cancellationToken);

        var before = await ReviewAsync(world, cancellationToken);

        using (var edited = await ReviewApi.EditAsync(
                   world.Client, world.ClientB.Token, world.ReviewId, 1, "підроблено", cancellationToken))
        {
            await FleetApi.AssertFailureAsync(
                edited, HttpStatusCode.Forbidden, ErrorCode.AUTH_FORBIDDEN, cancellationToken);
        }

        using (var deleted = await ReviewApi.DeleteAsync(
                   world.Client, world.ClientB.Token, world.ReviewId, cancellationToken))
        {
            await FleetApi.AssertFailureAsync(
                deleted, HttpStatusCode.Forbidden, ErrorCode.AUTH_FORBIDDEN, cancellationToken);
        }

        Assert.Equal(before, await ReviewAsync(world, cancellationToken));
    }

    private static Task<string?> StatusAsync(AuthorizationWorld world, CancellationToken cancellationToken) =>
        DeliveryApi.StatusAsync(world.Client, world.DispatcherToken, world.DeliveryId, cancellationToken);

    private static Task<long> TimelineCountAsync(
        AuthorizationWorld world,
        CancellationToken cancellationToken) =>
        AdministrationApi.CountAsync(
            world.Factory,
            "timeline_entries",
            "delivery_id = " + world.DeliveryId.ToString(CultureInfo.InvariantCulture),
            cancellationToken);

    /// <summary>The shift as dispatch reads it, rendered as one string so a change of any field fails.</summary>
    private static async Task<string> ShiftAsync(
        AuthorizationWorld world,
        CancellationToken cancellationToken)
    {
        using var response = await ShiftApi.GetAsync(
            world.Client, world.DispatcherToken, world.ShiftId, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (await FleetApi.DataAsync(response, cancellationToken)).GetRawText();
    }

    /// <summary>
    /// The proof as dispatch reads it, rendered as one string so a second signature, a changed
    /// recipient or a new asset id all fail the comparison.
    /// </summary>
    private static async Task<string> ProofAsync(
        AuthorizationWorld world,
        CancellationToken cancellationToken)
    {
        using var response = await ProofApi.ReadAsync(
            world.Client, world.DispatcherToken, world.DeliveryId, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (await FleetApi.DataAsync(response, cancellationToken)).GetRawText();
    }

    /// <summary>The review as moderation reads it, for the same reason and in the same shape.</summary>
    private static async Task<string> ReviewAsync(
        AuthorizationWorld world,
        CancellationToken cancellationToken)
    {
        using var response = await ReviewApi.ListAsync(
            world.Client, world.AdminToken, cancellationToken);

        var rows = await ReviewApi.RowsAsync(response, cancellationToken);

        return rows
            .Single(row => row.GetProperty("id").GetInt32() == world.ReviewId)
            .GetRawText();
    }
}
