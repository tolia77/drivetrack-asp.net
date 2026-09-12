using DriveTrack.Domain.Identity;

namespace DriveTrack.Application.Reviews;

/// <summary>
/// The Reviews capability (FR-62 to FR-67, FR-98): the only writer of the <c>reviews</c> table, and
/// the owner of the per-driver rating derived from it.
/// <para>
/// AD-24 in both directions, which is the whole of this capability's boundary. It never touches
/// <c>unitOfWork.Deliveries</c>, <c>.Drivers</c> or <c>.Users</c> — the delivery a review is about
/// is read through <c>IDeliveryService</c>, and so are the parties a dispatch-side summary names.
/// Symmetrically, the Drivers capability reads a driver's standing through
/// <see cref="ListDriverRatingsAsync"/> and never through <c>unitOfWork.Reviews</c>: the rating is
/// derived from reviews, so it belongs to whoever owns reviews.
/// </para>
/// <para>
/// Two list methods rather than one with a role branch inside it, for the reason
/// <c>IDeliveryService</c> has two: AD-17 makes "a role that sees less gets a distinct DTO" a type
/// rather than a convention, so the moderation list and the author's own list answer different
/// shapes and are reached by different routes.
/// </para>
/// <para>
/// Every method takes an authorization decision through <see cref="Authorization.IAccessGuard"/>,
/// inline in its own body: nothing here is in <see cref="Authorization.PublicEntryPoints"/>.
/// </para>
/// </summary>
public interface IReviewService
{
    /// <summary>
    /// Writes a client's verdict on one of their own finished deliveries (FR-62, FR-67).
    /// <para>
    /// One review per delivery, and the uniqueness is the database's. There is deliberately no
    /// "does a review already exist" read before the insert: a check that runs before the write is
    /// defeated by a second request running it at the same moment, which is the worked example
    /// DR-6 and AD-20 are about. The loser of a race gets the 409 the unique index produced.
    /// </para>
    /// <para>
    /// Answers <see cref="AuthoredReviewSummary"/>, and that is AD-17 rather than economy: the
    /// caller is a client, and a payload with a driver on it would put the identity FR-27 withholds
    /// one mapping mistake away from their screen.
    /// </para>
    /// </summary>
    /// <exception cref="Common.ForbiddenException">
    /// The caller is anonymous (<c>AUTH_UNAUTHENTICATED</c>, 401); or is an administrator, a
    /// dispatcher or a driver, or is a client whose claims carry no client row id
    /// (<c>AUTH_FORBIDDEN</c>, 403).
    /// </exception>
    /// <exception cref="Common.NotFoundException">
    /// No delivery has that id, or it is not the caller's — the same answer for both, so a client
    /// learns nothing about another client's delivery.
    /// </exception>
    /// <exception cref="Common.DomainRuleException">
    /// The delivery has not reached <c>Delivered</c> or <c>Failed</c>
    /// (<c>REVIEW_DELIVERY_NOT_COMPLETED</c>).
    /// </exception>
    /// <exception cref="Common.ValidationException">The rating is off the scale, or the text is empty or too long.</exception>
    /// <exception cref="Common.ConflictException">
    /// A review already exists for that delivery (<c>PERSISTENCE_UNIQUE_VIOLATION</c>, 409), raised
    /// by <c>ix_reviews_delivery_id</c> at commit rather than by a lookup beforehand.
    /// </exception>
    Task<AuthoredReviewSummary> CreateAsync(
        CreateReviewCommand command,
        CancellationToken cancellationToken);

    /// <summary>
    /// One page of every review, newest first, with both parties to the delivery named (FR-64).
    /// Dispatcher or admin; a client reads their own through <see cref="ListMineAsync"/> and a
    /// driver is refused outright.
    /// </summary>
    /// <exception cref="Common.ForbiddenException">The caller runs neither dispatch nor the system.</exception>
    /// <exception cref="Common.ValidationException">The paging parameters are out of range (NFR-27).</exception>
    Task<IReadOnlyList<ReviewSummary>> ListAsync(
        ListReviewsQuery query,
        CancellationToken cancellationToken);

    /// <summary>
    /// One page of the reviews the caller wrote (FR-63), carrying no party at all. The narrowing
    /// comes from the guard and reaches the query, so paging stays correct for an author whose rows
    /// are not the first hundred (AD-3).
    /// </summary>
    /// <exception cref="Common.ForbiddenException">The caller is not a client with a client row id.</exception>
    /// <exception cref="Common.ValidationException">The paging parameters are out of range.</exception>
    Task<IReadOnlyList<AuthoredReviewSummary>> ListMineAsync(
        ListReviewsQuery query,
        CancellationToken cancellationToken);

    /// <summary>
    /// Changes a review (FR-65). Absent fields are left alone (AD-23); the rules are judged against
    /// the merged state.
    /// <para>
    /// Its author, or an administrator moderating. Answers the party-free shape for both, because
    /// one of the two callers is a client and a type with no party field cannot carry one to them
    /// (AD-17) — a moderator who wants the parties reads the list.
    /// </para>
    /// </summary>
    /// <exception cref="Common.ForbiddenException">
    /// The caller is anonymous (401); or is a client who did not write this review, a dispatcher or
    /// a driver (403).
    /// </exception>
    /// <exception cref="Common.NotFoundException">No review has that id.</exception>
    /// <exception cref="Common.ValidationException">The merged state is one the row may not hold.</exception>
    Task<AuthoredReviewSummary> UpdateAsync(
        int id,
        UpdateReviewCommand command,
        CancellationToken cancellationToken);

    /// <summary>Removes a review (FR-66). Its author, or an administrator moderating.</summary>
    /// <exception cref="Common.ForbiddenException">The caller may not change this review.</exception>
    /// <exception cref="Common.NotFoundException">No review has that id.</exception>
    Task DeleteAsync(int id, CancellationToken cancellationToken);

    /// <summary>
    /// The standing of each of the named drivers (FR-98), derived on demand and stored nowhere
    /// (DR-18).
    /// <para>
    /// The port AD-24 gives the Drivers capability: it owns the driver, this capability owns the
    /// reviews the rating comes from, and neither reaches into the other's table. Batched by
    /// design — a roster asks once for every driver on it rather than once per row.
    /// </para>
    /// <para>
    /// A driver with no reviews is absent from the answer rather than present with a zero, so the
    /// caller can render "no rating" instead of a complaint nobody made.
    /// </para>
    /// </summary>
    /// <param name="driverIds">The drivers being asked about.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="Common.ForbiddenException">The caller runs neither dispatch nor the system.</exception>
    Task<IReadOnlyList<DriverRating>> ListDriverRatingsAsync(
        IReadOnlyCollection<DriverId> driverIds,
        CancellationToken cancellationToken);
}
