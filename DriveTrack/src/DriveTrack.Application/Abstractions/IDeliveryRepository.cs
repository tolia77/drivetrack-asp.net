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
    /// One delivery, but only if <paramref name="scope"/> admits it — otherwise null, whether the
    /// row is missing or merely somebody else's (FR-26, FR-27).
    /// </summary>
    /// <remarks>
    /// A distinctly named method rather than an overload of <see cref="GetByIdAsync"/>, and the
    /// reason is the note above it: the delete path depends on that one being tracked and
    /// un-included, and an overload invites a caller to reach for whichever signature is nearest.
    /// This one is <c>AsNoTracking</c>, because every caller of it reads.
    /// <para>
    /// The narrowing is a <c>WHERE</c> rather than a check after the fact, for AD-3's reason and one
    /// more: a caller who is told "not found" learns nothing about a row they may not see, so the
    /// non-disclosing 404 the requirements ask for falls out of the query instead of having to be
    /// remembered at each call site.
    /// </para>
    /// </remarks>
    /// <param name="id">The delivery being addressed.</param>
    /// <param name="scope">The guard's answer to "whose rows may this caller see".</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<Delivery?> FindVisibleAsync(int id, AccessScope scope, CancellationToken cancellationToken);

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
    /// The deliveries named by <paramref name="ids"/> that <paramref name="scope"/> admits, in one
    /// round trip.
    /// </summary>
    /// <remarks>
    /// Not a page and not ordered: the caller already holds the rows it is asking about — a page of
    /// reviews, each naming the delivery it is written on — and reads these to name their parties.
    /// It exists so that a capability needing N rows of somebody else's data asks once instead of N
    /// times; the mirror of <see cref="IReviewRepository.ListDriverRatingsAsync"/>, which the
    /// Drivers capability reads ratings through for the same reason.
    /// <para>
    /// The scope is a parameter for AD-3's reason, unchanged by the ids being explicit: a row the
    /// caller may not see must be absent from the answer rather than filtered out of it afterwards.
    /// An id the scope excludes is simply missing from the result, exactly as an id nobody ever
    /// wrote is.
    /// </para>
    /// <para>
    /// An empty <paramref name="ids"/> answers an empty list without asking the database; every
    /// caller reaching here with nothing to look up is a caller whose own page was empty.
    /// </para>
    /// </remarks>
    /// <param name="scope">The guard's answer to "whose rows may this caller see".</param>
    /// <param name="ids">The delivery ids being resolved. Duplicates are harmless.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<Delivery>> ListByIdsAsync(
        AccessScope scope,
        IReadOnlyCollection<int> ids,
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
