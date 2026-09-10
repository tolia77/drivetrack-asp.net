using DriveTrack.Domain.Chat;
using DriveTrack.Domain.Deliveries;
using DriveTrack.Domain.Identity;
using DriveTrack.Domain.Reviews;
using DriveTrack.Domain.Shifts;
using DriveTrack.Integration.Tests.Support;

namespace DriveTrack.Integration.Tests.Persistence;

/// <summary>
/// AD-20's delete table, proved at the schema level.
/// <para>
/// Every delete here is raw SQL rather than <c>DbSet.Remove</c>, deliberately: EF can produce
/// the right outcome by issuing its own statements for dependents it happens to be tracking,
/// which would leave the test green while the declared <c>ON DELETE</c> was missing entirely.
/// The original's "deleting a driver raises a database error" was an undeclared foreign key,
/// not a missing <c>if</c>, so what has to be asserted is what the database does unaided.
/// </para>
/// </summary>
public class DeleteBehaviourTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Deleting_a_driver_unassigns_its_deliveries_and_removes_its_shifts_and_messages()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(postgres.ConnectionString, cancellationToken);

        int deliveryId;
        DriverId driverId;

        await using (var context = await database.CreateContextAsync(cancellationToken))
        {
            var user = await Seed.UserAsync(context, cancellationToken);
            var driver = await Seed.DriverAsync(context, cancellationToken);
            driverId = driver.Id;

            var delivery = await Seed.DeliveryAsync(context, cancellationToken, driverId: driver.Id);
            deliveryId = delivery.Id;

            context.Shifts.Add(new Shift { DriverId = driver.Id, StartedAt = Seed.Instant });
            context.Messages.Add(new Message
            {
                DriverId = driver.Id,
                SenderUserId = new UserId(user.Id),
                Text = "On my way.",
                SentAt = Seed.Instant,
            });

            await context.SaveChangesAsync(cancellationToken);
        }

        await database.ExecuteAsync($"DELETE FROM drivers WHERE id = {driverId.Value}", cancellationToken);

        // FR-16, FR-39: an unassigned delivery is legal, so the delivery survives with no driver.
        Assert.Equal(1L, await CountAsync(database, $"deliveries WHERE id = {deliveryId}", cancellationToken));
        Assert.Null(await database.ScalarAsync(
            $"SELECT driver_id FROM deliveries WHERE id = {deliveryId}", cancellationToken));

        Assert.Equal(0L, await CountAsync(database, "shifts", cancellationToken));
        Assert.Equal(0L, await CountAsync(database, "messages", cancellationToken));
    }

    [Fact]
    public async Task Deleting_a_client_unassigns_its_deliveries_and_removes_its_reviews()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(postgres.ConnectionString, cancellationToken);

        int deliveryId;
        ClientId clientId;

        await using (var context = await database.CreateContextAsync(cancellationToken))
        {
            var client = await Seed.ClientAsync(context, cancellationToken);
            clientId = client.Id;

            var delivery = await Seed.DeliveryAsync(context, cancellationToken, client.Id);
            deliveryId = delivery.Id;

            context.Reviews.Add(new Review
            {
                DeliveryId = delivery.Id,
                ClientId = client.Id,
                Rating = 5,
                Text = "Excellent.",
                CreatedAt = Seed.Instant,
            });

            await context.SaveChangesAsync(cancellationToken);
        }

        await database.ExecuteAsync($"DELETE FROM clients WHERE id = {clientId.Value}", cancellationToken);

        // FR-47: the delivery is a record of work done and outlives the account that asked for it.
        Assert.Equal(1L, await CountAsync(database, $"deliveries WHERE id = {deliveryId}", cancellationToken));
        Assert.Null(await database.ScalarAsync(
            $"SELECT client_id FROM deliveries WHERE id = {deliveryId}", cancellationToken));

        Assert.Equal(0L, await CountAsync(database, "reviews", cancellationToken));
    }

    [Fact]
    public async Task Deleting_a_vehicle_leaves_its_driver_holding_none()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(postgres.ConnectionString, cancellationToken);

        int vehicleId;
        DriverId driverId;

        await using (var context = await database.CreateContextAsync(cancellationToken))
        {
            var vehicle = await Seed.VehicleAsync(context, cancellationToken);
            vehicleId = vehicle.Id;

            var driver = await Seed.DriverAsync(context, cancellationToken, vehicle.Id);
            driverId = driver.Id;
        }

        // FR-43's block on deleting an assigned vehicle is an application rule (story 4.1). At
        // the schema level the point is only that it cannot take the driver down with it.
        await database.ExecuteAsync($"DELETE FROM vehicles WHERE id = {vehicleId}", cancellationToken);

        Assert.Equal(1L, await CountAsync(database, $"drivers WHERE id = {driverId.Value}", cancellationToken));
        Assert.Null(await database.ScalarAsync(
            $"SELECT vehicle_id FROM drivers WHERE id = {driverId.Value}", cancellationToken));
    }

    [Fact]
    public async Task Deleting_a_delivery_takes_its_timeline_proof_review_and_attempts_with_it()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(postgres.ConnectionString, cancellationToken);

        int deliveryId;

        await using (var context = await database.CreateContextAsync(cancellationToken))
        {
            var user = await Seed.UserAsync(context, cancellationToken);
            var client = await Seed.ClientAsync(context, cancellationToken);
            var delivery = await Seed.DeliveryAsync(context, cancellationToken, client.Id);
            deliveryId = delivery.Id;

            context.TimelineEntries.Add(Seed.NewTimelineEntry(delivery.Id, new UserId(user.Id)));

            var proof = new ProofOfDelivery
            {
                DeliveryId = delivery.Id,
                RecipientName = "Recipient",
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
                DeliveryId = delivery.Id,
                ClientId = client.Id,
                Rating = 4,
                Text = "Fine.",
                CreatedAt = Seed.Instant,
            });

            context.NotificationAttempts.Add(new NotificationAttempt
            {
                DeliveryId = delivery.Id,
                Kind = NotificationKind.StatusChange,
                Recipient = "someone@drivetrack.test",
                AttemptedAt = Seed.Instant,
                Outcome = NotificationOutcome.Failed,
                Error = "SMTP timeout",
            });

            await context.SaveChangesAsync(cancellationToken);
        }

        // DR-9. The append-only trigger permits this precisely because the cascade runs inside
        // the referential-integrity trigger rather than as a statement of its own.
        await database.ExecuteAsync($"DELETE FROM deliveries WHERE id = {deliveryId}", cancellationToken);

        Assert.Equal(0L, await CountAsync(database, "timeline_entries", cancellationToken));
        Assert.Equal(0L, await CountAsync(database, "proof_of_deliveries", cancellationToken));
        Assert.Equal(0L, await CountAsync(database, "proof_assets", cancellationToken));
        Assert.Equal(0L, await CountAsync(database, "reviews", cancellationToken));
        Assert.Equal(0L, await CountAsync(database, "notification_attempts", cancellationToken));
    }

    [Fact]
    public async Task Deleting_a_user_takes_its_client_and_driver_rows_with_it()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(postgres.ConnectionString, cancellationToken);

        int clientUserId;
        int driverUserId;

        await using (var context = await database.CreateContextAsync(cancellationToken))
        {
            var client = await Seed.ClientAsync(context, cancellationToken);
            var driver = await Seed.DriverAsync(context, cancellationToken);

            clientUserId = client.UserId.Value;
            driverUserId = driver.UserId.Value;
        }

        // These two foreign keys are the hand-written half of the migration; without them the
        // subtype row would simply be left pointing at a user that no longer exists.
        await database.ExecuteAsync(
            $"DELETE FROM asp_net_users WHERE id IN ({clientUserId}, {driverUserId})",
            cancellationToken);

        Assert.Equal(0L, await CountAsync(database, "clients", cancellationToken));
        Assert.Equal(0L, await CountAsync(database, "drivers", cancellationToken));
    }

    [Fact]
    public async Task Deleting_a_message_sender_leaves_the_thread_standing_unattributed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(postgres.ConnectionString, cancellationToken);

        int senderUserId;
        int messageId;

        await using (var context = await database.CreateContextAsync(cancellationToken))
        {
            var sender = await Seed.UserAsync(context, cancellationToken);
            senderUserId = sender.Id;

            var driver = await Seed.DriverAsync(context, cancellationToken);

            var message = new Message
            {
                DriverId = driver.Id,
                SenderUserId = new UserId(sender.Id),
                Text = "On my way.",
                SentAt = Seed.Instant,
            };

            context.Messages.Add(message);
            await context.SaveChangesAsync(cancellationToken);

            messageId = message.Id;
        }

        // Cascade would delete half a conversation to close one account; restrict would make
        // closing it impossible. Set null leaves the line readable and unattributed.
        await database.ExecuteAsync(
            $"DELETE FROM asp_net_users WHERE id = {senderUserId}", cancellationToken);

        Assert.Equal(1L, await CountAsync(database, $"messages WHERE id = {messageId}", cancellationToken));
        Assert.Null(await database.ScalarAsync(
            $"SELECT sender_user_id FROM messages WHERE id = {messageId}", cancellationToken));
        Assert.Equal(
            "On my way.",
            await database.ScalarAsync($"SELECT text FROM messages WHERE id = {messageId}", cancellationToken));
    }

    [Fact]
    public async Task Deleting_a_proof_capturer_leaves_the_proof_standing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(postgres.ConnectionString, cancellationToken);

        int capturerUserId;
        int proofId;

        await using (var context = await database.CreateContextAsync(cancellationToken))
        {
            var capturer = await Seed.UserAsync(context, cancellationToken);
            capturerUserId = capturer.Id;

            var delivery = await Seed.DeliveryAsync(context, cancellationToken);

            var proof = new ProofOfDelivery
            {
                DeliveryId = delivery.Id,
                RecipientName = "Recipient",
                CaptureLocation = Seed.Location(),
                CapturedAt = Seed.Instant,
                CapturedByUserId = new UserId(capturer.Id),
            };

            context.ProofOfDeliveries.Add(proof);
            await context.SaveChangesAsync(cancellationToken);

            proofId = proof.Id;
        }

        // The evidence that a delivery was completed must not disappear because the driver who
        // captured it later left the company.
        await database.ExecuteAsync(
            $"DELETE FROM asp_net_users WHERE id = {capturerUserId}", cancellationToken);

        Assert.Equal(
            1L,
            await CountAsync(database, $"proof_of_deliveries WHERE id = {proofId}", cancellationToken));
        Assert.Null(await database.ScalarAsync(
            $"SELECT captured_by_user_id FROM proof_of_deliveries WHERE id = {proofId}",
            cancellationToken));
        Assert.Equal(
            "Recipient",
            await database.ScalarAsync(
                $"SELECT recipient_name FROM proof_of_deliveries WHERE id = {proofId}", cancellationToken));
    }

    private static async Task<long> CountAsync(
        TestDatabase database,
        string fromClause,
        CancellationToken cancellationToken)
    {
        var value = await database.ScalarAsync($"SELECT count(*) FROM {fromClause}", cancellationToken);

        return Assert.IsType<long>(value);
    }
}
