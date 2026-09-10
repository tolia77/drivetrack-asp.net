using System.Net;
using System.Text.Json;
using DriveTrack.Application.Common;
using DriveTrack.Domain.Identity;
using DriveTrack.Integration.Tests.Persistence;
using DriveTrack.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace DriveTrack.Integration.Tests.Fleet;

/// <summary>
/// FR-35 to FR-39 over HTTP, against the real <c>Program.cs</c> pipeline.
/// <para>
/// Two rows here are the original system's structural defects. FR-38's release — an update whose
/// <c>vehicleId</c> is present and null — is the arm the original could not express, and FR-39's
/// delete is the one that surfaced a raw database error. Both are asserted end to end, including
/// what the database holds afterwards.
/// </para>
/// </summary>
public class DriverManagementTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Taking_on_a_driver_commits_the_account_the_row_and_the_assignment_together()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var token = await FleetApi.TokenAsync(factory, client, UserRole.Dispatcher, cancellationToken);
        var vehicleId = await NewVehicleAsync(client, token, cancellationToken);
        var email = FleetApi.UniqueEmail();

        int driverId;
        int userId;

        using (var created = await CreateDriverAsync(client, token, email, vehicleId, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);

            var data = await FleetApi.DataAsync(created, cancellationToken);

            driverId = data.GetProperty("id").GetInt32();
            userId = data.GetProperty("userId").GetInt32();

            // AD-22: the typed ids are a compile-time device, not a wire shape - and they are
            // different numbers, which is what makes the separation load-bearing.
            Assert.Equal(vehicleId, data.GetProperty("vehicleId").GetInt32());
            Assert.Equal(email, data.GetProperty("email").GetString());
        }

        await using var context = await factory.Database.ContextFactory.CreateDbContextAsync(cancellationToken);

        Assert.Equal(1, await context.Drivers.CountAsync(row => row.Id == new DriverId(driverId), cancellationToken));
        Assert.Equal(1, await context.Users.CountAsync(row => row.Id == userId, cancellationToken));
        Assert.Equal(
            1,
            await context.Drivers.CountAsync(
                row => row.Id == new DriverId(driverId) && row.VehicleId == vehicleId,
                cancellationToken));

        // The role row, so the new account can sign in as a driver rather than as nothing.
        var signedIn = await FleetApi.SignInAsync(client, email, FleetApi.Password, cancellationToken);

        Assert.False(string.IsNullOrWhiteSpace(signedIn));
    }

    [Fact]
    public async Task A_second_driver_naming_the_same_vehicle_is_refused_and_leaves_no_orphan_account()
    {
        // FR-44, and AD-5's reason for existing: the account is created before the assignment is
        // judged, so a refusal that did not roll the whole operation back would leave an account
        // holding role Driver with no driver row behind it.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var token = await FleetApi.TokenAsync(factory, client, UserRole.Dispatcher, cancellationToken);
        var vehicleId = await NewVehicleAsync(client, token, cancellationToken);

        using (var first = await CreateDriverAsync(
                   client, token, FleetApi.UniqueEmail(), vehicleId, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        var refusedEmail = FleetApi.UniqueEmail();

        using (var second = await CreateDriverAsync(
                   client, token, refusedEmail, vehicleId, cancellationToken))
        {
            await FleetApi.AssertFailureAsync(
                second,
                HttpStatusCode.Conflict,
                ErrorCode.FLEET_VEHICLE_ALREADY_ASSIGNED,
                cancellationToken);
        }

        await using var context = await factory.Database.ContextFactory.CreateDbContextAsync(cancellationToken);

        var normalized = refusedEmail.ToUpperInvariant();

        Assert.Equal(
            0,
            await context.Users.CountAsync(row => row.NormalizedEmail == normalized, cancellationToken));
        Assert.Equal(
            1,
            await context.Drivers.CountAsync(row => row.VehicleId == vehicleId, cancellationToken));
    }

    [Fact]
    public async Task A_driver_may_hold_no_vehicle()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var token = await FleetApi.TokenAsync(factory, client, UserRole.Dispatcher, cancellationToken);

        using var created = await CreateDriverAsync(
            client, token, FleetApi.UniqueEmail(), vehicleId: null, cancellationToken);

        var data = await FleetApi.DataAsync(created, cancellationToken);

        Assert.Equal(JsonValueKind.Null, data.GetProperty("vehicleId").ValueKind);
        Assert.Equal(JsonValueKind.Null, data.GetProperty("vehicleModel").ValueKind);
    }

    [Fact]
    public async Task An_email_another_account_holds_is_refused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var token = await FleetApi.TokenAsync(factory, client, UserRole.Dispatcher, cancellationToken);
        var email = FleetApi.UniqueEmail();

        using (var first = await CreateDriverAsync(client, token, email, vehicleId: null, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        using var second = await CreateDriverAsync(client, token, email, vehicleId: null, cancellationToken);

        await FleetApi.AssertFailureAsync(
            second, HttpStatusCode.Conflict, ErrorCode.AUTH_EMAIL_ALREADY_IN_USE, cancellationToken);
    }

    [Fact]
    public async Task A_password_below_the_policy_is_refused_naming_the_field()
    {
        // Identity's own policy, reported back through the contract's code: the validator only
        // proves the password is present, so this row is the one that proves the two agree (NFR-2).
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var token = await FleetApi.TokenAsync(factory, client, UserRole.Dispatcher, cancellationToken);

        using var refused = await FleetApi.SendAsync(
            client,
            HttpMethod.Post,
            "/api/drivers",
            token,
            new
            {
                firstName = "Тарас",
                lastName = "Шевченко",
                email = FleetApi.UniqueEmail(),
                password = "abc",
                licenseNumber = "AA123456",
                vehicleId = (int?)null,
            },
            cancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);

        var error = (await FleetApi.ReadAsync(refused, cancellationToken)).GetProperty("error");

        Assert.Equal(nameof(ErrorCode.COMMON_VALIDATION_FAILED), error.GetProperty("code").GetString());
        Assert.True(error.GetProperty("fields").TryGetProperty("password", out _));
    }

    [Fact]
    public async Task An_explicit_null_vehicle_releases_the_assignment_and_the_vehicle_becomes_deletable()
    {
        // FR-38, and the whole reason Optional<T> exists. The original read a plain vehicle_id off
        // the payload, so this request was indistinguishable from one that mentioned no vehicle at
        // all - which is why an assigned vehicle there could never be released or deleted.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var token = await FleetApi.TokenAsync(factory, client, UserRole.Dispatcher, cancellationToken);
        var vehicleId = await NewVehicleAsync(client, token, cancellationToken);
        var driverId = await NewDriverAsync(client, token, vehicleId, cancellationToken);

        using (var released = await FleetApi.SendAsync(
                   client,
                   HttpMethod.Put,
                   $"/api/drivers/{driverId}",
                   token,
                   new { vehicleId = (int?)null },
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, released.StatusCode);

            var data = await FleetApi.DataAsync(released, cancellationToken);

            Assert.Equal(JsonValueKind.Null, data.GetProperty("vehicleId").ValueKind);
        }

        await using (var context = await factory.Database.ContextFactory
                         .CreateDbContextAsync(cancellationToken))
        {
            Assert.Equal(
                1,
                await context.Drivers.CountAsync(
                    row => row.Id == new DriverId(driverId) && row.VehicleId == null,
                    cancellationToken));
        }

        // It reappears in the list that feeds the assignment control (FR-45)...
        using (var unassigned = await FleetApi.SendAsync(
                   client, HttpMethod.Get, "/api/vehicles/unassigned", token, body: null, cancellationToken))
        {
            var ids = (await FleetApi.DataAsync(unassigned, cancellationToken))
                .EnumerateArray()
                .Select(row => row.GetProperty("id").GetInt32())
                .ToArray();

            Assert.Contains(vehicleId, ids);
        }

        // ...and deleting it now succeeds, which is the end of the defect (FR-43).
        using var deleted = await FleetApi.SendAsync(
            client, HttpMethod.Delete, $"/api/vehicles/{vehicleId}", token, body: null, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
    }

    [Fact]
    public async Task Renaming_a_driver_changes_the_account_and_leaves_the_licence_and_assignment_alone()
    {
        // FR-37 names three things a dispatcher may change, and the name is the one that lives on
        // the account rather than on the driver row - so this is also the row that proves the
        // narrow rename reaches Identity without disturbing anything else about the account.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var token = await FleetApi.TokenAsync(factory, client, UserRole.Dispatcher, cancellationToken);
        var vehicleId = await NewVehicleAsync(client, token, cancellationToken);
        var email = FleetApi.UniqueEmail();

        int driverId;
        int userId;
        string licenseNumber;

        using (var created = await CreateDriverAsync(client, token, email, vehicleId, cancellationToken))
        {
            var data = await FleetApi.DataAsync(created, cancellationToken);

            driverId = data.GetProperty("id").GetInt32();
            userId = data.GetProperty("userId").GetInt32();
            licenseNumber = data.GetProperty("licenseNumber").GetString()!;
        }

        using (var renamed = await FleetApi.SendAsync(
                   client,
                   HttpMethod.Put,
                   $"/api/drivers/{driverId}",
                   token,
                   new { firstName = "Леся", lastName = "Українка" },
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);

            var data = await FleetApi.DataAsync(renamed, cancellationToken);

            Assert.Equal("Леся", data.GetProperty("firstName").GetString());
            Assert.Equal("Українка", data.GetProperty("lastName").GetString());

            // The two fields the payload never mentioned.
            Assert.Equal(licenseNumber, data.GetProperty("licenseNumber").GetString());
            Assert.Equal(vehicleId, data.GetProperty("vehicleId").GetInt32());
        }

        await using var context = await factory.Database.ContextFactory.CreateDbContextAsync(cancellationToken);

        // On the account row, and with the address it signs in with untouched: the rename is two
        // columns, not an "update the user".
        Assert.Equal(
            1,
            await context.Users.CountAsync(
                row => row.Id == userId
                    && row.FirstName == "Леся"
                    && row.LastName == "Українка"
                    && row.Email == email,
                cancellationToken));

        // The credentials survive it, which is the part a UserManager.UpdateAsync would have
        // rolled: the driver can still sign in.
        var signedIn = await FleetApi.SignInAsync(client, email, FleetApi.Password, cancellationToken);

        Assert.False(string.IsNullOrWhiteSpace(signedIn));
    }

    [Fact]
    public async Task A_token_issued_before_a_rename_still_authenticates_afterwards()
    {
    // The one property RenameAsync was minted for. It assigns the tracked entity rather than
    // calling UserManager.UpdateAsync, which rolls the security stamp - and a rolled stamp
    // invalidates every token already issued for that account. Nothing else in the suite would
    // notice a refactor back to UpdateAsync: the rename would still land, and only a driver
    // signed in at the time would find themselves logged out.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var dispatcher = await FleetApi.TokenAsync(factory, client, UserRole.Dispatcher, cancellationToken);
        var email = FleetApi.UniqueEmail();

        int driverId;
        int userId;

        using (var created = await CreateDriverAsync(
                   client, dispatcher, email, vehicleId: null, cancellationToken))
        {
            var data = await FleetApi.DataAsync(created, cancellationToken);

            driverId = data.GetProperty("id").GetInt32();
            userId = data.GetProperty("userId").GetInt32();
        }

        // The driver's own bearer token, issued before the rename.
        var driverToken = await FleetApi.SignInAsync(client, email, FleetApi.Password, cancellationToken);

        using (var renamed = await FleetApi.SendAsync(
                   client,
                   HttpMethod.Put,
                   $"/api/drivers/{driverId}",
                   dispatcher,
                   new { firstName = "Леся", lastName = "Українка" },
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        }

        // Still their own profile, with the token from before: the credentials were untouched.
        using var profile = await FleetApi.SendAsync(
            client,
            HttpMethod.Get,
            $"/api/users/{userId}",
            driverToken,
            body: null,
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, profile.StatusCode);

        var data2 = await FleetApi.DataAsync(profile, cancellationToken);

        Assert.Equal("Леся", data2.GetProperty("firstName").GetString());
    }

    [Fact]
    public async Task Getting_one_driver_names_the_vehicle_they_hold()
    {
        // The only other call to this endpoint uses a missing id and asserts 404, so without
        // this the Include on the repository read could be deleted and the endpoint would stop
        // naming the assignment with the whole suite still green.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var token = await FleetApi.TokenAsync(factory, client, UserRole.Dispatcher, cancellationToken);
        var vehicleId = await NewVehicleAsync(client, token, cancellationToken);
        var driverId = await NewDriverAsync(client, token, vehicleId, cancellationToken);

        using var fetched = await FleetApi.SendAsync(
            client, HttpMethod.Get, $"/api/drivers/{driverId}", token, body: null, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);

        var data = await FleetApi.DataAsync(fetched, cancellationToken);

        Assert.Equal(driverId, data.GetProperty("id").GetInt32());
        Assert.Equal(vehicleId, data.GetProperty("vehicleId").GetInt32());
        Assert.Equal("Рено Мастер", data.GetProperty("vehicleModel").GetString());
        Assert.False(
            string.IsNullOrWhiteSpace(data.GetProperty("vehicleLicensePlate").GetString()),
            "The driver was fetched without the plate of the vehicle they hold.");
    }

    [Fact]
    public async Task An_update_naming_only_the_licence_number_leaves_both_names_alone()
    {
        // AD-23 again, now over a field that lives on the other row: absent is not "clear the
        // name", and a merge that read the payload would empty it.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var token = await FleetApi.TokenAsync(factory, client, UserRole.Dispatcher, cancellationToken);
        var driverId = await NewDriverAsync(client, token, vehicleId: null, cancellationToken);

        using var updated = await FleetApi.SendAsync(
            client,
            HttpMethod.Put,
            $"/api/drivers/{driverId}",
            token,
            new { licenseNumber = "ВІ909090" },
            cancellationToken);

        var data = await FleetApi.DataAsync(updated, cancellationToken);

        Assert.Equal("ВІ909090", data.GetProperty("licenseNumber").GetString());
        Assert.Equal("Тарас", data.GetProperty("firstName").GetString());
        Assert.Equal("Шевченко", data.GetProperty("lastName").GetString());
    }

    [Fact]
    public async Task A_blank_name_is_refused_naming_the_field()
    {
        // Present and empty, which is a value the caller sent rather than a field they omitted -
        // the distinction the merge exists to keep, asserted from the refusing side.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var token = await FleetApi.TokenAsync(factory, client, UserRole.Dispatcher, cancellationToken);
        var driverId = await NewDriverAsync(client, token, vehicleId: null, cancellationToken);

        using var refused = await FleetApi.SendAsync(
            client,
            HttpMethod.Put,
            $"/api/drivers/{driverId}",
            token,
            new { firstName = string.Empty },
            cancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);

        var error = (await FleetApi.ReadAsync(refused, cancellationToken)).GetProperty("error");

        Assert.Equal(nameof(ErrorCode.COMMON_VALIDATION_FAILED), error.GetProperty("code").GetString());

        // NFR-4: `firstName`, not `firstName.value` - a form can only attach a message to an input
        // it actually has.
        Assert.True(error.GetProperty("fields").TryGetProperty("firstName", out _));

        // And nothing was written: the name the driver was created with survives.
        await using var context = await factory.Database.ContextFactory.CreateDbContextAsync(cancellationToken);

        Assert.Equal(
            0,
            await context.Users.CountAsync(row => row.FirstName == string.Empty, cancellationToken));
    }

    [Fact]
    public async Task An_update_that_omits_the_vehicle_leaves_the_assignment_alone()
    {
        // The other half of AD-23's distinction. Absent is not null: an edit to the licence number
        // must not silently release the vehicle.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var token = await FleetApi.TokenAsync(factory, client, UserRole.Dispatcher, cancellationToken);
        var vehicleId = await NewVehicleAsync(client, token, cancellationToken);
        var driverId = await NewDriverAsync(client, token, vehicleId, cancellationToken);

        using var updated = await FleetApi.SendAsync(
            client,
            HttpMethod.Put,
            $"/api/drivers/{driverId}",
            token,
            new { licenseNumber = "ВІ777777" },
            cancellationToken);

        var data = await FleetApi.DataAsync(updated, cancellationToken);

        Assert.Equal("ВІ777777", data.GetProperty("licenseNumber").GetString());
        Assert.Equal(vehicleId, data.GetProperty("vehicleId").GetInt32());
    }

    [Fact]
    public async Task Reassigning_the_vehicle_a_driver_already_holds_is_a_no_op()
    {
        // Re-submitting an unchanged form is the ordinary case. Treating it as a conflict with
        // itself would make the edit screen unusable.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var token = await FleetApi.TokenAsync(factory, client, UserRole.Dispatcher, cancellationToken);
        var vehicleId = await NewVehicleAsync(client, token, cancellationToken);
        var driverId = await NewDriverAsync(client, token, vehicleId, cancellationToken);

        using var updated = await FleetApi.SendAsync(
            client,
            HttpMethod.Put,
            $"/api/drivers/{driverId}",
            token,
            new { vehicleId },
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);

        var data = await FleetApi.DataAsync(updated, cancellationToken);

        Assert.Equal(vehicleId, data.GetProperty("vehicleId").GetInt32());
    }

    [Fact]
    public async Task Assigning_a_vehicle_that_does_not_exist_is_not_found()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var token = await FleetApi.TokenAsync(factory, client, UserRole.Dispatcher, cancellationToken);
        var driverId = await NewDriverAsync(client, token, vehicleId: null, cancellationToken);

        using var refused = await FleetApi.SendAsync(
            client,
            HttpMethod.Put,
            $"/api/drivers/{driverId}",
            token,
            new { vehicleId = 999999 },
            cancellationToken);

        await FleetApi.AssertFailureAsync(
            refused, HttpStatusCode.NotFound, ErrorCode.COMMON_NOT_FOUND, cancellationToken);
    }

    [Fact]
    public async Task Deleting_a_driver_with_an_active_delivery_succeeds_and_leaves_it_unassigned()
    {
        // FR-39's "leaves them unassigned" arm, and the request that used to answer a raw database
        // error. The cascade itself is DeleteBehaviourTests' claim; what is asserted here is that
        // the endpoint answers a success envelope rather than a 500, and that the delivery survives.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var token = await FleetApi.TokenAsync(factory, client, UserRole.Dispatcher, cancellationToken);
        var vehicleId = await NewVehicleAsync(client, token, cancellationToken);

        int driverId;
        int userId;

        using (var created = await CreateDriverAsync(
                   client, token, FleetApi.UniqueEmail(), vehicleId, cancellationToken))
        {
            var data = await FleetApi.DataAsync(created, cancellationToken);

            driverId = data.GetProperty("id").GetInt32();
            userId = data.GetProperty("userId").GetInt32();
        }

        int deliveryId;

        await using (var context = await factory.Database.ContextFactory
                         .CreateDbContextAsync(cancellationToken))
        {
            var delivery = await Seed.DeliveryAsync(
                context, cancellationToken, clientId: null, driverId: new DriverId(driverId));

            deliveryId = delivery.Id;
        }

        using (var deleted = await FleetApi.SendAsync(
                   client, HttpMethod.Delete, $"/api/drivers/{driverId}", token, body: null, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);

            Assert.True((await FleetApi.ReadAsync(deleted, cancellationToken))
                .GetProperty("success")
                .GetBoolean());
        }

        await using var after = await factory.Database.ContextFactory.CreateDbContextAsync(cancellationToken);

        // The account goes with the driver row: an account holding role Driver with nothing behind
        // it is signable-in and carries a null driver claim.
        Assert.Equal(0, await after.Drivers.CountAsync(row => row.Id == new DriverId(driverId), cancellationToken));
        Assert.Equal(0, await after.Users.CountAsync(row => row.Id == userId, cancellationToken));

        // The delivery survives, unassigned. FR-39 takes that arm rather than refusing the delete.
        Assert.Equal(
            1,
            await after.Deliveries.CountAsync(
                row => row.Id == deliveryId && row.DriverId == null,
                cancellationToken));

        // And the vehicle they held is free again rather than deleted with them.
        Assert.Equal(1, await after.Vehicles.CountAsync(row => row.Id == vehicleId, cancellationToken));
    }

    [Fact]
    public async Task The_list_names_the_vehicle_each_driver_holds()
    {
        // FR-35: the roster is what the screen's browser-side search runs over, so the vehicle has
        // to travel with the driver rather than being fetched per row by the client.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var token = await FleetApi.TokenAsync(factory, client, UserRole.Dispatcher, cancellationToken);
        var vehicleId = await NewVehicleAsync(client, token, cancellationToken);

        await NewDriverAsync(client, token, vehicleId, cancellationToken);
        await NewDriverAsync(client, token, vehicleId: null, cancellationToken);

        using var listed = await FleetApi.SendAsync(
            client, HttpMethod.Get, "/api/drivers", token, body: null, cancellationToken);

        var rows = (await FleetApi.DataAsync(listed, cancellationToken)).EnumerateArray().ToArray();

        Assert.Equal(2, rows.Length);
        Assert.Contains(rows, row => row.GetProperty("vehicleModel").GetString() == "Рено Мастер");
        Assert.Contains(rows, row => row.GetProperty("vehicleId").ValueKind == JsonValueKind.Null);

        // AD-17: only DTOs leave the Application layer, so no EF navigation is serialized back.
        foreach (var row in rows)
        {
            Assert.False(row.TryGetProperty("vehicle", out _));
            Assert.False(row.TryGetProperty("shifts", out _));
            Assert.False(row.TryGetProperty("deliveries", out _));
        }
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
                     (HttpMethod.Put, new { licenseNumber = "ВІ111111" }),
                     (HttpMethod.Delete, null),
                 })
        {
            using var response = await FleetApi.SendAsync(
                client, method, "/api/drivers/999999", token, body, cancellationToken);

            await FleetApi.AssertFailureAsync(
                response, HttpStatusCode.NotFound, ErrorCode.COMMON_NOT_FOUND, cancellationToken);
        }
    }

    [Fact]
    public async Task A_signed_in_client_is_refused_by_the_guard_and_an_anonymous_caller_by_the_scheme()
    {
        // The two refusals are different failures and carry different codes: the client presented
        // credentials the guard would not accept, the anonymous caller presented none.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var clientToken = await FleetApi.TokenAsync(factory, client, UserRole.Client, cancellationToken);

        using (var forbidden = await FleetApi.SendAsync(
                   client, HttpMethod.Get, "/api/drivers", clientToken, body: null, cancellationToken))
        {
            await FleetApi.AssertFailureAsync(
                forbidden, HttpStatusCode.Forbidden, ErrorCode.AUTH_FORBIDDEN, cancellationToken);
        }

        using var anonymous = await FleetApi.SendAsync(
            client, HttpMethod.Get, "/api/drivers", token: null, body: null, cancellationToken);

        await FleetApi.AssertFailureAsync(
            anonymous, HttpStatusCode.Unauthorized, ErrorCode.AUTH_UNAUTHENTICATED, cancellationToken);
    }

    [Fact]
    public async Task An_admin_gets_the_same_success_a_dispatcher_does()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await FleetApi.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        var admin = await FleetApi.TokenAsync(factory, client, UserRole.Admin, cancellationToken);

        using var created = await CreateDriverAsync(
            client, admin, FleetApi.UniqueEmail(), vehicleId: null, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
    }

    // -------------------------------------------------------------------------------------
    // Shared arrangement
    // -------------------------------------------------------------------------------------

    private static Task<HttpResponseMessage> CreateDriverAsync(
        HttpClient client,
        string token,
        string email,
        int? vehicleId,
        CancellationToken cancellationToken) =>
        FleetApi.SendAsync(
            client,
            HttpMethod.Post,
            "/api/drivers",
            token,
            new
            {
                firstName = "Тарас",
                lastName = "Шевченко",
                email,
                password = FleetApi.Password,
                licenseNumber = Guid.NewGuid().ToString("N")[..10],
                vehicleId,
            },
            cancellationToken);

    private static async Task<int> NewDriverAsync(
        HttpClient client,
        string token,
        int? vehicleId,
        CancellationToken cancellationToken)
    {
        using var response = await CreateDriverAsync(
            client, token, FleetApi.UniqueEmail(), vehicleId, cancellationToken);

        return (await FleetApi.DataAsync(response, cancellationToken)).GetProperty("id").GetInt32();
    }

    private static async Task<int> NewVehicleAsync(
        HttpClient client,
        string token,
        CancellationToken cancellationToken)
    {
        using var response = await FleetApi.SendAsync(
            client,
            HttpMethod.Post,
            "/api/vehicles",
            token,
            new
            {
                model = "Рено Мастер",
                licensePlate = FleetApi.UniquePlate(),
                capacityKg = 1200m,
                mileage = 42_000,
                nextMaintenanceDate = "2026-12-01",
            },
            cancellationToken);

        return (await FleetApi.DataAsync(response, cancellationToken)).GetProperty("id").GetInt32();
    }
}
