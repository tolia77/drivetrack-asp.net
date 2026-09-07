using System.Globalization;
using DriveTrack.Domain.Deliveries;
using DriveTrack.Domain.Identity;
using DriveTrack.Domain.Reviews;
using DriveTrack.Domain.Vehicles;
using DriveTrack.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace DriveTrack.Integration.Tests.Persistence;

/// <summary>
/// AD-5 and NFR-9: one operation, one scope, one commit — and nothing left behind when the
/// operation does not finish. The scope is the only thing standing between a multi-step write
/// and a half-applied one, so it is asserted against a real database rather than a fake.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class UnitOfWorkTests(PostgresFixture postgres)
{
    [Fact]
    public async Task A_multi_step_write_lands_whole()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(postgres.ConnectionString, cancellationToken);

        int userId;
        await using (var seedContext = await database.CreateContextAsync(cancellationToken))
        {
            var user = await Seed.UserAsync(seedContext, cancellationToken);
            userId = user.Id;
        }

        await using (var unitOfWork = await database.UnitOfWorkFactory.CreateAsync(cancellationToken))
        {
            var vehicle = new Vehicle
            {
                Model = "Renault Master",
                LicensePlate = "AA0001AA",
                CapacityKg = 1200m,
                Mileage = 0,
                NextMaintenanceDate = null,
            };
            unitOfWork.Vehicles.Add(vehicle);

            var delivery = Seed.NewDelivery();
            unitOfWork.Deliveries.Add(delivery);

            await unitOfWork.CommitAsync(cancellationToken);

            // The timeline entry needs the delivery's key, so it is a second scope - which is
            // exactly the shape a real operation takes when it reacts to a committed write.
            await using var second = await database.UnitOfWorkFactory.CreateAsync(cancellationToken);
            second.TimelineEntries.Add(Seed.NewTimelineEntry(delivery.Id, new UserId(userId)));
            await second.CommitAsync(cancellationToken);
        }

        await using var context = await database.CreateContextAsync(cancellationToken);

        Assert.Equal(1, await context.Vehicles.CountAsync(cancellationToken));
        Assert.Equal(1, await context.Deliveries.CountAsync(cancellationToken));
        Assert.Equal(1, await context.TimelineEntries.CountAsync(cancellationToken));
    }

    [Fact]
    public async Task A_failure_part_way_through_leaves_nothing_behind()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(postgres.ConnectionString, cancellationToken);

        int deliveryId;
        ClientId clientId;

        await using (var seedContext = await database.CreateContextAsync(cancellationToken))
        {
            var client = await Seed.ClientAsync(seedContext, cancellationToken);
            clientId = client.Id;

            var delivery = await Seed.DeliveryAsync(seedContext, cancellationToken, client.Id);
            deliveryId = delivery.Id;
        }

        await using (var unitOfWork = await database.UnitOfWorkFactory.CreateAsync(cancellationToken))
        {
            // A legal write...
            unitOfWork.Vehicles.Add(new Vehicle
            {
                Model = "Citroen Jumper",
                LicensePlate = "BB0002BB",
                CapacityKg = 900m,
                Mileage = 10,
                NextMaintenanceDate = null,
            });

            // ...followed by one the rating check refuses.
            unitOfWork.Reviews.Add(new Review
            {
                DeliveryId = deliveryId,
                ClientId = clientId,
                Rating = 9,
                Text = "Out of range.",
                CreatedAt = Seed.Instant,
            });

            await Assert.ThrowsAnyAsync<Exception>(
                () => unitOfWork.CommitAsync(cancellationToken));
        }

        await using var context = await database.CreateContextAsync(cancellationToken);

        // NFR-9: the legal half is gone too. Without the transaction the vehicle would be
        // sitting there, committed, with nothing that references it.
        Assert.Equal(0, await context.Vehicles.CountAsync(cancellationToken));
        Assert.Equal(0, await context.Reviews.CountAsync(cancellationToken));
    }

    [Fact]
    public async Task Disposing_without_committing_persists_nothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(postgres.ConnectionString, cancellationToken);

        await using (var unitOfWork = await database.UnitOfWorkFactory.CreateAsync(cancellationToken))
        {
            unitOfWork.Deliveries.Add(Seed.NewDelivery());

            // No CommitAsync. An operation that throws before its commit takes this path, and
            // rolling back has to be what happens by default rather than what a catch block
            // remembers to do.
        }

        await using var context = await database.CreateContextAsync(cancellationToken);

        Assert.Equal(0, await context.Deliveries.CountAsync(cancellationToken));
    }

    [Fact]
    public async Task A_second_commit_on_one_scope_is_refused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(postgres.ConnectionString, cancellationToken);

        await using var unitOfWork = await database.UnitOfWorkFactory.CreateAsync(cancellationToken);

        unitOfWork.Deliveries.Add(Seed.NewDelivery());
        await unitOfWork.CommitAsync(cancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => unitOfWork.CommitAsync(cancellationToken));
    }

    [Fact]
    public async Task Entries_written_in_one_transaction_come_back_in_the_order_they_were_appended()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(postgres.ConnectionString, cancellationToken);

        int deliveryId;
        int actorUserId;

        await using (var seedContext = await database.CreateContextAsync(cancellationToken))
        {
            var user = await Seed.UserAsync(seedContext, cancellationToken);
            var delivery = await Seed.DeliveryAsync(seedContext, cancellationToken);
            deliveryId = delivery.Id;
            actorUserId = user.Id;
        }

        // AD-27 writes a timeline entry inside the transaction of the change it records, so two
        // entries appended by one operation share an instant exactly. The ids are assigned by
        // hand, and the higher one is written first, so the rows sit on the page in the reverse
        // of their append order: ordering by occurred_at alone returns "Second" first and the
        // assertions below fail. Only the ThenBy(Id) tie-break makes this test pass, which is
        // the whole point - seeding through EF would have let insertion order stand in for it.
        var instant = Seed.Instant.ToString("O", CultureInfo.InvariantCulture);
        await database.ExecuteAsync(
            $"""
             INSERT INTO timeline_entries
                 (id, delivery_id, actor_user_id, actor_display_name, actor_role, note, occurred_at)
             VALUES
                 (2, {deliveryId}, {actorUserId}, 'Test Person', 'dispatcher', 'Second', '{instant}'),
                 (1, {deliveryId}, {actorUserId}, 'Test Person', 'dispatcher', 'First', '{instant}');
             """,
            cancellationToken);

        await using var unitOfWork = await database.UnitOfWorkFactory.CreateAsync(cancellationToken);

        var entries = await unitOfWork.TimelineEntries.ListForDeliveryAsync(deliveryId, cancellationToken);

        Assert.Equal(2, entries.Count);
        Assert.Equal("First", entries[0].Note);
        Assert.Equal("Second", entries[1].Note);
    }

    [Fact]
    public async Task A_typed_identity_survives_the_round_trip_through_its_repository()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(postgres.ConnectionString, cancellationToken);

        ClientId clientId;
        DriverId driverId;

        await using (var seedContext = await database.CreateContextAsync(cancellationToken))
        {
            clientId = (await Seed.ClientAsync(seedContext, cancellationToken)).Id;
            driverId = (await Seed.DriverAsync(seedContext, cancellationToken)).Id;
        }

        await using var unitOfWork = await database.UnitOfWorkFactory.CreateAsync(cancellationToken);

        // These two lookups are the only ones in the system whose predicate compares a
        // value-converted key, so they are the only place a converter or translation regression
        // would show up before a later story tripped over it at runtime.
        var client = await unitOfWork.Clients.GetByIdAsync(clientId, cancellationToken);
        var driver = await unitOfWork.Drivers.GetByIdAsync(driverId, cancellationToken);

        Assert.NotNull(client);
        Assert.Equal(clientId, client.Id);

        Assert.NotNull(driver);
        Assert.Equal(driverId, driver.Id);

        // And a miss is a miss, not an exception or the first row of the table.
        Assert.Null(await unitOfWork.Clients.GetByIdAsync(
            new ClientId(clientId.Value + 1000), cancellationToken));
        Assert.Null(await unitOfWork.Drivers.GetByIdAsync(
            new DriverId(driverId.Value + 1000), cancellationToken));
    }

    [Fact]
    public async Task A_repository_result_is_materialized_and_outlives_the_scope()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(postgres.ConnectionString, cancellationToken);

        int deliveryId;
        int userId;

        await using (var seedContext = await database.CreateContextAsync(cancellationToken))
        {
            var user = await Seed.UserAsync(seedContext, cancellationToken);
            userId = user.Id;

            var delivery = await Seed.DeliveryAsync(seedContext, cancellationToken);
            deliveryId = delivery.Id;

            seedContext.TimelineEntries.Add(Seed.NewTimelineEntry(
                delivery.Id, new UserId(user.Id), note: "Picked up"));
            seedContext.TimelineEntries.Add(Seed.NewTimelineEntry(
                delivery.Id,
                new UserId(user.Id),
                previousStatus: DeliveryStatus.InTransit,
                newStatus: DeliveryStatus.Delivered,
                note: "Delivered",
                occurredAt: Seed.Instant.AddHours(2)));

            await seedContext.SaveChangesAsync(cancellationToken);
        }

        Delivery? delivery2;
        IReadOnlyList<TimelineEntry> entries;

        await using (var unitOfWork = await database.UnitOfWorkFactory.CreateAsync(cancellationToken))
        {
            delivery2 = await unitOfWork.Deliveries.GetByIdAsync(deliveryId, cancellationToken);
            entries = await unitOfWork.TimelineEntries.ListForDeliveryAsync(deliveryId, cancellationToken);
        }

        // AD-6: the scope is gone, and the results are still readable - which is only true
        // because nothing crossed the boundary holding a live context open.
        Assert.NotNull(delivery2);
        Assert.Equal(deliveryId, delivery2.Id);
        Assert.Equal(50.4501, delivery2.PickupLocation.Latitude, 4);

        Assert.Equal(2, entries.Count);
        Assert.Equal("Picked up", entries[0].Note);
        Assert.Equal("Delivered", entries[1].Note);
        Assert.Equal(new UserId(userId), entries[0].ActorUserId);
    }
}
