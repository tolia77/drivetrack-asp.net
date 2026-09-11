using System.Net;
using System.Text.Json;
using DriveTrack.Application.Abstractions;
using DriveTrack.Application.Common;
using DriveTrack.Domain.Deliveries;
using DriveTrack.Domain.Identity;
using DriveTrack.Integration.Tests.Fleet;
using DriveTrack.Integration.Tests.Persistence;
using DriveTrack.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace DriveTrack.Integration.Tests.Deliveries;

/// <summary>
/// FR-89, FR-90 and FR-91 over HTTP, against the real <c>Program.cs</c> pipeline.
/// <para>
/// The story's claim is not "a client can post a body" — it is that the row this creates is
/// attached to the caller, carries no driver and no chosen status, and lands in dispatch's list as
/// an ordinary delivery they assign through the edit path they already have. Half of that is only
/// observable against the database and the other half only across two roles' endpoints, which is
/// why this suite is here rather than beside the validator.
/// </para>
/// </summary>
public class DeliveryRequestTests(PostgresFixture postgres)
{
    // =====================================================================================
    // What a request creates
    // =====================================================================================

    [Fact]
    public async Task A_client_request_is_stored_pending_unassigned_and_attached_to_the_caller()
    {
        // The three halves of FR-90 in one row: the client is whoever asked, the driver is nobody,
        // and the status is Pending because the entity has no other way to be born.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var requester = await DeliveryApi.ClientAsync(client, dispatcher, cancellationToken);

        int deliveryId;

        using (var created = await DeliveryApi.RequestAsync(
                   client,
                   requester.Token,
                   DeliveryApi.NewRequest(notes: "Подзвонити перед приїздом"),
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);

            var data = await FleetApi.DataAsync(created, cancellationToken);

            Assert.Equal(nameof(DeliveryStatus.Pending), data.GetProperty("status").GetString());
            Assert.Equal(12.5m, data.GetProperty("packageWeightKg").GetDecimal());
            Assert.Equal("Подзвонити перед приїздом", data.GetProperty("deliveryNotes").GetString());

            // DR-11 / FR-94: coordinates are the record and the address is a cache the side effect
            // fills in later, so the response carries the point and a null address.
            Assert.Equal(
                50.4501,
                data.GetProperty("pickup").GetProperty("point").GetProperty("latitude").GetDouble());

            deliveryId = data.GetProperty("id").GetInt32();
        }

        await using var context = await factory.Database.ContextFactory
            .CreateDbContextAsync(cancellationToken);

        var stored = await context.Deliveries.SingleAsync(row => row.Id == deliveryId, cancellationToken);

        Assert.Equal(new ClientId(requester.ClientId), stored.ClientId);
        Assert.Null(stored.DriverId);
        Assert.Equal(DeliveryStatus.Pending, stored.Status);

        // FR-89 leaves the window to dispatch, and the command has no field that could have set one.
        Assert.Null(stored.WindowEarliestAt);
        Assert.Null(stored.WindowLatestAt);
    }

    [Fact]
    public async Task The_payload_a_client_gets_back_has_nowhere_to_put_a_driver_or_a_client()
    {
        // AD-17 asserted at the wire rather than at the type: a client's own row must not be able to
        // name a counterparty, and the way that is made true is that AssignedDeliverySummary has no
        // field for one. A serializer cannot emit what the type does not carry.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var requester = await DeliveryApi.ClientAsync(client, dispatcher, cancellationToken);

        using var created = await DeliveryApi.RequestAsync(
            client, requester.Token, DeliveryApi.NewRequest(), cancellationToken);

        Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        var data = await FleetApi.DataAsync(created, cancellationToken);

        Assert.False(data.TryGetProperty("driver", out _));
        Assert.False(data.TryGetProperty("client", out _));
        Assert.False(data.TryGetProperty("driverId", out _));
        Assert.False(data.TryGetProperty("clientId", out _));
    }

    [Fact]
    public async Task A_request_the_client_did_not_attach_is_still_attached_to_them()
    {
        // The reason the client id comes from the guard and not from the payload. The body below
        // names another client and a driver; the command has no field for either, so the extra
        // properties are ignored by the binder and the row lands on the caller regardless.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var requester = await DeliveryApi.ClientAsync(client, dispatcher, cancellationToken);
        var somebodyElse = await DeliveryApi.ClientAsync(client, dispatcher, cancellationToken);
        var driver = await DeliveryApi.DriverAsync(client, dispatcher, vehicleId: null, cancellationToken);

        int deliveryId;

        using (var created = await DeliveryApi.RequestAsync(
                   client,
                   requester.Token,
                   new
                   {
                       pickup = new { latitude = 50.4501, longitude = 30.5234 },
                       dropoff = new { latitude = 49.8397, longitude = 24.0297 },
                       packageDetails = "Одна палета",
                       packageWeightKg = 12.5m,
                       deliveryNotes = (string?)null,
                       clientId = somebodyElse.ClientId,
                       driverId = driver.DriverId,
                       status = nameof(DeliveryStatus.Delivered),
                   },
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);

            deliveryId = (await FleetApi.DataAsync(created, cancellationToken))
                .GetProperty("id")
                .GetInt32();
        }

        await using var context = await factory.Database.ContextFactory
            .CreateDbContextAsync(cancellationToken);

        var stored = await context.Deliveries.SingleAsync(row => row.Id == deliveryId, cancellationToken);

        Assert.Equal(new ClientId(requester.ClientId), stored.ClientId);
        Assert.Null(stored.DriverId);
        Assert.Equal(DeliveryStatus.Pending, stored.Status);
    }

    [Fact]
    public async Task An_absent_note_is_stored_as_nothing_rather_than_as_whitespace()
    {
        // FR-17 and the service's own Blank rule: a notes column holding a single space renders as
        // absent and filters as present, which is worse than either.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var requester = await DeliveryApi.ClientAsync(client, dispatcher, cancellationToken);

        int deliveryId;

        using (var created = await DeliveryApi.RequestAsync(
                   client, requester.Token, DeliveryApi.NewRequest(notes: "   "), cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);

            var data = await FleetApi.DataAsync(created, cancellationToken);

            Assert.Equal(JsonValueKind.Null, data.GetProperty("deliveryNotes").ValueKind);

            deliveryId = data.GetProperty("id").GetInt32();
        }

        await using var context = await factory.Database.ContextFactory
            .CreateDbContextAsync(cancellationToken);

        Assert.Null(
            (await context.Deliveries.SingleAsync(row => row.Id == deliveryId, cancellationToken))
                .DeliveryNotes);
    }

    [Fact]
    public async Task A_request_answers_without_an_address_and_carries_one_afterwards()
    {
        // FR-94 / FR-95 on this path rather than on the create path. Deleting the un-awaited
        // ResolveAddresses line from RequestAsync leaves every other test in this file green: the
        // row it writes is correct either way, and a client's addresses would simply never arrive.
        //
        // Both halves matter. The response the client received names no address at all - an
        // implementation that awaited the geocoder would satisfy the second half and break FR-95 -
        // and the addresses are there once the queue has drained.
        var cancellationToken = TestContext.Current.CancellationToken;

        var geocoder = new FakeGeocoder { Address = "Київ, Хрещатик, 1" };

        await using var factory = await FleetApi.CreateAsync(
            postgres.ConnectionString,
            cancellationToken,
            OutboundPorts.Replace(geocoder, new FakeEmailSender()));

        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var requester = await DeliveryApi.ClientAsync(client, dispatcher, cancellationToken);

        int deliveryId;

        using (var created = await DeliveryApi.RequestAsync(
                   client, requester.Token, DeliveryApi.NewRequest(), cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);

            var data = await FleetApi.DataAsync(created, cancellationToken);

            deliveryId = data.GetProperty("id").GetInt32();

            Assert.Equal(
                JsonValueKind.Null,
                data.GetProperty("pickup").GetProperty("address").ValueKind);
            Assert.Equal(
                JsonValueKind.Null,
                data.GetProperty("dropoff").GetProperty("address").ValueKind);
        }

        await OutboundPorts.DrainAsync(factory, cancellationToken);

        await using var context = await factory.Database.ContextFactory
            .CreateDbContextAsync(cancellationToken);

        var stored = await context.Deliveries.SingleAsync(row => row.Id == deliveryId, cancellationToken);

        Assert.Equal("Київ, Хрещатик, 1", stored.PickupLocation.Address);
        Assert.Equal("Київ, Хрещатик, 1", stored.DropoffLocation.Address);

        // Both points, and only both: a request has two unresolved locations and nothing else.
        Assert.Equal(
            new[] { (50.4501, 30.5234), (49.8397, 24.0297) },
            geocoder.Described);
    }

    [Fact]
    public async Task A_still_valid_token_for_a_deleted_client_row_is_refused_rather_than_failing_at_the_commit()
    {
        // A bearer token carries no revocation, so an administrator who deletes a client leaves that
        // client's token working until it expires. Inserting on the id their claim still carries is
        // SQLSTATE 23503, which PostgresConstraintTranslator deliberately passes through - so the
        // caller would get a 500 for a request that is merely stale. AUTH_UNAUTHENTICATED rather
        // than a 404 because what is gone is the identity behind the credentials.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var admin = await FleetApi.TokenAsync(factory, client, UserRole.Admin, cancellationToken);
        var requester = await DeliveryApi.ClientAsync(client, admin, cancellationToken);

        int userId;

        using (var roster = await FleetApi.SendAsync(
                   client, HttpMethod.Get, "/api/clients", admin, body: null, cancellationToken))
        {
            // The deletion route is keyed on the account, not on the client row, so the id comes
            // back off the roster - the same read the arrangement uses to learn the client id.
            userId = (await FleetApi.DataAsync(roster, cancellationToken))
                .EnumerateArray()
                .Single(candidate => candidate.GetProperty("email").GetString() == requester.Email)
                .GetProperty("userId")
                .GetInt32();
        }

        using (var deleted = await FleetApi.SendAsync(
                   client,
                   HttpMethod.Delete,
                   $"/api/clients/{userId}",
                   admin,
                   body: null,
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        using (var refused = await DeliveryApi.RequestAsync(
                   client, requester.Token, DeliveryApi.NewRequest(), cancellationToken))
        {
            await FleetApi.AssertFailureAsync(
                refused,
                HttpStatusCode.Unauthorized,
                ErrorCode.AUTH_UNAUTHENTICATED,
                cancellationToken);
        }

        await AssertNoDeliveriesAsync(factory, cancellationToken);
    }

    [Fact]
    public async Task A_weight_of_zero_is_refused_by_name_and_nothing_is_written()
    {
        // NFR-4 across the new route: the same refusal a dispatcher gets, naming the same field, so
        // the client's form has something to attach it to.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var requester = await DeliveryApi.ClientAsync(client, dispatcher, cancellationToken);

        using (var refused = await DeliveryApi.RequestAsync(
                   client, requester.Token, DeliveryApi.NewRequest(weight: 0m), cancellationToken))
        {
            await FleetApi.AssertFailureAsync(
                refused,
                HttpStatusCode.UnprocessableEntity,
                ErrorCode.COMMON_VALIDATION_FAILED,
                cancellationToken);

            var fields = (await FleetApi.ReadAsync(refused, cancellationToken))
                .GetProperty("error")
                .GetProperty("fields")
                .EnumerateObject()
                .Select(property => property.Name)
                .ToArray();

            Assert.Contains(
                fields,
                name => name.StartsWith("packageWeightKg", StringComparison.Ordinal));
        }

        await AssertNoDeliveriesAsync(factory, cancellationToken);
    }

    // =====================================================================================
    // Who may ask
    // =====================================================================================

    [Fact]
    public async Task An_anonymous_caller_is_refused_and_nothing_is_written()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        using (var refused = await DeliveryApi.RequestAsync(
                   client, token: null, DeliveryApi.NewRequest(), cancellationToken))
        {
            await FleetApi.AssertFailureAsync(
                refused,
                HttpStatusCode.Unauthorized,
                ErrorCode.AUTH_UNAUTHENTICATED,
                cancellationToken);
        }

        await AssertNoDeliveriesAsync(factory, cancellationToken);
    }

    [Fact]
    public async Task A_driver_is_refused_and_nothing_is_written()
    {
        // FR-25 makes a driver a reader of the parcels they carry. A request endpoint they could
        // reach would be a driver opening work for themselves, which is dispatch's job.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var driver = await DeliveryApi.DriverAsync(client, dispatcher, vehicleId: null, cancellationToken);

        using (var refused = await DeliveryApi.RequestAsync(
                   client, driver.Token, DeliveryApi.NewRequest(), cancellationToken))
        {
            await FleetApi.AssertFailureAsync(
                refused,
                HttpStatusCode.Forbidden,
                ErrorCode.AUTH_FORBIDDEN,
                cancellationToken);
        }

        await AssertNoDeliveriesAsync(factory, cancellationToken);
    }

    [Fact]
    public async Task Dispatch_and_the_administrator_are_refused_rather_than_handed_an_unattached_row()
    {
        // The case worth pinning, because the plausible-looking alternative writes a row. A
        // dispatcher has no client row of their own, so "the caller's client id" is null for them -
        // and a request path that accepted null would create a delivery attached to nobody through
        // an endpoint whose entire purpose is attaching one. They compose through POST
        // /api/deliveries, where the client is an explicit field.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var admin = await FleetApi.TokenAsync(factory, client, UserRole.Admin, cancellationToken);

        foreach (var token in new[] { dispatcher, admin })
        {
            using var refused = await DeliveryApi.RequestAsync(
                client, token, DeliveryApi.NewRequest(), cancellationToken);

            await FleetApi.AssertFailureAsync(
                refused,
                HttpStatusCode.Forbidden,
                ErrorCode.AUTH_FORBIDDEN,
                cancellationToken);
        }

        await AssertNoDeliveriesAsync(factory, cancellationToken);
    }

    [Fact]
    public async Task A_client_account_with_no_client_row_is_refused_rather_than_widened()
    {
        // The missing-claim row of the matrix, arranged the only way it can be: through the Identity
        // port directly, because registration creates the account and the clients row together. A
        // guard that answered "unrestricted" here would create a delivery attached to nobody on the
        // strength of a broken claim.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var email = FleetApi.UniqueEmail();

        await using (var unitOfWork = await factory.Database.UnitOfWorkFactory
                         .CreateAsync(cancellationToken))
        {
            await unitOfWork.Users.EnsureRoleAsync(UserRole.Client, cancellationToken);

            await unitOfWork.Users.CreateAsync(
                new NewUserAccount("Олена", "Петренко", email),
                FleetApi.Password,
                UserRole.Client,
                cancellationToken);

            await unitOfWork.CommitAsync(cancellationToken);
        }

        var token = await FleetApi.SignInAsync(client, email, FleetApi.Password, cancellationToken);

        using (var refused = await DeliveryApi.RequestAsync(
                   client, token, DeliveryApi.NewRequest(), cancellationToken))
        {
            await FleetApi.AssertFailureAsync(
                refused,
                HttpStatusCode.Forbidden,
                ErrorCode.AUTH_FORBIDDEN,
                cancellationToken);
        }

        await AssertNoDeliveriesAsync(factory, cancellationToken);
    }

    // =====================================================================================
    // What dispatch does with it
    // =====================================================================================

    [Fact]
    public async Task The_request_appears_on_the_clients_own_list_and_on_dispatchs_board()
    {
        // FR-91's whole point: a request is not a second kind of record waiting somewhere. It is a
        // delivery, in the one collection dispatch already reads, with the requester named - and it
        // is on the client's own list, where the payload can carry no counterparty at all.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var requester = await DeliveryApi.ClientAsync(client, dispatcher, cancellationToken);

        int deliveryId;

        using (var created = await DeliveryApi.RequestAsync(
                   client, requester.Token, DeliveryApi.NewRequest(), cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);

            deliveryId = (await FleetApi.DataAsync(created, cancellationToken))
                .GetProperty("id")
                .GetInt32();
        }

        using (var mine = await FleetApi.SendAsync(
                   client,
                   HttpMethod.Get,
                   "/api/deliveries/mine",
                   requester.Token,
                   body: null,
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, mine.StatusCode);

            var row = (await FleetApi.DataAsync(mine, cancellationToken))
                .EnumerateArray()
                .Single(candidate => candidate.GetProperty("id").GetInt32() == deliveryId);

            Assert.Equal(nameof(DeliveryStatus.Pending), row.GetProperty("status").GetString());
            Assert.False(row.TryGetProperty("driver", out _));
        }

        using (var board = await FleetApi.SendAsync(
                   client,
                   HttpMethod.Get,
                   "/api/deliveries",
                   dispatcher,
                   body: null,
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, board.StatusCode);

            var row = (await FleetApi.DataAsync(board, cancellationToken))
                .EnumerateArray()
                .Single(candidate => candidate.GetProperty("id").GetInt32() == deliveryId);

            Assert.Equal(JsonValueKind.Null, row.GetProperty("driver").ValueKind);
            Assert.Equal(requester.ClientId, row.GetProperty("client").GetProperty("id").GetInt32());
            Assert.Contains(
                "Петренко",
                row.GetProperty("client").GetProperty("name").GetString()!,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Dispatch_assigns_a_driver_to_a_request_through_the_edit_path_it_already_has()
    {
        // The reason this story adds no dispatcher screen and no second write path: a requested
        // delivery is assigned by the same PUT that assigns any other, and the capacity invariant
        // applies to it unchanged (FR-103).
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var requester = await DeliveryApi.ClientAsync(client, dispatcher, cancellationToken);
        var vehicleId = await DeliveryApi.VehicleAsync(client, dispatcher, 1000m, cancellationToken);
        var driver = await DeliveryApi.DriverAsync(client, dispatcher, vehicleId, cancellationToken);

        int deliveryId;

        using (var created = await DeliveryApi.RequestAsync(
                   client, requester.Token, DeliveryApi.NewRequest(weight: 500m), cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);

            deliveryId = (await FleetApi.DataAsync(created, cancellationToken))
                .GetProperty("id")
                .GetInt32();
        }

        using (var assigned = await FleetApi.SendAsync(
                   client,
                   HttpMethod.Put,
                   $"/api/deliveries/{deliveryId}",
                   dispatcher,
                   new { driverId = driver.DriverId },
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, assigned.StatusCode);

            var data = await FleetApi.DataAsync(assigned, cancellationToken);

            Assert.Equal(driver.DriverId, data.GetProperty("driver").GetProperty("id").GetInt32());

            // The client the request was attached to survives the edit: an assignment names a
            // driver and says nothing about the requester.
            Assert.Equal(requester.ClientId, data.GetProperty("client").GetProperty("id").GetInt32());
        }
    }

    [Fact]
    public async Task Assigning_a_driver_whose_vehicle_is_too_small_for_a_request_is_still_a_conflict()
    {
        // FR-103 reaches a requested delivery the same way it reaches a dispatcher's own. The weight
        // came from a client, which is precisely why the check has to be on the assignment.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var requester = await DeliveryApi.ClientAsync(client, dispatcher, cancellationToken);
        var vehicleId = await DeliveryApi.VehicleAsync(client, dispatcher, 1000m, cancellationToken);
        var driver = await DeliveryApi.DriverAsync(client, dispatcher, vehicleId, cancellationToken);

        int deliveryId;

        using (var created = await DeliveryApi.RequestAsync(
                   client, requester.Token, DeliveryApi.NewRequest(weight: 1500m), cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);

            deliveryId = (await FleetApi.DataAsync(created, cancellationToken))
                .GetProperty("id")
                .GetInt32();
        }

        using (var refused = await FleetApi.SendAsync(
                   client,
                   HttpMethod.Put,
                   $"/api/deliveries/{deliveryId}",
                   dispatcher,
                   new { driverId = driver.DriverId },
                   cancellationToken))
        {
            await FleetApi.AssertFailureAsync(
                refused,
                HttpStatusCode.Conflict,
                ErrorCode.DELIVERY_EXCEEDS_VEHICLE_CAPACITY,
                cancellationToken);
        }

        await using var context = await factory.Database.ContextFactory
            .CreateDbContextAsync(cancellationToken);

        // Refused and nothing happened, which is the second half of the claim: the row is still
        // unassigned rather than assigned to a driver who cannot carry it.
        Assert.Null(
            (await context.Deliveries.SingleAsync(row => row.Id == deliveryId, cancellationToken))
                .DriverId);
    }

    // =====================================================================================
    // Address search (FR-104)
    // =====================================================================================

    [Fact]
    public async Task A_client_may_search_for_an_address_and_a_driver_may_not()
    {
        // The reservation this story retires, and the one it keeps. FR-104 is unqualified about who
        // sets a location by typing, and a client's request form sets two - so the search follows
        // the question "may this caller compose a delivery" rather than a role list. A driver
        // composes nothing and is still refused, so a public geocoder is not reachable by every
        // session that exists.
        var cancellationToken = TestContext.Current.CancellationToken;

        var geocoder = new FakeGeocoder
        {
            Matches = [new GeocodedPlace("Київ, вулиця Хрещатик, 1", 50.4472, 30.5222)],
        };

        await using var factory = await FleetApi.CreateAsync(
            postgres.ConnectionString,
            cancellationToken,
            OutboundPorts.Replace(geocoder, new FakeEmailSender()));

        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var requester = await DeliveryApi.ClientAsync(client, dispatcher, cancellationToken);
        var driver = await DeliveryApi.DriverAsync(client, dispatcher, vehicleId: null, cancellationToken);

        using (var allowed = await SearchAsync(client, requester.Token, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);

            Assert.Single((await FleetApi.DataAsync(allowed, cancellationToken)).EnumerateArray());
        }

        using (var refused = await SearchAsync(client, driver.Token, cancellationToken))
        {
            await FleetApi.AssertFailureAsync(
                refused,
                HttpStatusCode.Forbidden,
                ErrorCode.AUTH_FORBIDDEN,
                cancellationToken);
        }

        // One search reached the port and one did not, which is the whole claim: the driver was
        // refused before the query was looked at rather than after it was spent.
        Assert.Equal(new[] { "Хрещатик" }, geocoder.Searched);
    }

    private static Task<HttpResponseMessage> SearchAsync(
        HttpClient client,
        string token,
        CancellationToken cancellationToken) =>
        FleetApi.SendAsync(
            client,
            HttpMethod.Get,
            "/api/deliveries/places?query=" + Uri.EscapeDataString("Хрещатик"),
            token,
            body: null,
            cancellationToken);

    /// <summary>
    /// "Refused" and "refused and nothing happened" are different claims, and this suite makes the
    /// second one.
    /// </summary>
    private static async Task AssertNoDeliveriesAsync(
        ApiFactory factory,
        CancellationToken cancellationToken)
    {
        await using var context = await factory.Database.ContextFactory
            .CreateDbContextAsync(cancellationToken);

        Assert.Empty(await context.Deliveries.ToListAsync(cancellationToken));
    }
}
