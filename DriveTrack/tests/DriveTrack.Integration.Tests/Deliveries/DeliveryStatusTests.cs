using System.Net;
using System.Text.Json;
using DriveTrack.Application.Common;
using DriveTrack.Domain.Deliveries;
using DriveTrack.Domain.Identity;
using DriveTrack.Integration.Tests.Fleet;
using DriveTrack.Integration.Tests.Persistence;
using DriveTrack.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace DriveTrack.Integration.Tests.Deliveries;

/// <summary>
/// FR-30 to FR-34 and FR-105 to FR-108 over HTTP, against the real <c>Program.cs</c> pipeline and a
/// real PostgreSQL.
/// <para>
/// Two halves of this story can only be asserted here. The first is that a status change and its
/// timeline entry are one write: a legal move leaves exactly one entry, and a refused one leaves the
/// stored status and the entry count as they were. Every refusal in <c>ChangeStatusAsync</c> is
/// raised before the entry is built, so what these tests establish is that the refusals really do
/// come first — not that a partial write rolls back, which nothing here can reach. The second half
/// is the disclosure matrix: who is told the actor's name is decided in the service's mapping step
/// against the caller, so the claim is about what a particular signed-in caller receives on the wire.
/// </para>
/// </summary>
public class DeliveryStatusTests(PostgresFixture postgres)
{
    // =====================================================================================
    // The lifecycle over the wire
    // =====================================================================================

    [Fact]
    public async Task A_legal_transition_moves_the_delivery_and_writes_one_entry()
    {
        // The first row of the matrix, end to end: the status changes, exactly one entry is
        // appended, and it carries the actor snapshot AD-20 asks for and the instant AD-13's clock
        // supplied.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);

        var deliveryId = await DeliveryApi.PostAsync(
            client, dispatcher, DeliveryApi.NewDelivery(), cancellationToken);

        using (var response = await DeliveryApi.ChangeStatusAsync(
                   client, dispatcher, deliveryId, DeliveryStatus.InTransit, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var entry = await FleetApi.DataAsync(response, cancellationToken);

            // AD-21: the member name on the wire, both ends of the move.
            Assert.Equal(
                nameof(DeliveryStatus.Pending),
                entry.GetProperty("previousStatus").GetString());
            Assert.Equal(
                nameof(DeliveryStatus.InTransit),
                entry.GetProperty("newStatus").GetString());

            Assert.Equal(nameof(UserRole.Dispatcher), entry.GetProperty("actorRole").GetString());
            Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty("actorName").GetString()));
            Assert.Equal(JsonValueKind.Null, entry.GetProperty("note").ValueKind);
        }

        Assert.Equal(
            nameof(DeliveryStatus.InTransit),
            await DeliveryApi.StatusAsync(client, dispatcher, deliveryId, cancellationToken));

        var entries = await DeliveryApi.EntriesAsync(client, dispatcher, deliveryId, cancellationToken);

        // Exactly one. A path that appended twice - once for the change and once for a note that
        // was never sent - would satisfy every assertion above.
        Assert.Single(entries);

        // AD-13: the instant is stored and returned at offset zero, which rules out a local-time
        // clock. It does not rule out DateTimeOffset.UtcNow - that also reads offset zero, and it is
        // barred by PersistenceContractTests, which scans src/ for the call rather than looking at
        // any value.
        var occurredAt = entries[0].GetProperty("occurredAt").GetDateTimeOffset();

        Assert.Equal(TimeSpan.Zero, occurredAt.Offset);
    }

    [Fact]
    public async Task A_change_and_its_note_land_on_the_same_entry()
    {
        // FR-106: "failed, nobody at the address" is one fact. Written as two rows it would have to
        // be reassembled by a later reader from two timestamps.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var driver = await DeliveryApi.DriverAsync(client, dispatcher, vehicleId: null, cancellationToken);

        var deliveryId = await DeliveryApi.PostAsync(
            client,
            dispatcher,
            DeliveryApi.NewDelivery(driverId: driver.DriverId),
            cancellationToken);

        using (var moved = await DeliveryApi.ChangeStatusAsync(
                   client, driver.Token, deliveryId, DeliveryStatus.InTransit, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        }

        using (var failed = await DeliveryApi.ChangeStatusAsync(
                   client,
                   driver.Token,
                   deliveryId,
                   DeliveryStatus.Failed,
                   cancellationToken,
                   note: "Нікого не було вдома"))
        {
            Assert.Equal(HttpStatusCode.OK, failed.StatusCode);

            var entry = await FleetApi.DataAsync(failed, cancellationToken);

            Assert.Equal(nameof(DeliveryStatus.InTransit), entry.GetProperty("previousStatus").GetString());
            Assert.Equal(nameof(DeliveryStatus.Failed), entry.GetProperty("newStatus").GetString());
            Assert.Equal("Нікого не було вдома", entry.GetProperty("note").GetString());
        }
    }

    [Theory]
    // FR-32: nothing jumps the lifecycle, and Delivered is terminal.
    [InlineData(DeliveryStatus.Pending, DeliveryStatus.Delivered)]
    [InlineData(DeliveryStatus.Pending, DeliveryStatus.Failed)]
    [InlineData(DeliveryStatus.InTransit, DeliveryStatus.Pending)]
    [InlineData(DeliveryStatus.Delivered, DeliveryStatus.InTransit)]
    [InlineData(DeliveryStatus.Delivered, DeliveryStatus.Failed)]
    // The same status re-sent. Not in the table, so refused rather than written as an entry saying
    // a parcel moved from where it is to where it is.
    [InlineData(DeliveryStatus.InTransit, DeliveryStatus.InTransit)]
    public async Task An_illegal_transition_is_a_conflict_that_writes_nothing(
        DeliveryStatus from,
        DeliveryStatus requested)
    {
        // "Refused" and "refused and nothing happened" are different claims, and the second is the
        // one AD-27 makes: the entry is staged before the commit, so a transition that throws after
        // an earlier one succeeded must leave neither a status nor a row behind it.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);

        var deliveryId = await DeliveryApi.PostAsync(
            client, dispatcher, DeliveryApi.NewDelivery(), cancellationToken);

        // Walked to the starting status through the endpoint that owns the write path, so the
        // arrangement is itself a use of the thing under test.
        await WalkAsync(client, dispatcher, deliveryId, from, cancellationToken);

        var before = await DeliveryApi.EntriesAsync(client, dispatcher, deliveryId, cancellationToken);

        using (var refused = await DeliveryApi.ChangeStatusAsync(
                   client, dispatcher, deliveryId, requested, cancellationToken, note: "Спробуймо"))
        {
            await FleetApi.AssertFailureAsync(
                refused,
                HttpStatusCode.Conflict,
                ErrorCode.DELIVERY_INVALID_STATUS_TRANSITION,
                cancellationToken);
        }

        Assert.Equal(
            from.ToString(),
            await DeliveryApi.StatusAsync(client, dispatcher, deliveryId, cancellationToken));

        var after = await DeliveryApi.EntriesAsync(client, dispatcher, deliveryId, cancellationToken);

        // Not "no entries" - the walk above wrote some - but "no new one", which is the claim that
        // survives whatever arrangement a later theory row needs. The note the refused request
        // carried is what a half-committed path would have left behind.
        Assert.Equal(before.Length, after.Length);
    }

    [Theory]
    // A body that names no status at all. Without a rule the property would bind to the enum's
    // first member and the caller would be told "Pending cannot move to Pending" - a 409 about a
    // transition they never asked for, and one no amount of re-reading FR-32 would explain.
    [InlineData(false)]
    // A number the enum has no member for. The shell registers JsonStringEnumConverter with its
    // default allowIntegerValues, so this deserializes rather than being refused as malformed.
    [InlineData(true)]
    public async Task A_status_the_system_does_not_have_is_a_refusal_naming_the_field(bool undefined)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);

        var deliveryId = await DeliveryApi.PostAsync(
            client, dispatcher, DeliveryApi.NewDelivery(), cancellationToken);

        object body = undefined ? new { status = 42 } : new { note = "Без стану" };

        using (var refused = await FleetApi.SendAsync(
                   client,
                   HttpMethod.Post,
                   $"/api/deliveries/{deliveryId}/status",
                   dispatcher,
                   body,
                   cancellationToken))
        {
            // 422 and not 409: NFR-2 gives one kind of failure one status, and a payload the system
            // cannot read is not a collision with the delivery's current state.
            await FleetApi.AssertFailureAsync(
                refused,
                HttpStatusCode.UnprocessableEntity,
                ErrorCode.COMMON_VALIDATION_FAILED,
                cancellationToken);

            var envelope = await FleetApi.ReadAsync(refused, cancellationToken);

            Assert.True(
                envelope.GetProperty("error").GetProperty("fields").TryGetProperty("status", out _),
                "The refusal named no field, so a form has nothing to attach it to.");
        }

        Assert.Equal(
            nameof(DeliveryStatus.Pending),
            await DeliveryApi.StatusAsync(client, dispatcher, deliveryId, cancellationToken));

        Assert.Empty(await DeliveryApi.EntriesAsync(client, dispatcher, deliveryId, cancellationToken));
    }

    [Fact]
    public async Task A_note_on_a_status_change_is_bounded_by_the_same_column()
    {
        // The status route validates its note too, and nothing else in this suite exercises that:
        // without this, dropping the validator call from ChangeStatusAsync keeps every other test
        // green while an over-long note reaches varchar(1000) as an untranslated 22001.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);

        var deliveryId = await DeliveryApi.PostAsync(
            client, dispatcher, DeliveryApi.NewDelivery(), cancellationToken);

        using (var refused = await DeliveryApi.ChangeStatusAsync(
                   client,
                   dispatcher,
                   deliveryId,
                   DeliveryStatus.InTransit,
                   cancellationToken,
                   note: new string('я', 1001)))
        {
            await FleetApi.AssertFailureAsync(
                refused,
                HttpStatusCode.UnprocessableEntity,
                ErrorCode.COMMON_VALIDATION_FAILED,
                cancellationToken);
        }

        // Refused before the move, so the parcel has not gone anywhere.
        Assert.Equal(
            nameof(DeliveryStatus.Pending),
            await DeliveryApi.StatusAsync(client, dispatcher, deliveryId, cancellationToken));

        using (var accepted = await DeliveryApi.ChangeStatusAsync(
                   client,
                   dispatcher,
                   deliveryId,
                   DeliveryStatus.InTransit,
                   cancellationToken,

                   // Padded, because the service stores the trimmed text: a note that fits once
                   // trimmed must not be refused for whitespace that is never written.
                   note: "  " + new string('я', 1000) + "  "))
        {
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

            var entry = await FleetApi.DataAsync(accepted, cancellationToken);

            Assert.Equal(1000, entry.GetProperty("note").GetString()!.Length);
        }
    }

    [Fact]
    public async Task A_token_whose_account_is_gone_is_unauthenticated_rather_than_a_server_error()
    {
        // A bearer token carries no revocation, so an administrator who deletes a dispatcher leaves
        // that dispatcher's token working until it expires. Their next status change would insert an
        // entry whose actor_user_id names no row - SQLSTATE 23503, which the constraint translator
        // deliberately passes through, so it would leave as a 500 for a request that is simply no
        // longer signed in.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var admin = await FleetApi.TokenAsync(factory, client, UserRole.Admin, cancellationToken);

        var email = FleetApi.UniqueEmail();
        int userId;

        using (var created = await FleetApi.SendAsync(
                   client,
                   HttpMethod.Post,
                   "/api/dispatchers",
                   admin,
                   new { firstName = "Ігор", lastName = "Ковальчук", email, password = FleetApi.Password },
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);

            userId = (await FleetApi.DataAsync(created, cancellationToken))
                .GetProperty("userId")
                .GetInt32();
        }

        var stale = await FleetApi.SignInAsync(client, email, FleetApi.Password, cancellationToken);

        var deliveryId = await DeliveryApi.PostAsync(
            client, stale, DeliveryApi.NewDelivery(), cancellationToken);

        using (var deleted = await FleetApi.SendAsync(
                   client,
                   HttpMethod.Delete,
                   $"/api/dispatchers/{userId}",
                   admin,
                   body: null,
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        using (var refused = await DeliveryApi.ChangeStatusAsync(
                   client, stale, deliveryId, DeliveryStatus.InTransit, cancellationToken))
        {
            // 401 rather than 403: the credentials are not insufficient, the account behind them is
            // gone, and FR-13's session-expiry flow branches on this single code.
            await FleetApi.AssertFailureAsync(
                refused,
                HttpStatusCode.Unauthorized,
                ErrorCode.AUTH_UNAUTHENTICATED,
                cancellationToken);
        }

        Assert.Equal(
            nameof(DeliveryStatus.Pending),
            await DeliveryApi.StatusAsync(client, admin, deliveryId, cancellationToken));

        Assert.Empty(await DeliveryApi.EntriesAsync(client, admin, deliveryId, cancellationToken));
    }

    [Fact]
    public async Task A_failed_delivery_goes_back_on_the_road()
    {
        // FR-31's retry, over the wire: the failure is not terminal, and the entry records the move
        // out of it as an ordinary transition rather than as a special case.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);

        var deliveryId = await DeliveryApi.PostAsync(
            client, dispatcher, DeliveryApi.NewDelivery(), cancellationToken);

        await WalkAsync(client, dispatcher, deliveryId, DeliveryStatus.Failed, cancellationToken);

        using (var retried = await DeliveryApi.ChangeStatusAsync(
                   client, dispatcher, deliveryId, DeliveryStatus.InTransit, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, retried.StatusCode);

            var entry = await FleetApi.DataAsync(retried, cancellationToken);

            Assert.Equal(nameof(DeliveryStatus.Failed), entry.GetProperty("previousStatus").GetString());
            Assert.Equal(nameof(DeliveryStatus.InTransit), entry.GetProperty("newStatus").GetString());
        }
    }

    [Fact]
    public async Task The_timeline_reads_oldest_first()
    {
        // FR-108. The order is the repository's, tie-broken by row id, and it is what makes the
        // history a history rather than a set.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);

        var deliveryId = await DeliveryApi.PostAsync(
            client, dispatcher, DeliveryApi.NewDelivery(), cancellationToken);

        await WalkAsync(client, dispatcher, deliveryId, DeliveryStatus.Delivered, cancellationToken);

        var entries = await DeliveryApi.EntriesAsync(client, dispatcher, deliveryId, cancellationToken);

        Assert.Equal(
            [nameof(DeliveryStatus.InTransit), nameof(DeliveryStatus.Delivered)],
            entries.Select(entry => entry.GetProperty("newStatus").GetString()).ToArray());
    }

    // =====================================================================================
    // Who may move a parcel
    // =====================================================================================

    [Fact]
    public async Task A_driver_may_advance_their_own_delivery_and_not_another_drivers()
    {
        // FR-26 and FR-34 as one predicate, asserted on both sides: a driver who is refused
        // everything proves nothing, and neither does one who is allowed everything.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var mine = await DeliveryApi.DriverAsync(client, dispatcher, vehicleId: null, cancellationToken);
        var theirs = await DeliveryApi.DriverAsync(client, dispatcher, vehicleId: null, cancellationToken);

        var deliveryId = await DeliveryApi.PostAsync(
            client,
            dispatcher,
            DeliveryApi.NewDelivery(driverId: mine.DriverId),
            cancellationToken);

        using (var refused = await DeliveryApi.ChangeStatusAsync(
                   client, theirs.Token, deliveryId, DeliveryStatus.InTransit, cancellationToken))
        {
            await FleetApi.AssertFailureAsync(
                refused, HttpStatusCode.Forbidden, ErrorCode.AUTH_FORBIDDEN, cancellationToken);
        }

        // Refused before any write: the parcel is still where it was, and nothing was recorded.
        Assert.Equal(
            nameof(DeliveryStatus.Pending),
            await DeliveryApi.StatusAsync(client, dispatcher, deliveryId, cancellationToken));

        Assert.Empty(await DeliveryApi.EntriesAsync(client, dispatcher, deliveryId, cancellationToken));

        using (var allowed = await DeliveryApi.ChangeStatusAsync(
                   client, mine.Token, deliveryId, DeliveryStatus.InTransit, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }
    }

    [Fact]
    public async Task A_driver_may_not_advance_an_unassigned_delivery()
    {
        // A null assignment must not read as "anyone's": a parcel nobody is carrying is dispatch's
        // to move, not the first driver's to claim.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var driver = await DeliveryApi.DriverAsync(client, dispatcher, vehicleId: null, cancellationToken);

        var deliveryId = await DeliveryApi.PostAsync(
            client, dispatcher, DeliveryApi.NewDelivery(), cancellationToken);

        using var refused = await DeliveryApi.ChangeStatusAsync(
            client, driver.Token, deliveryId, DeliveryStatus.InTransit, cancellationToken);

        await FleetApi.AssertFailureAsync(
            refused, HttpStatusCode.Forbidden, ErrorCode.AUTH_FORBIDDEN, cancellationToken);
    }

    [Fact]
    public async Task A_client_may_never_change_a_status_even_on_their_own_delivery()
    {
        // FR-90, and the reason the guard grew a member: the client's scope admits this very row,
        // so a rule written in terms of visibility would have let them move it.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var owner = await DeliveryApi.ClientAsync(client, dispatcher, cancellationToken);

        var deliveryId = await DeliveryApi.PostAsync(
            client,
            dispatcher,
            DeliveryApi.NewDelivery(clientId: owner.ClientId),
            cancellationToken);

        using var refused = await DeliveryApi.ChangeStatusAsync(
            client, owner.Token, deliveryId, DeliveryStatus.InTransit, cancellationToken);

        await FleetApi.AssertFailureAsync(
            refused, HttpStatusCode.Forbidden, ErrorCode.AUTH_FORBIDDEN, cancellationToken);
    }

    [Fact]
    public async Task An_anonymous_caller_is_refused_on_every_route()
    {
        // 401 rather than 403 on all three: no usable credentials were presented, and FR-13's
        // session-expiry flow branches on that single code.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);

        var deliveryId = await DeliveryApi.PostAsync(
            client, dispatcher, DeliveryApi.NewDelivery(), cancellationToken);

        using (var status = await DeliveryApi.ChangeStatusAsync(
                   client, token: null, deliveryId, DeliveryStatus.InTransit, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, status.StatusCode);
        }

        using (var note = await DeliveryApi.AddNoteAsync(
                   client, token: null, deliveryId, "Спроба", cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, note.StatusCode);
        }

        using var timeline = await DeliveryApi.TimelineAsync(
            client, token: null, deliveryId, cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, timeline.StatusCode);
    }

    [Fact]
    public async Task An_id_no_row_holds_answers_by_what_the_caller_could_have_been_told()
    {
        // The order the guard runs in, made visible. Dispatch is authorized for the lifecycle
        // whatever the row says, so they learn the delivery is not there; a driver is refused on the
        // null assignment first, which is what stops them probing for ids they may not act on.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var driver = await DeliveryApi.DriverAsync(client, dispatcher, vehicleId: null, cancellationToken);

        using (var missing = await DeliveryApi.ChangeStatusAsync(
                   client, dispatcher, 987_654_321, DeliveryStatus.InTransit, cancellationToken))
        {
            await FleetApi.AssertFailureAsync(
                missing, HttpStatusCode.NotFound, ErrorCode.COMMON_NOT_FOUND, cancellationToken);
        }

        using var refused = await DeliveryApi.ChangeStatusAsync(
            client, driver.Token, 987_654_321, DeliveryStatus.InTransit, cancellationToken);

        await FleetApi.AssertFailureAsync(
            refused, HttpStatusCode.Forbidden, ErrorCode.AUTH_FORBIDDEN, cancellationToken);
    }

    // =====================================================================================
    // Standalone notes
    // =====================================================================================

    [Fact]
    public async Task A_client_may_note_their_own_delivery_without_changing_its_status()
    {
        // FR-107: the entry carries neither status, and the delivery is exactly where it was. That
        // second half is what separates a note from a status change with an empty transition.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var owner = await DeliveryApi.ClientAsync(client, dispatcher, cancellationToken);

        var deliveryId = await DeliveryApi.PostAsync(
            client,
            dispatcher,
            DeliveryApi.NewDelivery(clientId: owner.ClientId),
            cancellationToken);

        using (var response = await DeliveryApi.AddNoteAsync(
                   client, owner.Token, deliveryId, "  Зателефонуйте перед приїздом  ", cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var entry = await FleetApi.DataAsync(response, cancellationToken);

            Assert.Equal(JsonValueKind.Null, entry.GetProperty("previousStatus").ValueKind);
            Assert.Equal(JsonValueKind.Null, entry.GetProperty("newStatus").ValueKind);
            Assert.Equal("Зателефонуйте перед приїздом", entry.GetProperty("note").GetString());
            Assert.Equal(nameof(UserRole.Client), entry.GetProperty("actorRole").GetString());
        }

        Assert.Equal(
            nameof(DeliveryStatus.Pending),
            await DeliveryApi.StatusAsync(client, dispatcher, deliveryId, cancellationToken));
    }

    [Fact]
    public async Task An_actor_whose_name_is_wider_than_the_snapshot_still_writes_an_entry()
    {
        // Registration bounds a given and a family name at a hundred characters each, so the two of
        // them joined by a space are a hundred and one characters wider than the column the snapshot
        // is written to. Nothing refuses that account, and an over-long insert is SQLSTATE 22001,
        // which the constraint translator deliberately passes through - so without a bound on the
        // write path a perfectly legal client could never say anything about their own delivery, and
        // would be told so with a 500.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);

        var owner = await DeliveryApi.ClientAsync(
            client,
            dispatcher,
            cancellationToken,
            firstName: new string('О', 100),
            lastName: new string('П', 100));

        var deliveryId = await DeliveryApi.PostAsync(
            client,
            dispatcher,
            DeliveryApi.NewDelivery(clientId: owner.ClientId),
            cancellationToken);

        using (var response = await DeliveryApi.AddNoteAsync(
                   client, owner.Token, deliveryId, "Будь ласка, зателефонуйте", cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // And dispatch, who may be told who acted, reads a name that fits rather than a blank one:
        // the snapshot is cut to the column, not dropped.
        using var timeline = await DeliveryApi.TimelineAsync(
            client, dispatcher, deliveryId, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, timeline.StatusCode);

        var actorName = (await FleetApi.DataAsync(timeline, cancellationToken))
            .EnumerateArray()
            .Single()
            .GetProperty("actorName")
            .GetString();

        Assert.Equal(TimelineEntry.ActorDisplayNameMaximumLength, actorName?.Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_note_is_refused_before_any_write(string? note)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);

        var deliveryId = await DeliveryApi.PostAsync(
            client, dispatcher, DeliveryApi.NewDelivery(), cancellationToken);

        using (var refused = await DeliveryApi.AddNoteAsync(
                   client, dispatcher, deliveryId, note, cancellationToken))
        {
            await FleetApi.AssertFailureAsync(
                refused,
                HttpStatusCode.UnprocessableEntity,
                ErrorCode.COMMON_VALIDATION_FAILED,
                cancellationToken);

            // NFR-4: the field is named, so a form can attach the refusal to the box that produced
            // it rather than showing a sentence beside an untouched panel.
            var envelope = await FleetApi.ReadAsync(refused, cancellationToken);

            Assert.True(
                envelope.GetProperty("error").GetProperty("fields").TryGetProperty("note", out _),
                "The refusal named no field, so a form has nothing to attach it to.");
        }

        Assert.Empty(await DeliveryApi.EntriesAsync(client, dispatcher, deliveryId, cancellationToken));
    }

    [Fact]
    public async Task A_note_the_length_of_the_column_is_accepted_and_one_character_more_is_not()
    {
        // The boundary in both directions, over the wire. A one-sided assertion passes for a
        // validator that refuses every note and for one that refuses none.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);

        var deliveryId = await DeliveryApi.PostAsync(
            client, dispatcher, DeliveryApi.NewDelivery(), cancellationToken);

        using (var accepted = await DeliveryApi.AddNoteAsync(
                   client, dispatcher, deliveryId, new string('я', 1000), cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }

        using var refused = await DeliveryApi.AddNoteAsync(
            client, dispatcher, deliveryId, new string('я', 1001), cancellationToken);

        await FleetApi.AssertFailureAsync(
            refused,
            HttpStatusCode.UnprocessableEntity,
            ErrorCode.COMMON_VALIDATION_FAILED,
            cancellationToken);

        Assert.Single(await DeliveryApi.EntriesAsync(client, dispatcher, deliveryId, cancellationToken));
    }

    [Fact]
    public async Task A_delivery_a_caller_may_not_see_is_not_there_as_far_as_they_are_concerned()
    {
        // FR-27 through the timeline routes: a client asking about another client's delivery gets
        // 404, not 403, so the answer discloses nothing about a row they may not see. Both the write
        // and the read say the same thing, which is the point - one of them answering 403 would be
        // the leak.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var owner = await DeliveryApi.ClientAsync(client, dispatcher, cancellationToken);
        var stranger = await DeliveryApi.ClientAsync(client, dispatcher, cancellationToken);
        var driver = await DeliveryApi.DriverAsync(client, dispatcher, vehicleId: null, cancellationToken);

        var deliveryId = await DeliveryApi.PostAsync(
            client,
            dispatcher,
            DeliveryApi.NewDelivery(clientId: owner.ClientId),
            cancellationToken);

        using (var note = await DeliveryApi.AddNoteAsync(
                   client, stranger.Token, deliveryId, "Чуже", cancellationToken))
        {
            await FleetApi.AssertFailureAsync(
                note, HttpStatusCode.NotFound, ErrorCode.COMMON_NOT_FOUND, cancellationToken);
        }

        using (var read = await DeliveryApi.TimelineAsync(
                   client, stranger.Token, deliveryId, cancellationToken))
        {
            await FleetApi.AssertFailureAsync(
                read, HttpStatusCode.NotFound, ErrorCode.COMMON_NOT_FOUND, cancellationToken);
        }

        // And the same for a driver the delivery is not assigned to.
        using var driverRead = await DeliveryApi.TimelineAsync(
            client, driver.Token, deliveryId, cancellationToken);

        await FleetApi.AssertFailureAsync(
            driverRead, HttpStatusCode.NotFound, ErrorCode.COMMON_NOT_FOUND, cancellationToken);
    }

    // =====================================================================================
    // Who is told who acted
    // =====================================================================================

    [Fact]
    public async Task Dispatch_is_told_the_actor_and_the_two_narrowed_roles_are_not()
    {
        // AD-17 and FR-105 together: the same entries reach every viewer, and only the name is
        // withheld. A null actorName here means "withheld" and nothing else - the column is not
        // null - which is why this can be one DTO rather than two.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var driver = await DeliveryApi.DriverAsync(client, dispatcher, vehicleId: null, cancellationToken);
        var owner = await DeliveryApi.ClientAsync(client, dispatcher, cancellationToken);

        var deliveryId = await DeliveryApi.PostAsync(
            client,
            dispatcher,
            DeliveryApi.NewDelivery(driverId: driver.DriverId, clientId: owner.ClientId),
            cancellationToken);

        using (var moved = await DeliveryApi.ChangeStatusAsync(
                   client, dispatcher, deliveryId, DeliveryStatus.InTransit, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        }

        var seenByDispatch = await DeliveryApi.EntriesAsync(
            client, dispatcher, deliveryId, cancellationToken);

        Assert.Equal(
            "Диспетчер Тестовий",
            Assert.Single(seenByDispatch).GetProperty("actorName").GetString());

        foreach (var token in new[] { driver.Token, owner.Token })
        {
            var narrowed = await DeliveryApi.EntriesAsync(client, token, deliveryId, cancellationToken);

            var entry = Assert.Single(narrowed);

            // The same entry, minus the one field FR-27 withholds. The role survives, so the driver
            // and the client can still see that dispatch moved the parcel.
            Assert.Equal(JsonValueKind.Null, entry.GetProperty("actorName").ValueKind);
            Assert.Equal(nameof(UserRole.Dispatcher), entry.GetProperty("actorRole").GetString());
            Assert.Equal(nameof(DeliveryStatus.InTransit), entry.GetProperty("newStatus").GetString());
        }
    }

    [Fact]
    public async Task A_deleted_actor_leaves_the_entry_standing()
    {
        // AD-20's whole reason: a restrict would make every user who has ever acted undeletable -
        // the "deleting a driver raises a database error" defect this rewrite exists to fix - and a
        // cascade would erase the history instead. Set-null keeps both.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var driver = await DeliveryApi.DriverAsync(client, dispatcher, vehicleId: null, cancellationToken);

        var deliveryId = await DeliveryApi.PostAsync(
            client,
            dispatcher,
            DeliveryApi.NewDelivery(driverId: driver.DriverId),
            cancellationToken);

        using (var moved = await DeliveryApi.ChangeStatusAsync(
                   client, driver.Token, deliveryId, DeliveryStatus.InTransit, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        }

        // Through the fleet capability that ships driver removal (FR-39), not by hand: what is
        // being asserted is that the shipped path leaves the history intact.
        using (var deleted = await FleetApi.SendAsync(
                   client,
                   HttpMethod.Delete,
                   $"/api/drivers/{driver.DriverId}",
                   dispatcher,
                   body: null,
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        }

        var entry = Assert.Single(
            await DeliveryApi.EntriesAsync(client, dispatcher, deliveryId, cancellationToken));

        // The snapshot stands: the name and the role are what they were when the entry was written.
        Assert.Equal("Тарас Шевченко", entry.GetProperty("actorName").GetString());
        Assert.Equal(nameof(UserRole.Driver), entry.GetProperty("actorRole").GetString());

        // And the reference itself is gone, which is what set-null means (AD-20).
        await using var context = await factory.Database.ContextFactory
            .CreateDbContextAsync(cancellationToken);

        var stored = await context.TimelineEntries
            .AsNoTracking()
            .SingleAsync(row => row.DeliveryId == deliveryId, cancellationToken);

        Assert.Null(stored.ActorUserId);
        Assert.Equal(UserRole.Driver, stored.ActorRole);
    }

    // =====================================================================================
    // The transaction
    // =====================================================================================

    [Fact]
    public async Task Nothing_outside_the_status_route_writes_a_timeline_entry()
    {
        // AD-27's closed set of appending operations, asserted by what does not happen: creating and
        // editing a delivery are not lifecycle events, and a create that quietly appended an
        // "opened" entry would make every count in this suite one higher for no requirement.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);

        var deliveryId = await DeliveryApi.PostAsync(
            client, dispatcher, DeliveryApi.NewDelivery(), cancellationToken);

        using (var edited = await FleetApi.SendAsync(
                   client,
                   HttpMethod.Put,
                   $"/api/deliveries/{deliveryId}",
                   dispatcher,
                   new { packageDetails = "Дві палети" },
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        }

        Assert.Empty(await DeliveryApi.EntriesAsync(client, dispatcher, deliveryId, cancellationToken));
    }

    /// <summary>
    /// Advances a delivery to <paramref name="target"/> through the shipped endpoint, which is the
    /// only write path there is (AD-10). Every status is two moves or fewer from <c>Pending</c>.
    /// </summary>
    private static async Task WalkAsync(
        HttpClient client,
        string dispatcherToken,
        int deliveryId,
        DeliveryStatus target,
        CancellationToken cancellationToken)
    {
        if (target == DeliveryStatus.Pending)
        {
            return;
        }

        await MoveAsync(client, dispatcherToken, deliveryId, DeliveryStatus.InTransit, cancellationToken);

        if (target != DeliveryStatus.InTransit)
        {
            await MoveAsync(client, dispatcherToken, deliveryId, target, cancellationToken);
        }
    }

    private static async Task MoveAsync(
        HttpClient client,
        string dispatcherToken,
        int deliveryId,
        DeliveryStatus status,
        CancellationToken cancellationToken)
    {
        using var response = await DeliveryApi.ChangeStatusAsync(
            client, dispatcherToken, deliveryId, status, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
