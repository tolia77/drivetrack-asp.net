using DriveTrack.Application.Authorization;
using DriveTrack.Domain.Deliveries;
using DriveTrack.Domain.Identity;

namespace DriveTrack.Application.Abstractions;

/// <summary>Persistence for <see cref="Delivery"/>. See <see cref="IClientRepository"/> for the shape rules (AD-6).</summary>
public interface IDeliveryRepository
{
    /// <summary>Loads a delivery, or null when there is none with that id.</summary>
    /// <remarks>
    /// Deliberately un-included, and it must stay that way. The delete path loads through here, and
    /// a tracked timeline entry makes EF client-cascade the dependent by issuing its own
    /// <c>DELETE</c> at trigger depth 1, where the append-only guard raises. With nothing tracked,
    /// the database's own cascade runs at depth 2, which the guard permits (AD-27).
    /// </remarks>
    Task<Delivery?> GetByIdAsync(int id, CancellationToken cancellationToken);

    /// <summary>
    /// One page of deliveries, oldest first, narrowed to the rows the caller may see (FR-18, FR-25,
    /// NFR-27).
    /// </summary>
    /// <remarks>
    /// "Oldest first" is the row id ascending, not <see cref="Delivery.CreatedAt"/>: the id comes
    /// from a sequence, so it is insertion order, and insertion order is creation order for every
    /// row this system writes. Ordering by the timestamp instead would need a tie-break and an
    /// index for no change in the answer.
    /// <para>
    /// There is no overload without <paramref name="scope"/>, and AD-3 is why: a page fetched
    /// unscoped and filtered afterwards answers an empty page for a driver whose rows fall outside
    /// the first hundred, and no amount of paging further finds them. The narrowing has to be part
    /// of the query, so it is part of the signature.
    /// </para>
    /// </remarks>
    /// <param name="scope">The guard's answer to "whose rows may this caller see".</param>
    /// <param name="offset">Rows to skip. The caller has already validated it.</param>
    /// <param name="limit">Rows to take. The caller has already validated it.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<Delivery>> ListAsync(
        AccessScope scope,
        int offset,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>
    /// The heaviest package among the driver's deliveries that have not been carried yet, or null
    /// when they have none — the one question FR-103's fleet-side arm asks.
    /// <para>
    /// Only <see cref="DeliveryStatus.Pending"/> and <see cref="DeliveryStatus.InTransit"/> count.
    /// A delivered or failed parcel has already been carried, and counting it would let a job
    /// finished last winter block a fleet change for good.
    /// </para>
    /// </summary>
    /// <param name="driverId">The driver whose load is being weighed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<decimal?> FindHeaviestActiveWeightForDriverAsync(
        DriverId driverId,
        CancellationToken cancellationToken);

    /// <summary>Stages a new delivery for the next commit.</summary>
    void Add(Delivery delivery);

    /// <summary>Stages a delivery for deletion (FR-24). Its timeline, proof, review and notification attempts go with it (DR-9).</summary>
    void Remove(Delivery delivery);
}
