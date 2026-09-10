using DriveTrack.Domain.Deliveries;
using DriveTrack.Domain.Identity;
using DriveTrack.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace DriveTrack.Integration.Tests.Persistence;

/// <summary>
/// AD-27's guard, which is the reason the append-only rule is worth anything.
/// <para>
/// The interface has no update or delete method, but an interface only binds code that goes
/// through it. These tests issue SQL straight at the table — the shape a stray script, a
/// migration or a console session would take — and prove the database itself refuses, while
/// still letting the one removal DR-9 declares run.
/// </para>
/// </summary>
public class TimelineAppendOnlyTests(PostgresFixture postgres)
{
    private const string CheckViolation = "23514";

    /// <summary>
    /// The name the guard raises under. It is not a real check constraint - a trigger has no
    /// constraint of its own - but story 1.3 translates violations by name, so the trigger
    /// supplies a stable one rather than leaving callers to match on message text.
    /// </summary>
    private const string AppendOnlyConstraint = "ck_timeline_entries_append_only";

    [Fact]
    public async Task A_direct_update_raises()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(postgres.ConnectionString, cancellationToken);

        var entryId = await AppendEntryAsync(database, cancellationToken);

        await PostgresFailure.RejectedByAsync(
            () => database.ExecuteAsync(
                $"UPDATE timeline_entries SET note = 'rewritten' WHERE id = {entryId}",
                cancellationToken),
            CheckViolation,
            AppendOnlyConstraint);
    }

    [Fact]
    public async Task A_direct_delete_raises()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(postgres.ConnectionString, cancellationToken);

        var entryId = await AppendEntryAsync(database, cancellationToken);

        await PostgresFailure.RejectedByAsync(
            () => database.ExecuteAsync(
                $"DELETE FROM timeline_entries WHERE id = {entryId}", cancellationToken),
            CheckViolation,
            AppendOnlyConstraint);
    }

    [Fact]
    public async Task A_correction_is_a_new_entry_and_the_original_stands()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(postgres.ConnectionString, cancellationToken);

        await using var context = await database.CreateContextAsync(cancellationToken);

        var user = await Seed.UserAsync(context, cancellationToken);
        var delivery = await Seed.DeliveryAsync(context, cancellationToken);

        context.TimelineEntries.Add(Seed.NewTimelineEntry(
            delivery.Id, new UserId(user.Id), note: "Left with neighbour"));
        await context.SaveChangesAsync(cancellationToken);

        // FR-107, FR-108: a note-only entry is legal, and it is how a correction is made.
        context.TimelineEntries.Add(Seed.NewTimelineEntry(
            delivery.Id,
            new UserId(user.Id),
            previousStatus: null,
            newStatus: null,
            note: "Correction: left at reception",
            occurredAt: Seed.Instant.AddMinutes(5)));
        await context.SaveChangesAsync(cancellationToken);

        var entries = await context.TimelineEntries
            .AsNoTracking()
            .Where(entry => entry.DeliveryId == delivery.Id)
            .OrderBy(entry => entry.OccurredAt)
            .ToListAsync(cancellationToken);

        Assert.Equal(2, entries.Count);
        Assert.Equal("Left with neighbour", entries[0].Note);
        Assert.Equal("Correction: left at reception", entries[1].Note);
        Assert.Null(entries[1].NewStatus);
    }

    [Fact]
    public async Task The_cascade_from_the_delivery_is_permitted()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(postgres.ConnectionString, cancellationToken);

        int deliveryId;

        await using (var context = await database.CreateContextAsync(cancellationToken))
        {
            var user = await Seed.UserAsync(context, cancellationToken);
            var delivery = await Seed.DeliveryAsync(context, cancellationToken);
            deliveryId = delivery.Id;

            context.TimelineEntries.Add(Seed.NewTimelineEntry(delivery.Id, new UserId(user.Id)));
            await context.SaveChangesAsync(cancellationToken);
        }

        // The same DELETE the guard refuses when issued directly, now arriving through the
        // foreign key's referential-integrity trigger at depth 2. That distinction is the whole
        // design: the guard reads trigger depth, not the statement text.
        await database.ExecuteAsync($"DELETE FROM deliveries WHERE id = {deliveryId}", cancellationToken);

        Assert.Equal(0L, await CountAsync(database, "timeline_entries", cancellationToken));
    }

    [Fact]
    public async Task Deleting_the_actor_nulls_the_reference_and_leaves_the_snapshot_intact()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(postgres.ConnectionString, cancellationToken);

        int entryId;
        int userId;

        await using (var context = await database.CreateContextAsync(cancellationToken))
        {
            var user = await Seed.UserAsync(context, cancellationToken);
            userId = user.Id;

            var delivery = await Seed.DeliveryAsync(context, cancellationToken);

            var entry = Seed.NewTimelineEntry(delivery.Id, new UserId(user.Id));
            context.TimelineEntries.Add(entry);
            await context.SaveChangesAsync(cancellationToken);

            entryId = entry.Id;
        }

        // AD-20: set null, not restrict and not cascade. This is also an UPDATE on an
        // append-only table, permitted for the same depth reason as the cascade above.
        await database.ExecuteAsync($"DELETE FROM asp_net_users WHERE id = {userId}", cancellationToken);

        await using var after = await database.CreateContextAsync(cancellationToken);

        var survivor = await after.TimelineEntries
            .AsNoTracking()
            .SingleAsync(entry => entry.Id == entryId, cancellationToken);

        Assert.Null(survivor.ActorUserId);

        // The point of the snapshot: the entry still says who did it, which is what a restrict
        // would have protected at the cost of making the user undeletable forever.
        Assert.Equal("Test Person", survivor.ActorDisplayName);
        Assert.Equal(UserRole.Dispatcher, survivor.ActorRole);
        Assert.Equal(DeliveryStatus.InTransit, survivor.NewStatus);
    }

    [Fact]
    public async Task A_direct_truncate_raises()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(postgres.ConnectionString, cancellationToken);

        await AppendEntryAsync(database, cancellationToken);

        // A row trigger never sees a TRUNCATE - it removes every row without producing one - so
        // the two BEFORE UPDATE/DELETE triggers alone would leave the table emptiable in one
        // statement. This is the third trigger, at statement level, closing that door.
        await PostgresFailure.RejectedByAsync(
            () => database.ExecuteAsync("TRUNCATE timeline_entries", cancellationToken),
            CheckViolation,
            AppendOnlyConstraint);

        Assert.Equal(1L, await CountAsync(database, "timeline_entries", cancellationToken));
    }

    [Fact]
    public async Task Deleting_a_delivery_through_the_unit_of_work_after_reading_its_timeline_succeeds()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(postgres.ConnectionString, cancellationToken);

        int deliveryId;

        await using (var context = await database.CreateContextAsync(cancellationToken))
        {
            var user = await Seed.UserAsync(context, cancellationToken);
            var delivery = await Seed.DeliveryAsync(context, cancellationToken);
            deliveryId = delivery.Id;

            context.TimelineEntries.Add(Seed.NewTimelineEntry(delivery.Id, new UserId(user.Id)));
            await context.SaveChangesAsync(cancellationToken);
        }

        // The real path, in one scope: read the timeline, then delete the delivery. If the read
        // tracked its entries, EF would client-cascade them with DELETE statements of its own at
        // trigger depth 1 and the guard would refuse the whole operation. The AsNoTracking in
        // ListForDeliveryAsync is what keeps that from happening, and this is the test that says
        // so - without it, removing that one call breaks nothing until a later story.
        await using (var unitOfWork = await database.UnitOfWorkFactory.CreateAsync(cancellationToken))
        {
            var entries = await unitOfWork.TimelineEntries
                .ListForDeliveryAsync(deliveryId, cancellationToken);

            Assert.Single(entries);

            var delivery = await unitOfWork.Deliveries.GetByIdAsync(deliveryId, cancellationToken);
            Assert.NotNull(delivery);

            unitOfWork.Deliveries.Remove(delivery);
            await unitOfWork.CommitAsync(cancellationToken);
        }

        Assert.Equal(0L, await CountAsync(database, "timeline_entries", cancellationToken));
        Assert.Equal(0L, await CountAsync(database, "deliveries", cancellationToken));
    }

    private static async Task<int> AppendEntryAsync(
        TestDatabase database,
        CancellationToken cancellationToken)
    {
        await using var context = await database.CreateContextAsync(cancellationToken);

        var user = await Seed.UserAsync(context, cancellationToken);
        var delivery = await Seed.DeliveryAsync(context, cancellationToken);

        var entry = Seed.NewTimelineEntry(delivery.Id, new UserId(user.Id), note: "Picked up");
        context.TimelineEntries.Add(entry);
        await context.SaveChangesAsync(cancellationToken);

        return entry.Id;
    }

    private static async Task<long> CountAsync(
        TestDatabase database,
        string table,
        CancellationToken cancellationToken)
    {
        var value = await database.ScalarAsync($"SELECT count(*) FROM {table}", cancellationToken);

        return Assert.IsType<long>(value);
    }
}
