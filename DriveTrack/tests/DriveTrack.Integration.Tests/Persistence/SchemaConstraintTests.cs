using DriveTrack.Domain.Clients;
using DriveTrack.Domain.Deliveries;
using DriveTrack.Domain.Drivers;
using DriveTrack.Domain.Identity;
using DriveTrack.Domain.Reviews;
using DriveTrack.Domain.Shifts;
using DriveTrack.Integration.Tests.Support;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace DriveTrack.Integration.Tests.Persistence;

/// <summary>
/// AD-20's invariants, each proved by attempting the write that must be refused and reading the
/// SQLSTATE and constraint name the database answers with.
/// <para>
/// Every one of these could have been an <c>if</c> in a service, and in the original several
/// were — which is precisely why they are here. A check that runs before the insert is defeated
/// by a second request running the same check at the same moment; a constraint is not.
/// </para>
/// </summary>
[Collection(nameof(PostgresCollection))]
public class SchemaConstraintTests(PostgresFixture postgres)
{
    private const string UniqueViolation = "23505";
    private const string CheckViolation = "23514";

    [Fact]
    public async Task Second_review_for_one_delivery_is_refused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(postgres.ConnectionString, cancellationToken);

        await using var context = await database.CreateContextAsync(cancellationToken);

        var client = await Seed.ClientAsync(context, cancellationToken);
        var delivery = await Seed.DeliveryAsync(context, cancellationToken, client.Id);

        context.Reviews.Add(NewReview(delivery.Id, client.Id, rating: 5));
        await context.SaveChangesAsync(cancellationToken);

        await using var second = await database.CreateContextAsync(cancellationToken);
        second.Reviews.Add(NewReview(delivery.Id, client.Id, rating: 4));

        await PostgresFailure.RejectedByAsync(
            () => second.SaveChangesAsync(cancellationToken),
            UniqueViolation,
            "ix_reviews_delivery_id");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(-1)]
    public async Task Rating_outside_one_to_five_is_refused(int rating)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(postgres.ConnectionString, cancellationToken);

        await using var context = await database.CreateContextAsync(cancellationToken);

        var client = await Seed.ClientAsync(context, cancellationToken);
        var delivery = await Seed.DeliveryAsync(context, cancellationToken, client.Id);

        context.Reviews.Add(NewReview(delivery.Id, client.Id, rating));

        await PostgresFailure.RejectedByAsync(
            () => context.SaveChangesAsync(cancellationToken),
            CheckViolation,
            "ck_reviews_rating");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public async Task Rating_at_either_bound_is_accepted(int rating)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(postgres.ConnectionString, cancellationToken);

        await using var context = await database.CreateContextAsync(cancellationToken);

        var client = await Seed.ClientAsync(context, cancellationToken);
        var delivery = await Seed.DeliveryAsync(context, cancellationToken, client.Id);

        context.Reviews.Add(NewReview(delivery.Id, client.Id, rating));
        await context.SaveChangesAsync(cancellationToken);

        Assert.Equal(1, await context.Reviews.CountAsync(cancellationToken));
    }

    [Fact]
    public async Task Second_vehicle_with_the_same_plate_is_refused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(postgres.ConnectionString, cancellationToken);

        await using var context = await database.CreateContextAsync(cancellationToken);
        await Seed.VehicleAsync(context, cancellationToken, "AA1234BB");

        await using var second = await database.CreateContextAsync(cancellationToken);

        await PostgresFailure.RejectedByAsync(
            () => Seed.VehicleAsync(second, cancellationToken, "AA1234BB"),
            UniqueViolation,
            "ix_vehicles_license_plate");
    }

    [Fact]
    public async Task Second_driver_on_the_same_vehicle_is_refused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(postgres.ConnectionString, cancellationToken);

        await using var context = await database.CreateContextAsync(cancellationToken);

        var vehicle = await Seed.VehicleAsync(context, cancellationToken);
        await Seed.DriverAsync(context, cancellationToken, vehicle.Id);

        await using var second = await database.CreateContextAsync(cancellationToken);

        await PostgresFailure.RejectedByAsync(
            () => Seed.DriverAsync(second, cancellationToken, vehicle.Id),
            UniqueViolation,
            "ix_drivers_vehicle_id");
    }

    [Fact]
    public async Task Any_number_of_drivers_may_hold_no_vehicle()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(postgres.ConnectionString, cancellationToken);

        await using var context = await database.CreateContextAsync(cancellationToken);

        // The filter is what makes the one-to-one index survive contact with reality: without
        // it, the second unassigned driver would collide on a NULL.
        await Seed.DriverAsync(context, cancellationToken);
        await Seed.DriverAsync(context, cancellationToken);

        Assert.Equal(2, await context.Drivers.CountAsync(cancellationToken));
    }

    [Fact]
    public async Task Second_client_row_for_one_user_is_refused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(postgres.ConnectionString, cancellationToken);

        await using var context = await database.CreateContextAsync(cancellationToken);

        var client = await Seed.ClientAsync(context, cancellationToken);

        // One subtype row per user. AD-22's separation only means anything if the mapping from
        // a user to its client row is a function; two rows would make "the caller's ClientId"
        // ambiguous at exactly the point an ownership check reads it.
        await using var second = await database.CreateContextAsync(cancellationToken);
        second.Clients.Add(new Client { UserId = client.UserId, PhoneNumber = "+380440000000" });

        await PostgresFailure.RejectedByAsync(
            () => second.SaveChangesAsync(cancellationToken),
            UniqueViolation,
            "ix_clients_user_id");
    }

    [Fact]
    public async Task Second_driver_row_for_one_user_is_refused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(postgres.ConnectionString, cancellationToken);

        await using var context = await database.CreateContextAsync(cancellationToken);

        var driver = await Seed.DriverAsync(context, cancellationToken);

        await using var second = await database.CreateContextAsync(cancellationToken);
        second.Drivers.Add(new Driver
        {
            UserId = driver.UserId,
            LicenseNumber = "SECOND0001",
            VehicleId = null,
        });

        await PostgresFailure.RejectedByAsync(
            () => second.SaveChangesAsync(cancellationToken),
            UniqueViolation,
            "ix_drivers_user_id");
    }

    [Fact]
    public async Task Second_role_for_one_user_is_refused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(postgres.ConnectionString, cancellationToken);

        await using var context = await database.CreateContextAsync(cancellationToken);

        var user = await Seed.UserAsync(context, cancellationToken);

        var driverRole = new IdentityRole<int> { Name = "driver", NormalizedName = "DRIVER" };
        var dispatcherRole = new IdentityRole<int> { Name = "dispatcher", NormalizedName = "DISPATCHER" };

        context.Roles.AddRange(driverRole, dispatcherRole);
        await context.SaveChangesAsync(cancellationToken);

        context.UserRoles.Add(new IdentityUserRole<int> { UserId = user.Id, RoleId = driverRole.Id });
        await context.SaveChangesAsync(cancellationToken);

        // Two different roles, so the composite primary key is satisfied and the only thing that
        // can refuse the row is AD-4's unique index.
        await using var second = await database.CreateContextAsync(cancellationToken);
        second.UserRoles.Add(new IdentityUserRole<int> { UserId = user.Id, RoleId = dispatcherRole.Id });

        await PostgresFailure.RejectedByAsync(
            () => second.SaveChangesAsync(cancellationToken),
            UniqueViolation,
            "ix_asp_net_user_roles_user_id");
    }

    [Fact]
    public async Task Second_open_shift_for_one_driver_is_refused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(postgres.ConnectionString, cancellationToken);

        await using var context = await database.CreateContextAsync(cancellationToken);

        var driver = await Seed.DriverAsync(context, cancellationToken);

        context.Shifts.Add(new Shift { DriverId = driver.Id, StartedAt = Seed.Instant });
        await context.SaveChangesAsync(cancellationToken);

        await using var second = await database.CreateContextAsync(cancellationToken);
        second.Shifts.Add(new Shift { DriverId = driver.Id, StartedAt = Seed.Instant.AddHours(1) });

        await PostgresFailure.RejectedByAsync(
            () => second.SaveChangesAsync(cancellationToken),
            UniqueViolation,
            "ix_shifts_driver_id_open");
    }

    [Fact]
    public async Task A_second_shift_is_accepted_once_the_first_is_ended()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(postgres.ConnectionString, cancellationToken);

        await using var context = await database.CreateContextAsync(cancellationToken);

        var driver = await Seed.DriverAsync(context, cancellationToken);

        var first = new Shift { DriverId = driver.Id, StartedAt = Seed.Instant };
        context.Shifts.Add(first);
        await context.SaveChangesAsync(cancellationToken);

        first.EndedAt = Seed.Instant.AddHours(8);
        await context.SaveChangesAsync(cancellationToken);

        context.Shifts.Add(new Shift { DriverId = driver.Id, StartedAt = Seed.Instant.AddHours(9) });
        await context.SaveChangesAsync(cancellationToken);

        Assert.Equal(2, await context.Shifts.CountAsync(cancellationToken));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Non_positive_package_weight_is_refused(decimal weight)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(postgres.ConnectionString, cancellationToken);

        await using var context = await database.CreateContextAsync(cancellationToken);

        context.Deliveries.Add(Seed.NewDelivery(packageWeightKg: weight));

        await PostgresFailure.RejectedByAsync(
            () => context.SaveChangesAsync(cancellationToken),
            CheckViolation,
            "ck_deliveries_package_weight_kg");
    }

    [Fact]
    public async Task Delivery_window_whose_earliest_is_not_before_its_latest_is_refused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(postgres.ConnectionString, cancellationToken);

        // Equal bounds: the boundary case, and the one an "earliest <= latest" check would let
        // through as a window nothing can be delivered inside.
        await using var equalBounds = await database.CreateContextAsync(cancellationToken);
        equalBounds.Deliveries.Add(Seed.NewDelivery(
            windowEarliestAt: Seed.Instant, windowLatestAt: Seed.Instant));

        await PostgresFailure.RejectedByAsync(
            () => equalBounds.SaveChangesAsync(cancellationToken),
            CheckViolation,
            "ck_deliveries_delivery_window");

        await using var inverted = await database.CreateContextAsync(cancellationToken);
        inverted.Deliveries.Add(Seed.NewDelivery(
            windowEarliestAt: Seed.Instant.AddHours(2), windowLatestAt: Seed.Instant));

        await PostgresFailure.RejectedByAsync(
            () => inverted.SaveChangesAsync(cancellationToken),
            CheckViolation,
            "ck_deliveries_delivery_window");
    }

    [Fact]
    public async Task Delivery_window_with_a_missing_bound_is_accepted()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(postgres.ConnectionString, cancellationToken);

        await using var context = await database.CreateContextAsync(cancellationToken);

        // FR-100: either bound may stand alone, and both may be absent.
        context.Deliveries.Add(Seed.NewDelivery());
        context.Deliveries.Add(Seed.NewDelivery(windowEarliestAt: Seed.Instant));
        context.Deliveries.Add(Seed.NewDelivery(windowLatestAt: Seed.Instant));
        context.Deliveries.Add(Seed.NewDelivery(
            windowEarliestAt: Seed.Instant, windowLatestAt: Seed.Instant.AddHours(2)));

        await context.SaveChangesAsync(cancellationToken);

        Assert.Equal(4, await context.Deliveries.CountAsync(cancellationToken));
    }

    [Fact]
    public async Task Second_proof_for_one_delivery_is_refused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(postgres.ConnectionString, cancellationToken);

        await using var context = await database.CreateContextAsync(cancellationToken);

        var user = await Seed.UserAsync(context, cancellationToken);
        var delivery = await Seed.DeliveryAsync(context, cancellationToken);

        context.ProofOfDeliveries.Add(NewProof(delivery.Id, user.Id));
        await context.SaveChangesAsync(cancellationToken);

        await using var second = await database.CreateContextAsync(cancellationToken);
        second.ProofOfDeliveries.Add(NewProof(delivery.Id, user.Id));

        await PostgresFailure.RejectedByAsync(
            () => second.SaveChangesAsync(cancellationToken),
            UniqueViolation,
            "ix_proof_of_deliveries_delivery_id");
    }

    private static Review NewReview(int deliveryId, ClientId clientId, int rating) =>
        new()
        {
            DeliveryId = deliveryId,
            ClientId = clientId,
            Rating = rating,
            Text = "Arrived on time.",
            CreatedAt = Seed.Instant,
        };

    private static ProofOfDelivery NewProof(int deliveryId, int userId) =>
        new()
        {
            DeliveryId = deliveryId,
            RecipientName = "Recipient",
            CaptureLocation = Seed.Location(),
            CapturedAt = Seed.Instant,
            CapturedByUserId = new UserId(userId),
        };
}
