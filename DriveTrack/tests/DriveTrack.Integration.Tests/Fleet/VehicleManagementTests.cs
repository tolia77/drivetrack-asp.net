using System.Net;
using System.Text.Json;
using DriveTrack.Application.Common;
using DriveTrack.Domain.Identity;
using DriveTrack.Integration.Tests.Persistence;
using DriveTrack.Integration.Tests.Support;
using DriveTrack.Web.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace DriveTrack.Integration.Tests.Fleet;

/// <summary>
/// FR-40 to FR-45 over HTTP, against the real <c>Program.cs</c> pipeline.
/// <para>
/// The outermost surface the intent reaches, and the only one where the status, the envelope and
/// what actually landed in the database are asserted together. The headline row is FR-43: the
/// original system let a vehicle be assigned and never released, which made an assigned vehicle
/// permanently undeletable — so "refused, and here is the way out" is the claim, not merely
/// "refused".
/// </para>
/// </summary>
public class VehicleManagementTests(PostgresFixture postgres)
{
    [Fact]
    public async Task A_dispatcher_creates_a_vehicle_and_reads_it_back()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var token = await FleetApi.TokenAsync(factory, client, UserRole.Dispatcher, cancellationToken);
        var plate = FleetApi.UniquePlate();

        int id;

        using (var created = await FleetApi.SendAsync(
                   client,
                   HttpMethod.Post,
                   "/api/vehicles",
                   token,
                   new
                   {
                       model = "Рено Мастер",
                       licensePlate = plate,
                       capacityKg = 1200m,
                       mileage = 42_000,
                       nextMaintenanceDate = "2026-12-01",
                   },
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);

            var data = await FleetApi.DataAsync(created, cancellationToken);

            id = data.GetProperty("id").GetInt32();

            Assert.Equal(plate, data.GetProperty("licensePlate").GetString());
            Assert.Equal(1200m, data.GetProperty("capacityKg").GetDecimal());

            // FR-40 names all five. These two are read by no logic in this milestone, which is
            // exactly why the round trip has to be asserted: nothing else would notice them being
            // dropped on the way in.
            Assert.Equal(42_000, data.GetProperty("mileage").GetInt32());
            Assert.Equal("2026-12-01", data.GetProperty("nextMaintenanceDate").GetString());
        }

        Assert.True(id > 0);

        using var fetched = await FleetApi.SendAsync(
            client, HttpMethod.Get, $"/api/vehicles/{id}", token, body: null, cancellationToken);

        var vehicle = await FleetApi.DataAsync(fetched, cancellationToken);

        Assert.Equal("Рено Мастер", vehicle.GetProperty("model").GetString());
        Assert.Equal(42_000, vehicle.GetProperty("mileage").GetInt32());
        Assert.Equal("2026-12-01", vehicle.GetProperty("nextMaintenanceDate").GetString());

        await using var context = await factory.Database.ContextFactory.CreateDbContextAsync(cancellationToken);

        Assert.Equal(1, await context.Vehicles.CountAsync(row => row.LicensePlate == plate, cancellationToken));
    }

    [Fact]
    public async Task A_plate_that_differs_only_by_whitespace_or_case_is_a_conflict()
    {
        // FR-41. The friendly check answers FLEET_LICENSE_PLATE_IN_USE and the unique index behind
        // it answers PERSISTENCE_UNIQUE_VIOLATION; both are 409, so either is a correct outcome and
        // the caller cannot tell which one ran (NFR-2).
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var token = await FleetApi.TokenAsync(factory, client, UserRole.Dispatcher, cancellationToken);
        var plate = FleetApi.UniquePlate();

        using (var first = await CreateVehicleAsync(client, token, plate, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        foreach (var variant in new[] { "  " + plate + "  ", plate.ToLowerInvariant() })
        {
            using var second = await CreateVehicleAsync(client, token, variant, cancellationToken);

            Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

            var error = (await FleetApi.ReadAsync(second, cancellationToken)).GetProperty("error");

            // The friendly check, specifically. Accepting the unique violation as well would
            // let the application-level check be deleted outright with this test still green -
            // and it is the check that produces a code a caller can act on. The index behind
            // it is the race backstop and answers the same 409 (NFR-2), but a plate normalized
            // on write means the friendly answer always gets there first.
            Assert.Equal(
                nameof(ErrorCode.FLEET_LICENSE_PLATE_IN_USE),
                error.GetProperty("code").GetString());

            // NFR-3: nothing about the schema reaches the client.
            var message = error.GetProperty("message").GetString() ?? string.Empty;

            Assert.DoesNotContain("SQLSTATE", message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("license_plate", message, StringComparison.OrdinalIgnoreCase);
        }

        await using var context = await factory.Database.ContextFactory.CreateDbContextAsync(cancellationToken);

        // The plate was trimmed on the way in, and the refusals wrote nothing.
        Assert.Equal(1, await context.Vehicles.CountAsync(row => row.LicensePlate == plate, cancellationToken));
    }

    [Fact]
    public async Task A_plate_is_stored_normalized_so_the_unique_index_carries_the_rule()
    {
        // A plate is a case-insensitive identifier, and the unique index on the column is not.
        // Normalizing on write is what closes that gap without a migration - so this asserts
        // the stored form, not merely that the request succeeded. Store it as typed and two
        // concurrent creates differing only in case both pass the check and both commit.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var token = await FleetApi.TokenAsync(factory, client, UserRole.Dispatcher, cancellationToken);
        var plate = FleetApi.UniquePlate();

        int id;

        using (var created = await CreateVehicleAsync(
                   client, token, "  " + plate.ToLowerInvariant() + "  ", cancellationToken))
        {
            var data = await FleetApi.DataAsync(created, cancellationToken);

            id = data.GetProperty("id").GetInt32();

            Assert.Equal(plate, data.GetProperty("licensePlate").GetString());
        }

        await using var context = await factory.Database.ContextFactory.CreateDbContextAsync(cancellationToken);

        Assert.Equal(
            1,
            await context.Vehicles.CountAsync(
                row => row.Id == id && row.LicensePlate == plate,
                cancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_update_moving_a_plate_onto_another_vehicle_is_a_conflict(bool lowerCase)
    {
        // FR-41 holds on the way through an edit too. Tested because no other PUT body in this
        // suite carries a licensePlate at all: without this the duplicate check in UpdateAsync
        // could be deleted with nothing failing.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var token = await FleetApi.TokenAsync(factory, client, UserRole.Dispatcher, cancellationToken);
        var takenPlate = FleetApi.UniquePlate();

        using (var first = await CreateVehicleAsync(client, token, takenPlate, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        var otherId = await NewVehicleAsync(client, token, cancellationToken);
        var sent = lowerCase ? takenPlate.ToLowerInvariant() : takenPlate;

        using var refused = await FleetApi.SendAsync(
            client,
            HttpMethod.Put,
            $"/api/vehicles/{otherId}",
            token,
            new { licensePlate = sent },
            cancellationToken);

        await FleetApi.AssertFailureAsync(
            refused,
            HttpStatusCode.Conflict,
            ErrorCode.FLEET_LICENSE_PLATE_IN_USE,
            cancellationToken);

        await using var context = await factory.Database.ContextFactory.CreateDbContextAsync(cancellationToken);

        // One vehicle carries it, and it is not the one the edit tried to move it to.
        Assert.Equal(
            1,
            await context.Vehicles.CountAsync(row => row.LicensePlate == takenPlate, cancellationToken));
        Assert.Equal(
            0,
            await context.Vehicles.CountAsync(
                row => row.Id == otherId && row.LicensePlate == takenPlate,
                cancellationToken));
    }

    [Fact]
    public async Task An_edit_that_leaves_the_plate_as_it_is_is_not_a_conflict_with_itself()
    {
        // The other side of the same check: re-submitting an unchanged form is the ordinary
        // case, and a vehicle must not collide with its own plate.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var token = await FleetApi.TokenAsync(factory, client, UserRole.Dispatcher, cancellationToken);
        var plate = FleetApi.UniquePlate();

        int id;

        using (var created = await CreateVehicleAsync(client, token, plate, cancellationToken))
        {
            id = (await FleetApi.DataAsync(created, cancellationToken)).GetProperty("id").GetInt32();
        }

        using var updated = await FleetApi.SendAsync(
            client,
            HttpMethod.Put,
            $"/api/vehicles/{id}",
            token,
            new { licensePlate = plate.ToLowerInvariant(), model = "Форд Транзіт" },
            cancellationToken);

        var data = await FleetApi.DataAsync(updated, cancellationToken);

        Assert.Equal(plate, data.GetProperty("licensePlate").GetString());
        Assert.Equal("Форд Транзіт", data.GetProperty("model").GetString());
    }

    [Fact]
    public async Task A_vehicle_a_driver_holds_is_refused_with_the_way_out_in_the_message()
    {
        // FR-43, and the defect it closes. The schema's SetNull would happily strand a driver
        // holding nothing, so the application refuses - but only because FR-38 now gives the caller
        // a way to release the assignment first.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var token = await FleetApi.TokenAsync(factory, client, UserRole.Dispatcher, cancellationToken);
        var vehicleId = await NewVehicleAsync(client, token, cancellationToken);

        await NewDriverAsync(client, token, vehicleId, cancellationToken);

        using var refused = await FleetApi.SendAsync(
            client, HttpMethod.Delete, $"/api/vehicles/{vehicleId}", token, body: null, cancellationToken);

        await FleetApi.AssertFailureAsync(
            refused, HttpStatusCode.Conflict, ErrorCode.FLEET_VEHICLE_IN_USE, cancellationToken);

        // The message is the catalogue's, asserted against the catalogue rather than pinned as a
        // sentence a translation pass would break.
        using (var scope = factory.Services.CreateScope())
        {
            var localizer = scope.ServiceProvider.GetRequiredService<IStringLocalizer<ErrorMessages>>();
            var expected = localizer[nameof(ErrorCode.FLEET_VEHICLE_IN_USE)].Value;

            Assert.False(string.IsNullOrWhiteSpace(expected));

            var message = (await FleetApi.ReadAsync(refused, cancellationToken))
                .GetProperty("error")
                .GetProperty("message")
                .GetString();

            Assert.Equal(expected, message);
        }

        await using var context = await factory.Database.ContextFactory.CreateDbContextAsync(cancellationToken);

        Assert.Equal(1, await context.Vehicles.CountAsync(row => row.Id == vehicleId, cancellationToken));
    }

    [Fact]
    public async Task A_vehicle_nobody_holds_is_deleted()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var token = await FleetApi.TokenAsync(factory, client, UserRole.Dispatcher, cancellationToken);
        var vehicleId = await NewVehicleAsync(client, token, cancellationToken);

        using (var deleted = await FleetApi.SendAsync(
                   client, HttpMethod.Delete, $"/api/vehicles/{vehicleId}", token, body: null, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);

            var envelope = await FleetApi.ReadAsync(deleted, cancellationToken);

            Assert.True(envelope.GetProperty("success").GetBoolean());
        }

        await using var context = await factory.Database.ContextFactory.CreateDbContextAsync(cancellationToken);

        Assert.Equal(0, await context.Vehicles.CountAsync(row => row.Id == vehicleId, cancellationToken));
    }

    [Fact]
    public async Task An_update_changes_only_the_fields_it_names()
    {
        // AD-23 over the wire: absent means unchanged. A payload naming only the capacity must not
        // be judged as though the model were empty.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var token = await FleetApi.TokenAsync(factory, client, UserRole.Dispatcher, cancellationToken);
        var plate = FleetApi.UniquePlate();

        int id;

        using (var created = await CreateVehicleAsync(client, token, plate, cancellationToken))
        {
            id = (await FleetApi.DataAsync(created, cancellationToken)).GetProperty("id").GetInt32();
        }

        using var updated = await FleetApi.SendAsync(
            client, HttpMethod.Put, $"/api/vehicles/{id}", token, new { capacityKg = 2500m }, cancellationToken);

        var data = await FleetApi.DataAsync(updated, cancellationToken);

        Assert.Equal(2500m, data.GetProperty("capacityKg").GetDecimal());
        Assert.Equal(plate, data.GetProperty("licensePlate").GetString());
        Assert.Equal("Рено Мастер", data.GetProperty("model").GetString());
        Assert.Equal(42_000, data.GetProperty("mileage").GetInt32());
        Assert.Equal("2026-12-01", data.GetProperty("nextMaintenanceDate").GetString());
    }

    [Fact]
    public async Task An_odometer_that_runs_backwards_is_refused_naming_the_field()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var token = await FleetApi.TokenAsync(factory, client, UserRole.Dispatcher, cancellationToken);

        using (var refused = await FleetApi.SendAsync(
                   client,
                   HttpMethod.Post,
                   "/api/vehicles",
                   token,
                   new
                   {
                       model = "Рено Мастер",
                       licensePlate = FleetApi.UniquePlate(),
                       capacityKg = 1200m,
                       mileage = -1,
                       nextMaintenanceDate = (string?)null,
                   },
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);

            var error = (await FleetApi.ReadAsync(refused, cancellationToken)).GetProperty("error");

            Assert.Equal(nameof(ErrorCode.COMMON_VALIDATION_FAILED), error.GetProperty("code").GetString());
            Assert.True(error.GetProperty("fields").TryGetProperty("mileage", out _));
        }

        // And the same rule on the way through an update, where it is the merged state that is
        // judged rather than the payload.
        var id = await NewVehicleAsync(client, token, cancellationToken);

        using var edited = await FleetApi.SendAsync(
            client, HttpMethod.Put, $"/api/vehicles/{id}", token, new { mileage = -5 }, cancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, edited.StatusCode);

        var editError = (await FleetApi.ReadAsync(edited, cancellationToken)).GetProperty("error");

        // NFR-4: keyed by the name the caller sent, not by the CLR path through the wrapper.
        Assert.True(editError.GetProperty("fields").TryGetProperty("mileage", out _));
    }

    [Fact]
    public async Task A_null_for_a_field_that_cannot_hold_one_is_refused_and_the_row_is_untouched()
    {
        // The mirror of the test below, and the reason the converter refuses rather than folding a
        // null into default(T): mileage is not nullable on the row, so reading `null` as zero would
        // reset an odometer on a malformed payload. The unchanged row is the claim - a 422 with the
        // write already done would be worse than either.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var token = await FleetApi.TokenAsync(factory, client, UserRole.Dispatcher, cancellationToken);
        var id = await NewVehicleAsync(client, token, cancellationToken);

        using var refused = await FleetApi.SendAsync(
            client,
            HttpMethod.Put,
            $"/api/vehicles/{id}",
            token,
            new { mileage = (int?)null },
            cancellationToken);

        // The refusal is raised inside model binding, before an action or a validator runs, and it
        // is a DriveTrackException rather than a JsonException - so it travels past the input
        // formatter's own catch to ApiEnvelopeMiddleware and leaves as the ordinary 422 envelope.
        await FleetApi.AssertFailureAsync(
            refused,
            HttpStatusCode.UnprocessableEntity,
            ErrorCode.COMMON_VALIDATION_FAILED,
            cancellationToken);

        // And no field list at all. The converter cannot know which property the null belonged to,
        // so it names none - NFR-4's `error.fields` keys are ones a form can attach a message to,
        // and a key invented from the CLR type would name an input the caller does not have.
        // Pinned so the choice stays deliberate rather than becoming an accident of the writer.
        var error = (await FleetApi.ReadAsync(refused, cancellationToken)).GetProperty("error");

        Assert.False(
            error.TryGetProperty("fields", out _),
            "The refusal carried a field list naming a property the caller never sent.");

        await using var context = await factory.Database.ContextFactory.CreateDbContextAsync(cancellationToken);

        Assert.Equal(
            1,
            await context.Vehicles.CountAsync(
                row => row.Id == id && row.Mileage == 42_000,
                cancellationToken));
    }

    [Fact]
    public async Task An_explicit_null_clears_the_maintenance_date_and_leaves_the_mileage_alone()
    {
        // The one vehicle field where a present-null is a genuine clear: next_maintenance_date is
        // nullable on the row, so "no servicing scheduled" is a state a dispatcher can choose. The
        // mileage in the same payload is absent, so it must survive untouched.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var token = await FleetApi.TokenAsync(factory, client, UserRole.Dispatcher, cancellationToken);
        var id = await NewVehicleAsync(client, token, cancellationToken);

        using var cleared = await FleetApi.SendAsync(
            client,
            HttpMethod.Put,
            $"/api/vehicles/{id}",
            token,
            new { nextMaintenanceDate = (string?)null },
            cancellationToken);

        var data = await FleetApi.DataAsync(cleared, cancellationToken);

        Assert.Equal(JsonValueKind.Null, data.GetProperty("nextMaintenanceDate").ValueKind);
        Assert.Equal(42_000, data.GetProperty("mileage").GetInt32());

        await using var context = await factory.Database.ContextFactory.CreateDbContextAsync(cancellationToken);

        Assert.Equal(
            1,
            await context.Vehicles.CountAsync(
                row => row.Id == id && row.NextMaintenanceDate == null && row.Mileage == 42_000,
                cancellationToken));
    }

    [Fact]
    public async Task An_update_that_empties_a_required_field_is_refused_naming_it()
    {
        // The other side of the merge: a field the caller did send is judged, and a present null on
        // a column that cannot be null is a 422 rather than a clear.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var token = await FleetApi.TokenAsync(factory, client, UserRole.Dispatcher, cancellationToken);
        var id = await NewVehicleAsync(client, token, cancellationToken);

        using var refused = await FleetApi.SendAsync(
            client,
            HttpMethod.Put,
            $"/api/vehicles/{id}",
            token,
            new { model = (string?)null },
            cancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);

        var error = (await FleetApi.ReadAsync(refused, cancellationToken)).GetProperty("error");

        Assert.Equal(nameof(ErrorCode.COMMON_VALIDATION_FAILED), error.GetProperty("code").GetString());

        // NFR-4: keyed by the name the caller sent, not by the CLR path through the wrapper.
        Assert.True(error.GetProperty("fields").TryGetProperty("model", out _));
    }

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(0, 0)]
    [InlineData(0, 101)]
    public async Task Paging_outside_its_range_is_refused_before_any_query(int offset, int limit)
    {
        // NFR-27's second half. The original passed skip and limit through unchecked.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var token = await FleetApi.TokenAsync(factory, client, UserRole.Dispatcher, cancellationToken);

        using var refused = await FleetApi.SendAsync(
            client,
            HttpMethod.Get,
            $"/api/vehicles?offset={offset}&limit={limit}",
            token,
            body: null,
            cancellationToken);

        await FleetApi.AssertFailureAsync(
            refused,
            HttpStatusCode.UnprocessableEntity,
            ErrorCode.COMMON_VALIDATION_FAILED,
            cancellationToken);
    }

    [Fact]
    public async Task The_list_pages_and_defaults_to_the_first_hundred()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var token = await FleetApi.TokenAsync(factory, client, UserRole.Dispatcher, cancellationToken);

        for (var index = 0; index < 3; index++)
        {
            await NewVehicleAsync(client, token, cancellationToken);
        }

        int[] all;

        using (var listed = await FleetApi.SendAsync(
                   client, HttpMethod.Get, "/api/vehicles", token, body: null, cancellationToken))
        {
            all = [.. (await FleetApi.DataAsync(listed, cancellationToken))
                .EnumerateArray()
                .Select(row => row.GetProperty("id").GetInt32())];
        }

        Assert.Equal(3, all.Length);

        // The ids, in order, one page at a time. Counting rows alone would stay green with the
        // offset or the ordering dropped from the query, while every page served the same rows.
        for (var offset = 0; offset < all.Length; offset++)
        {
            using var page = await FleetApi.SendAsync(
                client,
                HttpMethod.Get,
                $"/api/vehicles?offset={offset}&limit=1",
                token,
                body: null,
                cancellationToken);

            var rows = (await FleetApi.DataAsync(page, cancellationToken))
                .EnumerateArray()
                .Select(row => row.GetProperty("id").GetInt32())
                .ToArray();

            Assert.Equal(new[] { all[offset] }, rows);
        }
    }

    [Fact]
    public async Task The_unassigned_list_excludes_a_vehicle_a_driver_holds()
    {
        // FR-45: the list that feeds the assignment control. A held vehicle offered there is a
        // choice that can only be refused.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var token = await FleetApi.TokenAsync(factory, client, UserRole.Dispatcher, cancellationToken);
        var held = await NewVehicleAsync(client, token, cancellationToken);
        var free = await NewVehicleAsync(client, token, cancellationToken);

        await NewDriverAsync(client, token, held, cancellationToken);

        var ids = await UnassignedIdsAsync(client, token, cancellationToken);

        Assert.Contains(free, ids);
        Assert.DoesNotContain(held, ids);
    }

    [Fact]
    public async Task An_id_with_no_row_is_not_found()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var token = await FleetApi.TokenAsync(factory, client, UserRole.Dispatcher, cancellationToken);

        foreach (var (method, body) in new (HttpMethod Method, object? Body)[]
                 {
                     (HttpMethod.Get, null),
                     (HttpMethod.Put, new { capacityKg = 10m }),
                     (HttpMethod.Delete, null),
                 })
        {
            using var response = await FleetApi.SendAsync(
                client, method, "/api/vehicles/999999", token, body, cancellationToken);

            await FleetApi.AssertFailureAsync(
                response, HttpStatusCode.NotFound, ErrorCode.COMMON_NOT_FOUND, cancellationToken);
        }
    }

    [Theory]
    [InlineData(UserRole.Client)]
    [InlineData(UserRole.Driver)]
    public async Task A_signed_in_caller_of_the_wrong_role_is_refused_by_the_guard(UserRole role)
    {
        // FR-12: the controller carries a bare [Authorize], so this 403 is IAccessGuard's answer
        // and not an attribute's. Removing the attribute would change the anonymous case below and
        // nothing about this one.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await FleetApi.TokenAsync(factory, client, UserRole.Dispatcher, cancellationToken);
        var token = role == UserRole.Driver
            ? await DriverTokenAsync(client, dispatcher, cancellationToken)
            : await FleetApi.TokenAsync(factory, client, role, cancellationToken);

        foreach (var path in new[] { "/api/vehicles", "/api/vehicles/unassigned" })
        {
            using var response = await FleetApi.SendAsync(
                client, HttpMethod.Get, path, token, body: null, cancellationToken);

            await FleetApi.AssertFailureAsync(
                response, HttpStatusCode.Forbidden, ErrorCode.AUTH_FORBIDDEN, cancellationToken);
        }

        using var created = await CreateVehicleAsync(client, token, FleetApi.UniquePlate(), cancellationToken);

        await FleetApi.AssertFailureAsync(
            created, HttpStatusCode.Forbidden, ErrorCode.AUTH_FORBIDDEN, cancellationToken);
    }

    [Fact]
    public async Task An_anonymous_caller_is_unauthenticated()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        using var response = await FleetApi.SendAsync(
            client, HttpMethod.Get, "/api/vehicles", token: null, body: null, cancellationToken);

        await FleetApi.AssertFailureAsync(
            response, HttpStatusCode.Unauthorized, ErrorCode.AUTH_UNAUTHENTICATED, cancellationToken);
    }

    [Fact]
    public async Task An_admin_gets_the_same_success_a_dispatcher_does()
    {
        // AD-4's override end to end: the admin holds one role row like everybody else and passes
        // by rule inside the guard.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var admin = await FleetApi.TokenAsync(factory, client, UserRole.Admin, cancellationToken);

        using var created = await CreateVehicleAsync(client, admin, FleetApi.UniquePlate(), cancellationToken);

        Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        using var listed = await FleetApi.SendAsync(
            client, HttpMethod.Get, "/api/vehicles", admin, body: null, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
    }

    // -------------------------------------------------------------------------------------
    // Shared arrangement
    // -------------------------------------------------------------------------------------

    private static Task<HttpResponseMessage> CreateVehicleAsync(
        HttpClient client,
        string token,
        string licensePlate,
        CancellationToken cancellationToken) =>
        FleetApi.SendAsync(
            client,
            HttpMethod.Post,
            "/api/vehicles",
            token,
            new
            {
                model = "Рено Мастер",
                licensePlate,
                capacityKg = 1200m,
                mileage = 42_000,
                nextMaintenanceDate = "2026-12-01",
            },
            cancellationToken);

    private static async Task<int> NewVehicleAsync(
        HttpClient client,
        string token,
        CancellationToken cancellationToken)
    {
        using var response = await CreateVehicleAsync(
            client, token, FleetApi.UniquePlate(), cancellationToken);

        return (await FleetApi.DataAsync(response, cancellationToken)).GetProperty("id").GetInt32();
    }

    private static async Task<int> NewDriverAsync(
        HttpClient client,
        string token,
        int? vehicleId,
        CancellationToken cancellationToken)
    {
        using var response = await FleetApi.SendAsync(
            client,
            HttpMethod.Post,
            "/api/drivers",
            token,
            new
            {
                firstName = "Тарас",
                lastName = "Шевченко",
                email = FleetApi.UniqueEmail(),
                password = FleetApi.Password,
                licenseNumber = Guid.NewGuid().ToString("N")[..10],
                vehicleId,
            },
            cancellationToken);

        return (await FleetApi.DataAsync(response, cancellationToken)).GetProperty("id").GetInt32();
    }

    /// <summary>A bearer token for a driver the fleet capability itself created.</summary>
    private static async Task<string> DriverTokenAsync(
        HttpClient client,
        string dispatcherToken,
        CancellationToken cancellationToken)
    {
        var email = FleetApi.UniqueEmail();

        using (var created = await FleetApi.SendAsync(
                   client,
                   HttpMethod.Post,
                   "/api/drivers",
                   dispatcherToken,
                   new
                   {
                       firstName = "Тарас",
                       lastName = "Шевченко",
                       email,
                       password = FleetApi.Password,
                       licenseNumber = Guid.NewGuid().ToString("N")[..10],
                       vehicleId = (int?)null,
                   },
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        }

        return await FleetApi.SignInAsync(client, email, FleetApi.Password, cancellationToken);
    }

    private static async Task<int[]> UnassignedIdsAsync(
        HttpClient client,
        string token,
        CancellationToken cancellationToken)
    {
        using var response = await FleetApi.SendAsync(
            client, HttpMethod.Get, "/api/vehicles/unassigned", token, body: null, cancellationToken);

        var rows = await FleetApi.DataAsync(response, cancellationToken);

        return [.. rows.EnumerateArray().Select(row => row.GetProperty("id").GetInt32())];
    }
}
