using DriveTrack.Application.Abstractions;
using DriveTrack.Domain.Deliveries;
using Microsoft.EntityFrameworkCore;

namespace DriveTrack.Infrastructure.Persistence.Repositories;

/// <summary>
/// EF Core implementation of <see cref="ITimelineEntryRepository"/> — append and read only.
/// <para>
/// There is no update or delete method here because there is none on the interface (AD-27), and
/// none on the interface because a path that does not exist cannot be taken by mistake. The
/// migration's trigger covers the other half: SQL that never passes through this class.
/// </para>
/// </summary>
internal sealed class EfTimelineEntryRepository(AppDbContext context) : ITimelineEntryRepository
{
    /// <inheritdoc />
    public void Add(TimelineEntry entry) => context.TimelineEntries.Add(entry);

    /// <inheritdoc />
    public async Task<IReadOnlyList<TimelineEntry>> ListForDeliveryAsync(
        int deliveryId,
        CancellationToken cancellationToken) =>
        await context.TimelineEntries
            // Required by AD-27's guard, not an optimisation. A tracked entry makes EF a
            // client-cascade candidate: deleting its delivery in the same scope would have EF
            // issue its own DELETE against timeline_entries first, arriving at trigger depth 1,
            // where the append-only guard raises. Untracked, the database cascade runs instead,
            // at depth 2, which the guard permits.
            .AsNoTracking()
            .Where(entry => entry.DeliveryId == deliveryId)
            // Id breaks a tie: two entries written in the same transaction share an instant,
            // and FR-108 asks for an order, not an almost-order.
            .OrderBy(entry => entry.OccurredAt)
            .ThenBy(entry => entry.Id)
            .ToListAsync(cancellationToken);
}
