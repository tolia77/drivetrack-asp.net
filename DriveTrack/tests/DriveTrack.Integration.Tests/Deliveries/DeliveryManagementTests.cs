using System.Net;
using System.Text.Json;
using DriveTrack.Application.Common;
using DriveTrack.Domain.Deliveries;
using DriveTrack.Domain.Identity;
using DriveTrack.Domain.Reviews;
using DriveTrack.Integration.Tests.Fleet;
using DriveTrack.Integration.Tests.Persistence;
using DriveTrack.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace DriveTrack.Integration.Tests.Deliveries;

/// <summary>
/// FR-14 to FR-24 and FR-100 to FR-103 over HTTP, against the real <c>Program.cs</c> pipeline.
/// <para>
/// Asserted through the adapter that ships the capability rather than against the service directly,
/// because half of what the story promises is about the adapter: which status a refusal carries,
/// which code, and whether anything was written when it was refused. The last of those is asserted
/// against the database every time — "refused" and "refused and nothing happened" are different
/// claims, and the second is the one the requirements make.
/// </para>
/// </summary>
public class DeliveryManagementTests(PostgresFixture postgres)
{
    // =====================================================================================
    // Creating
    // =====================================================================================

    [Fact]
    public async Task A_delivery_needs_only_two_points_a_description_and_a_weight()
    {
        // FR-16's minimum: no driver, no client, no notes, no window. Every one of those is
        // optional by requirement, and a create path that quietly required one would make the
        // ordinary case impossible.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);

        using var created = await FleetApi.SendAsync(
            client,
            HttpMethod.Post,
            "/api/deliveries",
            dispatcher,
            DeliveryApi.NewDelivery(),
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        var data = await FleetApi.DataAsync(created, cancellationToken);

        // FR-30: a delivery is only ever born Pending, and AD-21 puts the member name on the wire.
        Assert.Equal(nameof(DeliveryStatus.Pending), data.GetProperty("status").GetString());

        Assert.Equal(JsonValueKind.Null, data.GetProperty("driver").ValueKind);
        Assert.Equal(JsonValueKind.Null, data.GetProperty("client").ValueKind);
        Assert.False(data.GetProperty("isOverdue").GetBoolean());

        // DR-11 / FR-94: coordinates are the record and the address is a cache nothing has filled
        // yet - story 5.2 owns the geocoder, and this story has to work while there is none.
        Assert.Equal(50.4501, data.GetProperty("pickup").GetProperty("point").GetProperty("latitude").GetDouble());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("pickup").GetProperty("address").ValueKind);
        Assert.Equal(JsonValueKind.Null, data.GetProperty("dropoff").GetProperty("address").ValueKind);

        // AD-17: only DTOs leave the Application layer, so no EF navigation is serialized back.
        Assert.False(data.TryGetProperty("proofOfDelivery", out _));
        Assert.False(data.TryGetProperty("review", out _));
    }

    [Fact]
    public async Task A_delivery_may_name_a_driver_and_a_client_and_the_summary_names_them_back()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var driver = await DeliveryApi.DriverAsync(client, dispatcher, vehicleId: null, cancellationToken);
        var requester = await DeliveryApi.ClientAsync(client, dispatcher, cancellationToken);

        using var created = await FleetApi.SendAsync(
            client,
            HttpMethod.Post,
            "/api/deliveries",
            dispatcher,
            DeliveryApi.NewDelivery(driverId: driver.DriverId, clientId: requester.ClientId),
            cancellationToken);

        var data = await FleetApi.DataAsync(created, cancellationToken);

        Assert.Equal(driver.DriverId, data.GetProperty("driver").GetProperty("id").GetInt32());
        Assert.Equal(requester.ClientId, data.GetProperty("client").GetProperty("id").GetInt32());

        // The name costs a second read - Delivery has no navigation to either party, and a client's
        // name lives on the Identity account - so the fact that it arrives at all is the assertion.
        Assert.Contains("Шевченко", data.GetProperty("driver").GetProperty("name").GetString()!, StringComparison.Ordinal);
        Assert.Contains("Петренко", data.GetProperty("client").GetProperty("name").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_driver_that_does_not_exist_is_refused_by_name_and_nothing_is_written()
    {
        // FR-29, and a code of the capability's own rather than COMMON_NOT_FOUND: a dispatcher told
        // "driver not found" knows to pick another driver, where "not found" leaves them wondering
        // whether the endpoint exists.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);

        using (var refused = await FleetApi.SendAsync(
                   client,
                   HttpMethod.Post,
                   "/api/deliveries",
                   dispatcher,
                   DeliveryApi.NewDelivery(driverId: 999999),
                   cancellationToken))
        {
            await FleetApi.AssertFailureAsync(
                refused,
                HttpStatusCode.NotFound,
                ErrorCode.DELIVERY_DRIVER_NOT_FOUND,
                cancellationToken);
        }

        await AssertNoDeliveriesAsync(factory, cancellationToken);
    }

    [Fact]
    public async Task A_client_that_does_not_exist_is_refused_by_name_and_nothing_is_written()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);

        using (var refused = await FleetApi.SendAsync(
                   client,
                   HttpMethod.Post,
                   "/api/deliveries",
                   dispatcher,
                   DeliveryApi.NewDelivery(clientId: 999999),
                   cancellationToken))
        {
            await FleetApi.AssertFailureAsync(
                refused,
                HttpStatusCode.NotFound,
                ErrorCode.DELIVERY_CLIENT_NOT_FOUND,
                cancellationToken);
        }

        await AssertNoDeliveriesAsync(factory, cancellationToken);
    }

    [Fact]
    public async Task A_parcel_heavier_than_the_assigned_driver_holds_is_refused_and_nothing_is_written()
    {
        // FR-103's delivery-side arm. The refusal has to leave no row behind, or the invariant it
        // exists to keep would be broken by the very request that reported keeping it.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var vehicleId = await DeliveryApi.VehicleAsync(client, dispatcher, 1000m, cancellationToken);
        var driver = await DeliveryApi.DriverAsync(client, dispatcher, vehicleId, cancellationToken);

        using (var refused = await FleetApi.SendAsync(
                   client,
                   HttpMethod.Post,
                   "/api/deliveries",
                   dispatcher,
                   DeliveryApi.NewDelivery(weight: 1500m, driverId: driver.DriverId),
                   cancellationToken))
        {
            await FleetApi.AssertFailureAsync(
                refused,
                HttpStatusCode.Conflict,
                ErrorCode.DELIVERY_EXCEEDS_VEHICLE_CAPACITY,
                cancellationToken);
        }

        await AssertNoDeliveriesAsync(factory, cancellationToken);
    }

    [Fact]
    public async Task A_parcel_weighing_exactly_what_the_vehicle_carries_is_accepted()
    {
        // The boundary the invariant draws, which is inclusive: a thousand-kilogram vehicle carries
        // a thousand kilograms. Asserted because the two refusal cases either side of it are both
        // satisfied by a strict comparison, so `<=` could become `<` and nothing else would notice
        // - and the delivery a dispatcher would then be unable to book is the fully loaded one.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var vehicleId = await DeliveryApi.VehicleAsync(client, dispatcher, 1000m, cancellationToken);
        var driver = await DeliveryApi.DriverAsync(client, dispatcher, vehicleId, cancellationToken);

        using var created = await FleetApi.SendAsync(
            client,
            HttpMethod.Post,
            "/api/deliveries",
            dispatcher,
            DeliveryApi.NewDelivery(weight: 1000m, driverId: driver.DriverId),
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
    }

    [Fact]
    public async Task Two_parcels_that_each_fit_are_both_accepted_and_leave_the_fleet_free()
    {
        // FR-103 is per parcel, not per driver: a vehicle carries one at a time, so two six-hundred
        // kilogram deliveries on a thousand-kilogram vehicle are two ordinary jobs rather than an
        // overload. Both halves are asserted, because they are decided by different code - the
        // creates by the per-delivery comparison, and the reassignment by the aggregate, which is
        // where a Max read as a Sum would answer 1200 and refuse a change that is perfectly legal.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var held = await DeliveryApi.VehicleAsync(client, dispatcher, 1000m, cancellationToken);
        var replacement = await DeliveryApi.VehicleAsync(client, dispatcher, 1000m, cancellationToken);
        var driver = await DeliveryApi.DriverAsync(client, dispatcher, held, cancellationToken);

        for (var parcel = 0; parcel < 2; parcel++)
        {
            using var created = await FleetApi.SendAsync(
                client,
                HttpMethod.Post,
                "/api/deliveries",
                dispatcher,
                DeliveryApi.NewDelivery(weight: 600m, driverId: driver.DriverId),
                cancellationToken);

            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        }

        using var reassigned = await FleetApi.SendAsync(
            client,
            HttpMethod.Put,
            $"/api/drivers/{driver.DriverId}",
            dispatcher,
            new { vehicleId = replacement },
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, reassigned.StatusCode);
    }

    [Fact]
    public async Task A_window_bound_sent_at_another_offset_is_stored_as_the_same_instant()
    {
        // AD-13, and the one line between a correct request and a 500. The column is
        // `timestamp with time zone`, which Npgsql will only write a DateTimeOffset to when its
        // offset is zero - so a browser's local datetime, or a REST client in Kyiv, would otherwise
        // fail at the commit for a payload the caller got right. The offset carries no information
        // the instant does not, so it is converted rather than refused.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var sent = new DateTimeOffset(2026, 12, 1, 10, 0, 0, TimeSpan.FromHours(3));

        using var created = await FleetApi.SendAsync(
            client,
            HttpMethod.Post,
            "/api/deliveries",
            dispatcher,
            DeliveryApi.NewDelivery(windowLatestAt: sent),
            cancellationToken);

        // A 200 is half the assertion: the write reached the database, which is what the conversion
        // exists to make possible.
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        var echoed = (await FleetApi.DataAsync(created, cancellationToken))
            .GetProperty("windowLatestAt")
            .GetDateTimeOffset();

        Assert.Equal(TimeSpan.Zero, echoed.Offset);
        Assert.Equal(sent.ToUniversalTime(), echoed);
    }

    [Fact]
    public async Task A_driver_holding_no_vehicle_takes_no_capacity_check()
    {
        // FR-38: holding nothing is a legal state, not a check waiting to happen. Without this a
        // "capacity must be at least the weight" rule would refuse every unassigned vehicle.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var driver = await DeliveryApi.DriverAsync(client, dispatcher, vehicleId: null, cancellationToken);

        using var created = await FleetApi.SendAsync(
            client,
            HttpMethod.Post,
            "/api/deliveries",
            dispatcher,
            DeliveryApi.NewDelivery(weight: 9000m, driverId: driver.DriverId),
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
    }

    [Theory]
    [InlineData("packageWeightKg")]
    [InlineData("packageDetails")]
    [InlineData("windowLatestAt")]
    [InlineData("pickup")]
    public async Task A_value_the_row_may_not_hold_is_refused_before_any_query_naming_the_field(
        string field)
    {
        // NFR-4. The two check constraints behind the first and third of these refuse them too, and
        // with no field list at all - SQLSTATE 23514 carries a constraint name, not a property - so
        // the friendly validator is what makes the message attachable to an input.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);

        object body = field switch
        {
            "packageWeightKg" => Malformed(weight: 0m),
            "packageDetails" => Malformed(details: string.Empty),
            "windowLatestAt" => Malformed(
                earliest: new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero),
                latest: new DateTimeOffset(2026, 9, 8, 10, 0, 0, TimeSpan.Zero)),

            // Latitude 91 is the row that would answer 500 if MapLocation were bound directly: its
            // constructor throws, and a throwing constructor during model binding lands in the
            // suppressed model state (DW-12).
            _ => Malformed(latitude: 91),
        };

        using var refused = await FleetApi.SendAsync(
            client, HttpMethod.Post, "/api/deliveries", dispatcher, body, cancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);

        var error = (await FleetApi.ReadAsync(refused, cancellationToken)).GetProperty("error");

        Assert.Equal(nameof(ErrorCode.COMMON_VALIDATION_FAILED), error.GetProperty("code").GetString());

        var fields = error.GetProperty("fields")
            .EnumerateObject()
            .Select(property => property.Name)
            .ToArray();

        Assert.Contains(fields, name => name.StartsWith(field, StringComparison.Ordinal));

        await AssertNoDeliveriesAsync(factory, cancellationToken);
    }

    [Fact]
    public async Task A_driver_and_a_client_may_not_open_a_delivery()
    {
        // FR-14 is a dispatcher's, and the refusal is the guard's rather than an attribute's: the
        // controller carries [Authorize] with no roles precisely so this is decided in one place.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var driver = await DeliveryApi.DriverAsync(client, dispatcher, vehicleId: null, cancellationToken);
        var requester = await DeliveryApi.ClientAsync(client, dispatcher, cancellationToken);

        foreach (var token in new[] { driver.Token, requester.Token })
        {
            using var refused = await FleetApi.SendAsync(
                client,
                HttpMethod.Post,
                "/api/deliveries",
                token,
                DeliveryApi.NewDelivery(),
                cancellationToken);

            await FleetApi.AssertFailureAsync(
                refused, HttpStatusCode.Forbidden, ErrorCode.AUTH_FORBIDDEN, cancellationToken);
        }

        await AssertNoDeliveriesAsync(factory, cancellationToken);
    }

    // =====================================================================================
    // Reading
    // =====================================================================================

    [Fact]
    public async Task The_list_answers_every_delivery_oldest_first()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);

        var first = await DeliveryApi.PostAsync(client, dispatcher, DeliveryApi.NewDelivery(), cancellationToken);
        var second = await DeliveryApi.PostAsync(client, dispatcher, DeliveryApi.NewDelivery(), cancellationToken);
        var third = await DeliveryApi.PostAsync(client, dispatcher, DeliveryApi.NewDelivery(), cancellationToken);

        using var listed = await FleetApi.SendAsync(
            client, HttpMethod.Get, "/api/deliveries", dispatcher, body: null, cancellationToken);

        var ids = (await FleetApi.DataAsync(listed, cancellationToken))
            .EnumerateArray()
            .Select(row => row.GetProperty("id").GetInt32())
            .ToArray();

        // Explicitly ordered rather than left to the database: an unordered OFFSET is a page that
        // can repeat and skip rows between requests.
        Assert.Equal(new[] { first, second, third }, ids);
    }

    [Theory]
    [InlineData("?limit=0")]
    [InlineData("?limit=101")]
    [InlineData("?offset=-1")]
    public async Task A_page_outside_the_bounds_is_refused_before_any_query(string query)
    {
        // NFR-27: validated rather than passed through. A limit of zero is a refusal, not "give me
        // nothing", which is also why the controller's parameters are nullable.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);

        using var refused = await FleetApi.SendAsync(
            client, HttpMethod.Get, "/api/deliveries" + query, dispatcher, body: null, cancellationToken);

        await FleetApi.AssertFailureAsync(
            refused,
            HttpStatusCode.UnprocessableEntity,
            ErrorCode.COMMON_VALIDATION_FAILED,
            cancellationToken);
    }

    [Fact]
    public async Task An_admin_reads_one_delivery_and_a_driver_is_refused_the_detail_endpoint()
    {
        // The detail endpoint is dispatch's. A driver reads their own rows through /mine, which
        // answers a type that has no field a counterparty's name could be written into (AD-17).
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var admin = await FleetApi.TokenAsync(factory, client, UserRole.Admin, cancellationToken);
        var driver = await DeliveryApi.DriverAsync(client, dispatcher, vehicleId: null, cancellationToken);

        var deliveryId = await DeliveryApi.PostAsync(
            client, dispatcher, DeliveryApi.NewDelivery(driverId: driver.DriverId), cancellationToken);

        using (var fetched = await FleetApi.SendAsync(
                   client,
                   HttpMethod.Get,
                   $"/api/deliveries/{deliveryId}",
                   admin,
                   body: null,
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);

            var data = await FleetApi.DataAsync(fetched, cancellationToken);

            Assert.Equal(deliveryId, data.GetProperty("id").GetInt32());
        }

        using var refused = await FleetApi.SendAsync(
            client,
            HttpMethod.Get,
            $"/api/deliveries/{deliveryId}",
            driver.Token,
            body: null,
            cancellationToken);

        await FleetApi.AssertFailureAsync(
            refused, HttpStatusCode.Forbidden, ErrorCode.AUTH_FORBIDDEN, cancellationToken);
    }

    [Fact]
    public async Task An_id_with_no_row_is_not_found_on_every_verb()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        // The admin, so the delete arm is refused for the missing row rather than for the caller.
        var admin = await FleetApi.TokenAsync(factory, client, UserRole.Admin, cancellationToken);

        foreach (var (method, body) in new (HttpMethod Method, object? Body)[]
                 {
                     (HttpMethod.Get, null),
                     (HttpMethod.Put, new { packageWeightKg = 5m }),
                     (HttpMethod.Delete, null),
                 })
        {
            using var response = await FleetApi.SendAsync(
                client, method, "/api/deliveries/999999", admin, body, cancellationToken);

            await FleetApi.AssertFailureAsync(
                response, HttpStatusCode.NotFound, ErrorCode.COMMON_NOT_FOUND, cancellationToken);
        }
    }

    [Fact]
    public async Task Overdue_is_derived_from_the_injected_clock_and_stored_nowhere()
    {
        // FR-19. The clock is moved rather than the row, which is the only thing that can tell a
        // derived flag from a stored one: nothing writes to the delivery between the two reads, and
        // the same row answers differently because the time did.
        var cancellationToken = TestContext.Current.CancellationToken;
        var clock = new FixedTimeProvider(DateTimeOffset.UtcNow);

        await using var factory = await ApiFactory.CreateAsync(
            postgres.ConnectionString,
            cancellationToken,
            useProbeAuthentication: false,
            clock);

        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var deadline = clock.Now.AddHours(1);

        var deliveryId = await DeliveryApi.PostAsync(
            client,
            dispatcher,
            DeliveryApi.NewDelivery(windowLatestAt: deadline),
            cancellationToken);

        Assert.False(await IsOverdueAsync(client, dispatcher, deliveryId, cancellationToken));

        // Two hours later, with nothing written in between.
        clock.Now = clock.Now.AddHours(2);

        Assert.True(await IsOverdueAsync(client, dispatcher, deliveryId, cancellationToken));

        // And a delivered parcel is never overdue, however long the window has been shut: it
        // arrived, and when it arrived is the timeline's record rather than a flag's. Walked through
        // the entity's own mutator rather than through the endpoint, because what is under test is
        // the derivation and not how a delivery becomes Delivered - but through TryChangeStatus all
        // the same, since that is the only assignment to Status the system has (AD-10).
        await using (var delivered = await factory.Database.ContextFactory
                         .CreateDbContextAsync(cancellationToken))
        {
            var stored = await delivered.Deliveries.SingleAsync(row => row.Id == deliveryId, cancellationToken);

            Seed.Advance(stored, DeliveryStatus.Delivered);

            await delivered.SaveChangesAsync(cancellationToken);
        }

        Assert.False(await IsOverdueAsync(client, dispatcher, deliveryId, cancellationToken));

        // And no column holds it: the deliveries table has weight, window, status and created-at,
        // and nothing that could be an overdue flag.
        await using var context = await factory.Database.ContextFactory
            .CreateDbContextAsync(cancellationToken);

        var columns = context.Model
            .FindEntityType(typeof(Delivery))!
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();

        Assert.DoesNotContain(columns, name => name.Contains("Overdue", StringComparison.OrdinalIgnoreCase));
    }

    // =====================================================================================
    // Editing
    // =====================================================================================

    [Fact]
    public async Task A_driver_and_a_client_may_not_change_a_delivery()
    {
        // FR-22 is a dispatcher's, and the update verb is where the role check is easiest to lose:
        // it is the one method whose guard call sits after a load, and GuardCoverageTests only
        // proves that some guard member is called, so swapping RequireRole for RequireScope here
        // would leave every other test green while a driver gained the whole board's edit rights.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var driver = await DeliveryApi.DriverAsync(client, dispatcher, vehicleId: null, cancellationToken);
        var requester = await DeliveryApi.ClientAsync(client, dispatcher, cancellationToken);

        // The delivery is the caller's own, which is the case a scope check would wave through: a
        // driver may read this row through /api/deliveries/mine, and it must still not be theirs to
        // edit.
        var deliveryId = await DeliveryApi.PostAsync(
            client,
            dispatcher,
            DeliveryApi.NewDelivery(
                weight: 12.5m,
                driverId: driver.DriverId,
                clientId: requester.ClientId),
            cancellationToken);

        foreach (var token in new[] { driver.Token, requester.Token })
        {
            using var refused = await FleetApi.SendAsync(
                client,
                HttpMethod.Put,
                $"/api/deliveries/{deliveryId}",
                token,
                new { packageWeightKg = 999m },
                cancellationToken);

            await FleetApi.AssertFailureAsync(
                refused, HttpStatusCode.Forbidden, ErrorCode.AUTH_FORBIDDEN, cancellationToken);
        }

        await using var context = await factory.Database.ContextFactory
            .CreateDbContextAsync(cancellationToken);

        // Refused, and nothing written: a 403 raised after the row had already been assigned would
        // still have changed it.
        Assert.Equal(
            12.5m,
            (await context.Deliveries.SingleAsync(row => row.Id == deliveryId, cancellationToken))
                .PackageWeightKg);
    }

    [Fact]
    public async Task An_update_naming_one_field_leaves_every_other_column_alone()
    {
        // AD-23's absent case, asserted across the whole row rather than on one field: an update
        // that read the payload instead of the merge would blank the description, the window and
        // both parties, and a test that only checked the weight would not notice.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var driver = await DeliveryApi.DriverAsync(client, dispatcher, vehicleId: null, cancellationToken);
        var requester = await DeliveryApi.ClientAsync(client, dispatcher, cancellationToken);
        var deadline = new DateTimeOffset(2026, 12, 1, 10, 0, 0, TimeSpan.Zero);

        var deliveryId = await DeliveryApi.PostAsync(
            client,
            dispatcher,
            DeliveryApi.NewDelivery(
                driverId: driver.DriverId,
                clientId: requester.ClientId,
                notes: "Подзвонити",
                windowLatestAt: deadline),
            cancellationToken);

        using var updated = await FleetApi.SendAsync(
            client,
            HttpMethod.Put,
            $"/api/deliveries/{deliveryId}",
            dispatcher,
            new { packageWeightKg = 33.25m },
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);

        var data = await FleetApi.DataAsync(updated, cancellationToken);

        Assert.Equal(33.25m, data.GetProperty("packageWeightKg").GetDecimal());
        Assert.Equal("Одна палета", data.GetProperty("packageDetails").GetString());
        Assert.Equal("Подзвонити", data.GetProperty("deliveryNotes").GetString());
        Assert.Equal(deadline, data.GetProperty("windowLatestAt").GetDateTimeOffset());
        Assert.Equal(driver.DriverId, data.GetProperty("driver").GetProperty("id").GetInt32());
        Assert.Equal(requester.ClientId, data.GetProperty("client").GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task An_explicit_null_driver_unassigns_and_an_absent_one_leaves_the_assignment_alone()
    {
        // FR-16 through AD-23, and the whole reason Optional exists: the two requests differ only
        // in whether the field is present, and they have to mean different things.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var driver = await DeliveryApi.DriverAsync(client, dispatcher, vehicleId: null, cancellationToken);

        var deliveryId = await DeliveryApi.PostAsync(
            client, dispatcher, DeliveryApi.NewDelivery(driverId: driver.DriverId), cancellationToken);

        using (var untouched = await FleetApi.SendAsync(
                   client,
                   HttpMethod.Put,
                   $"/api/deliveries/{deliveryId}",
                   dispatcher,
                   new { packageWeightKg = 14m },
                   cancellationToken))
        {
            var data = await FleetApi.DataAsync(untouched, cancellationToken);

            Assert.Equal(driver.DriverId, data.GetProperty("driver").GetProperty("id").GetInt32());
        }

        using (var released = await FleetApi.SendAsync(
                   client,
                   HttpMethod.Put,
                   $"/api/deliveries/{deliveryId}",
                   dispatcher,
                   new { driverId = (int?)null },
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, released.StatusCode);

            var data = await FleetApi.DataAsync(released, cancellationToken);

            Assert.Equal(JsonValueKind.Null, data.GetProperty("driver").ValueKind);
        }

        await using var context = await factory.Database.ContextFactory
            .CreateDbContextAsync(cancellationToken);

        Assert.Equal(
            1,
            await context.Deliveries.CountAsync(
                row => row.Id == deliveryId && row.DriverId == null,
                cancellationToken));
    }

    [Fact]
    public async Task Raising_the_weight_past_the_assigned_vehicle_is_refused_and_nothing_is_written()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var vehicleId = await DeliveryApi.VehicleAsync(client, dispatcher, 1000m, cancellationToken);
        var driver = await DeliveryApi.DriverAsync(client, dispatcher, vehicleId, cancellationToken);

        var deliveryId = await DeliveryApi.PostAsync(
            client,
            dispatcher,
            DeliveryApi.NewDelivery(weight: 500m, driverId: driver.DriverId),
            cancellationToken);

        using (var refused = await FleetApi.SendAsync(
                   client,
                   HttpMethod.Put,
                   $"/api/deliveries/{deliveryId}",
                   dispatcher,
                   new { packageWeightKg = 1500m },
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

        Assert.Equal(
            1,
            await context.Deliveries.CountAsync(
                row => row.Id == deliveryId && row.PackageWeightKg == 500m,
                cancellationToken));
    }

    [Fact]
    public async Task Moving_a_location_updates_the_delivery_row_and_drops_the_address_it_cached()
    {
        // AD-11: a location is columns of this row, so moving one cannot orphan anything. DR-11:
        // the address describes where the point used to be, so it goes with the move - leaving a
        // stale address beside new coordinates is the one thing DR-11 forbids.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);

        var deliveryId = await DeliveryApi.PostAsync(
            client, dispatcher, DeliveryApi.NewDelivery(), cancellationToken);

        // Story 5.2's geocoder does not exist yet, so the cache is filled by hand - on both points,
        // because the claim has two halves: the address of the point that moves is discarded, and
        // the address of the point that does not is kept. Seeding only the dropoff would leave a
        // "reset every address on every edit" implementation looking correct.
        await using (var seeded = await factory.Database.ContextFactory
                         .CreateDbContextAsync(cancellationToken))
        {
            var stored = await seeded.Deliveries.SingleAsync(row => row.Id == deliveryId, cancellationToken);

            stored.PickupLocation = new DriveTrack.Domain.Common.Location(
                stored.PickupLocation.Latitude,
                stored.PickupLocation.Longitude,
                "Київ, Хрещатик",
                Seed.Instant);

            stored.DropoffLocation = new DriveTrack.Domain.Common.Location(
                stored.DropoffLocation.Latitude,
                stored.DropoffLocation.Longitude,
                "Львів, площа Ринок",
                Seed.Instant);

            await seeded.SaveChangesAsync(cancellationToken);
        }

        using (var moved = await FleetApi.SendAsync(
                   client,
                   HttpMethod.Put,
                   $"/api/deliveries/{deliveryId}",
                   dispatcher,
                   new { dropoff = new { latitude = 46.4825, longitude = 30.7233 } },
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, moved.StatusCode);

            var data = await FleetApi.DataAsync(moved, cancellationToken);
            var dropoff = data.GetProperty("dropoff");

            Assert.Equal(46.4825, dropoff.GetProperty("point").GetProperty("latitude").GetDouble());
            Assert.Equal(JsonValueKind.Null, dropoff.GetProperty("address").ValueKind);

            // The pickup was not sent, so the merge filled it in from the row and the coordinates
            // are the ones it already had - which means it did not move, which means its address
            // still describes it. Discarding that too would throw away work story 5.2 does on
            // every unrelated edit.
            Assert.Equal(
                "Київ, Хрещатик",
                data.GetProperty("pickup").GetProperty("address").GetString());
        }

        await using var context = await factory.Database.ContextFactory
            .CreateDbContextAsync(cancellationToken);

        // One row, updated in place: there is no locations table for a move to leave a row behind
        // in, and the count is what says so.
        Assert.Equal(1, await context.Deliveries.CountAsync(cancellationToken));
    }

    [Fact]
    public async Task Swapping_a_driver_onto_a_smaller_vehicle_is_refused_and_removing_it_is_not()
    {
        // FR-103's fleet-side arm, which is where the invariant is easiest to lose: the delivery is
        // untouched and the change is somebody else's, so nothing about the delivery request could
        // have caught it. Removing the vehicle instead is allowed - a driver holding nothing takes
        // no check (FR-38).
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var large = await DeliveryApi.VehicleAsync(client, dispatcher, 2000m, cancellationToken);
        var small = await DeliveryApi.VehicleAsync(client, dispatcher, 1200m, cancellationToken);
        var driver = await DeliveryApi.DriverAsync(client, dispatcher, large, cancellationToken);

        await DeliveryApi.PostAsync(
            client,
            dispatcher,
            DeliveryApi.NewDelivery(weight: 1500m, driverId: driver.DriverId),
            cancellationToken);

        using (var refused = await FleetApi.SendAsync(
                   client,
                   HttpMethod.Put,
                   $"/api/drivers/{driver.DriverId}",
                   dispatcher,
                   new { vehicleId = small },
                   cancellationToken))
        {
            await FleetApi.AssertFailureAsync(
                refused,
                HttpStatusCode.Conflict,
                ErrorCode.DELIVERY_EXCEEDS_VEHICLE_CAPACITY,
                cancellationToken);
        }

        await using (var context = await factory.Database.ContextFactory
                         .CreateDbContextAsync(cancellationToken))
        {
            // The assignment is unchanged: a refused change that half-applied would be worse than
            // one that was allowed.
            Assert.Equal(
                1,
                await context.Drivers.CountAsync(
                    row => row.Id == new DriverId(driver.DriverId) && row.VehicleId == large,
                    cancellationToken));
        }

        using var cleared = await FleetApi.SendAsync(
            client,
            HttpMethod.Put,
            $"/api/drivers/{driver.DriverId}",
            dispatcher,
            new { vehicleId = (int?)null },
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
    }

    [Fact]
    public async Task Lowering_the_capacity_of_a_held_vehicle_below_its_load_is_refused()
    {
        // FR-103's third mover. Neither the delivery nor the assignment changes here - only the
        // figure recorded against the vehicle the driver already holds - and it reaches the same
        // forbidden state the other two arms refuse. Editing a vehicle nobody holds, or raising the
        // capacity, is untouched by this: the check runs only when the figure actually falls short.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var vehicleId = await DeliveryApi.VehicleAsync(client, dispatcher, 2000m, cancellationToken);
        var driver = await DeliveryApi.DriverAsync(client, dispatcher, vehicleId, cancellationToken);

        await DeliveryApi.PostAsync(
            client,
            dispatcher,
            DeliveryApi.NewDelivery(weight: 1500m, driverId: driver.DriverId),
            cancellationToken);

        using (var refused = await FleetApi.SendAsync(
                   client,
                   HttpMethod.Put,
                   $"/api/vehicles/{vehicleId}",
                   dispatcher,
                   new { capacityKg = 1000m },
                   cancellationToken))
        {
            await FleetApi.AssertFailureAsync(
                refused,
                HttpStatusCode.Conflict,
                ErrorCode.DELIVERY_EXCEEDS_VEHICLE_CAPACITY,
                cancellationToken);
        }

        await using (var context = await factory.Database.ContextFactory
                         .CreateDbContextAsync(cancellationToken))
        {
            // Nothing was written: the refusal rolls the whole edit back, so the vehicle still
            // carries the figure the delivery was booked against.
            Assert.Equal(
                1,
                await context.Vehicles.CountAsync(
                    row => row.Id == vehicleId && row.CapacityKg == 2000m,
                    cancellationToken));
        }

        // And the edit a dispatcher meant to make is still available: raising it, or lowering it to
        // something the load still fits inside, is not this rule's business.
        using var allowed = await FleetApi.SendAsync(
            client,
            HttpMethod.Put,
            $"/api/vehicles/{vehicleId}",
            dispatcher,
            new { capacityKg = 1600m },
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Theory]
    [InlineData(DeliveryStatus.Pending, HttpStatusCode.Conflict)]
    [InlineData(DeliveryStatus.InTransit, HttpStatusCode.Conflict)]
    [InlineData(DeliveryStatus.Delivered, HttpStatusCode.OK)]
    [InlineData(DeliveryStatus.Failed, HttpStatusCode.OK)]
    public async Task Only_a_parcel_still_to_be_carried_blocks_the_fleet(
        DeliveryStatus status,
        HttpStatusCode expected)
    {
        // The whole of FindHeaviestActiveWeightForDriverAsync's status set, both halves pinned. A
        // parcel already carried must stop blocking a vehicle change, or a job finished last winter
        // holds the fleet for good; one still to be carried must keep blocking it, or FR-103 breaks
        // at the exact moment the parcel is in the van. Asserting only the Delivered half would let
        // the predicate narrow to Pending alone and stay green.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var large = await DeliveryApi.VehicleAsync(client, dispatcher, 2000m, cancellationToken);
        var small = await DeliveryApi.VehicleAsync(client, dispatcher, 1200m, cancellationToken);
        var driver = await DeliveryApi.DriverAsync(client, dispatcher, large, cancellationToken);

        var deliveryId = await DeliveryApi.PostAsync(
            client,
            dispatcher,
            DeliveryApi.NewDelivery(weight: 1500m, driverId: driver.DriverId),
            cancellationToken);

        // Walked to the status rather than posted through the endpoint: what is under test is which
        // rows the invariant counts, not how one reaches a status. It still goes through
        // TryChangeStatus, which is the only assignment to Status anywhere (AD-10) - so this cannot
        // seed a state the lifecycle forbids.
        await using (var context = await factory.Database.ContextFactory
                         .CreateDbContextAsync(cancellationToken))
        {
            var stored = await context.Deliveries.SingleAsync(row => row.Id == deliveryId, cancellationToken);

            Seed.Advance(stored, status);

            await context.SaveChangesAsync(cancellationToken);
        }

        using var reassigned = await FleetApi.SendAsync(
            client,
            HttpMethod.Put,
            $"/api/drivers/{driver.DriverId}",
            dispatcher,
            new { vehicleId = small },
            cancellationToken);

        Assert.Equal(expected, reassigned.StatusCode);

        var held = expected == HttpStatusCode.OK ? small : large;

        await using (var context = await factory.Database.ContextFactory
                         .CreateDbContextAsync(cancellationToken))
        {
            // The status code alone would not separate a refusal from one that half-applied, so the
            // assignment itself is read back: refused leaves the large vehicle, allowed moves.
            Assert.Equal(
                1,
                await context.Drivers.CountAsync(
                    row => row.Id == new DriverId(driver.DriverId) && row.VehicleId == held,
                    cancellationToken));
        }
    }

    // =====================================================================================
    // Deleting
    // =====================================================================================

    [Fact]
    public async Task An_admin_deletes_a_delivery_and_all_four_dependents_go_with_it()
    {
        // FR-24 and DR-9. The delete goes through the endpoint rather than through SQL, which is
        // what makes this a test of the service's un-included load as well as of the cascade: a
        // tracked timeline entry would make EF issue its own DELETE at trigger depth 1, where the
        // append-only guard raises, and the request would answer a 500 instead.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);
        var admin = await FleetApi.TokenAsync(factory, client, UserRole.Admin, cancellationToken);
        var requester = await DeliveryApi.ClientAsync(client, dispatcher, cancellationToken);

        var deliveryId = await DeliveryApi.PostAsync(
            client,
            dispatcher,
            DeliveryApi.NewDelivery(clientId: requester.ClientId),
            cancellationToken);

        await using (var context = await factory.Database.ContextFactory
                         .CreateDbContextAsync(cancellationToken))
        {
            var user = await Seed.UserAsync(context, cancellationToken);

            context.TimelineEntries.Add(Seed.NewTimelineEntry(deliveryId, new UserId(user.Id)));

            var proof = new ProofOfDelivery
            {
                DeliveryId = deliveryId,
                RecipientName = "Отримувач",
                CaptureLocation = Seed.Location(),
                CapturedAt = Seed.Instant,
                CapturedByUserId = new UserId(user.Id),
            };
            proof.Assets.Add(new ProofAsset
            {
                Kind = ProofAssetKind.Signature,
                StorageKey = "proof/1/signature",
                ContentType = "image/png",
            });
            context.ProofOfDeliveries.Add(proof);

            context.Reviews.Add(new Review
            {
                DeliveryId = deliveryId,
                ClientId = new ClientId(requester.ClientId),
                Rating = 4,
                Text = "Добре.",
                CreatedAt = Seed.Instant,
            });

            context.NotificationAttempts.Add(new NotificationAttempt
            {
                DeliveryId = deliveryId,
                Kind = NotificationKind.StatusChange,
                Recipient = "someone@drivetrack.test",
                AttemptedAt = Seed.Instant,
                Outcome = NotificationOutcome.Failed,
                Error = "SMTP timeout",
            });

            await context.SaveChangesAsync(cancellationToken);
        }

        using (var deleted = await FleetApi.SendAsync(
                   client,
                   HttpMethod.Delete,
                   $"/api/deliveries/{deliveryId}",
                   admin,
                   body: null,
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);

            Assert.True((await FleetApi.ReadAsync(deleted, cancellationToken))
                .GetProperty("success")
                .GetBoolean());
        }

        await using var after = await factory.Database.ContextFactory
            .CreateDbContextAsync(cancellationToken);

        Assert.Equal(0, await after.Deliveries.CountAsync(cancellationToken));
        Assert.Equal(0, await after.TimelineEntries.CountAsync(cancellationToken));
        Assert.Equal(0, await after.ProofOfDeliveries.CountAsync(cancellationToken));
        Assert.Equal(0, await after.Reviews.CountAsync(cancellationToken));
        Assert.Equal(0, await after.NotificationAttempts.CountAsync(cancellationToken));
    }

    [Fact]
    public async Task A_dispatcher_may_not_delete_a_delivery_and_the_row_survives()
    {
        // FR-24 reserves deletion to an administrator. This is the one operation in the capability
        // where "dispatcher or admin" is not the rule, so it is the one worth pinning.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await DeliveryApi.DispatcherAsync(factory, client, cancellationToken);

        var deliveryId = await DeliveryApi.PostAsync(
            client, dispatcher, DeliveryApi.NewDelivery(), cancellationToken);

        using (var refused = await FleetApi.SendAsync(
                   client,
                   HttpMethod.Delete,
                   $"/api/deliveries/{deliveryId}",
                   dispatcher,
                   body: null,
                   cancellationToken))
        {
            await FleetApi.AssertFailureAsync(
                refused, HttpStatusCode.Forbidden, ErrorCode.AUTH_FORBIDDEN, cancellationToken);
        }

        await using var context = await factory.Database.ContextFactory
            .CreateDbContextAsync(cancellationToken);

        Assert.Equal(1, await context.Deliveries.CountAsync(row => row.Id == deliveryId, cancellationToken));
    }

    // -------------------------------------------------------------------------------------
    // Shared arrangement
    // -------------------------------------------------------------------------------------

    /// <summary>A create body with exactly one thing wrong with it.</summary>
    private static object Malformed(
        decimal weight = 12.5m,
        string? details = "Одна палета",
        double latitude = 50.4501,
        DateTimeOffset? earliest = null,
        DateTimeOffset? latest = null) =>
        new
        {
            pickup = new { latitude, longitude = 30.5234 },
            dropoff = new { latitude = 49.8397, longitude = 24.0297 },
            packageDetails = details,
            packageWeightKg = weight,
            windowEarliestAt = earliest,
            windowLatestAt = latest,
        };

    private static async Task<bool> IsOverdueAsync(
        HttpClient client,
        string token,
        int deliveryId,
        CancellationToken cancellationToken)
    {
        using var response = await FleetApi.SendAsync(
            client, HttpMethod.Get, $"/api/deliveries/{deliveryId}", token, body: null, cancellationToken);

        return (await FleetApi.DataAsync(response, cancellationToken))
            .GetProperty("isOverdue")
            .GetBoolean();
    }

    /// <summary>
    /// "Refused" and "refused and nothing happened" are different claims, and every refusal in this
    /// story makes the second.
    /// </summary>
    private static async Task AssertNoDeliveriesAsync(
        ApiFactory factory,
        CancellationToken cancellationToken)
    {
        await using var context = await factory.Database.ContextFactory
            .CreateDbContextAsync(cancellationToken);

        Assert.Equal(0, await context.Deliveries.CountAsync(cancellationToken));
    }
}
