using System.Globalization;
using DriveTrack.Application.Abstractions;
using DriveTrack.Application.Authorization;
using DriveTrack.Application.Common;
using DriveTrack.Application.Deliveries;
using DriveTrack.Domain.Deliveries;
using DriveTrack.Domain.Identity;
using DriveTrack.Domain.Reviews;
using FluentValidation;

namespace DriveTrack.Application.Reviews;

/// <summary>
/// AD-3's pipeline over a client's verdict on a delivery: load resource → guard → not found →
/// validate → act → commit → map.
/// <para>
/// AD-2: every method calls <see cref="IAccessGuard"/> through the interface, inline in its own
/// body. Folding the two review members into a private helper would read better and would not
/// count — <c>GuardCoverageTests</c> walks each public method's own IL and does not follow a call
/// it makes.
/// </para>
/// <para>
/// <b>What this capability reads that it does not own (AD-24).</b> Nothing through another
/// capability's repository. The delivery a review is about is read through
/// <see cref="IDeliveryService.GetMineAsync"/>, and the parties a moderation page names come from
/// <see cref="IDeliveryService.ListByIdsAsync"/>. There is no <c>unitOfWork.Deliveries</c>,
/// <c>.Drivers</c> or <c>.Users</c> anywhere in this file, and there must not be: the driver a
/// review is about is reached as <c>Review.DeliveryId → Delivery.DriverId</c>, which is precisely
/// the reach the rule is about.
/// </para>
/// <para>
/// Both of those doors are asked once per call, never once per row. <c>ListByIdsAsync</c> is
/// plural for that reason alone: resolving a page's parties a delivery at a time would open a unit
/// of work per row and read a whole role roster twice inside each, so a page bounded at
/// <see cref="ListReviewsQueryValidator.MaximumLimit"/> would cost a hundred transactions and two
/// hundred roster reads. Round trips must not scale with rows - the same rule
/// <see cref="ListDriverRatingsAsync"/> follows on the way out of this capability.
/// </para>
/// <para>
/// <c>ValidatorExtensions.ValidateAndThrowAsync</c> is called in static form, for the reason
/// <c>DeliveryService</c> states: a file in this namespace that imported only
/// <c>FluentValidation</c> would bind to that library's identically named extension, whose
/// exception carries no <see cref="ErrorCode"/> and leaves as a 500 where 422 was meant.
/// </para>
/// </summary>
public sealed class ReviewService(
    IUnitOfWorkFactory unitOfWorkFactory,
    IAccessGuard accessGuard,
    IDeliveryService deliveries,
    TimeProvider timeProvider,
    IValidator<CreateReviewCommand> createValidator,
    IValidator<ReviewState> stateValidator,
    IValidator<ListReviewsQuery> listValidator) : IReviewService
{
    /// <inheritdoc />
    public async Task<AuthoredReviewSummary> CreateAsync(
        CreateReviewCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // A create loads nothing of its own, so the guard is the first step rather than the second.
        // The answer is also the row the review is attached to: an author the caller could name
        // would be an author the caller could forge.
        var clientId = accessGuard.RequireReviewAuthor();

        // AD-24: the delivery comes from the capability that owns it, through the scoped read. A
        // delivery that is not the caller's is answered as a 404 there, so nothing about somebody
        // else's parcel is disclosed by the attempt to review it.
        var delivery = await deliveries.GetMineAsync(command.DeliveryId, cancellationToken);

        if (delivery.Status is not (DeliveryStatus.Delivered or DeliveryStatus.Failed))
        {
            // FR-62: a verdict on a journey that has not happened yet. Refused before validation,
            // because it is a refusal about the delivery the payload named rather than about the
            // rating or the text - and telling an author their text is fine when the whole request
            // is not would be the wrong first answer.
            throw new DomainRuleException(
                ErrorCode.REVIEW_DELIVERY_NOT_COMPLETED,
                "Delivery " + delivery.Id.ToString(CultureInfo.InvariantCulture)
                    + " is " + delivery.Status + " and has not been carried, so it cannot be reviewed.");
        }

        // Trimmed as it is validated, not afterwards: the validator has to judge the string that
        // will actually be stored, or three spaces pass as content and land as nothing. The trim
        // belongs to the contract rather than to this line, so the edit path applies the same one.
        var trimmed = command.Trimmed();

        await ValidatorExtensions.ValidateAndThrowAsync(createValidator, trimmed, cancellationToken);

        await using var unitOfWork = await unitOfWorkFactory.CreateAsync(cancellationToken);

        var review = new Review
        {
            DeliveryId = delivery.Id,
            ClientId = clientId,
            Rating = trimmed.Rating,

            // The validator has proved it is present; the nullable annotation exists because the
            // wire can send anything and the command has to carry it as far as here.
            Text = trimmed.Text!,

            // AD-13: the injected clock, at offset zero. DateTimeOffset.UtcNow here would fail
            // PersistenceContractTests and, more to the point, make the ordering untestable.
            CreatedAt = timeProvider.GetUtcNow(),
        };

        unitOfWork.Reviews.Add(review);

        // No prior "is there already a review" read, and its absence is the point (DR-6, AD-20).
        // Two authors racing both pass such a check; only one passes ix_reviews_delivery_id, and
        // the loser's DbUpdateException leaves here as ConflictException - a 409 - through the one
        // translation point in Infrastructure.
        await unitOfWork.CommitAsync(cancellationToken);

        // Built from the entity the commit populated - the database assigned its id - rather than
        // re-read: a second read would only ask the database to confirm what this scope wrote.
        return Authored(review);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReviewSummary>> ListAsync(
        ListReviewsQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        accessGuard.RequireRole(UserRole.Dispatcher);

        // AD-3's "the predicate comes from the guard", satisfied rather than optimized away. On
        // this route it can only ever be unrestricted - the role check above has already narrowed
        // the caller to the two roles the guard answers unrestricted for - but the scope still
        // travels from the guard into the query, so the day a fourth role is given this list there
        // is no second place deciding what it may see.
        var scope = accessGuard.RequireScope();

        await ValidatorExtensions.ValidateAndThrowAsync(listValidator, query, cancellationToken);

        List<Review> reviews;

        // An explicit block rather than a method-wide `await using`, so the scope is closed before
        // the party reads below open theirs. Those go through another capability's service, which
        // opens a unit of work of its own; nesting them would hold two transactions open across one
        // read for no reason.
        await using (var unitOfWork = await unitOfWorkFactory.CreateAsync(cancellationToken))
        {
            reviews =
            [
                .. await unitOfWork.Reviews.ListAsync(
                    scope,
                    query.Offset,
                    query.Limit,
                    cancellationToken),
            ];
        }

        // AD-24: the parties are the Deliveries capability's answer, not a join this capability
        // writes - and they are asked for once for the whole page rather than once per row.
        //
        // The per-row shape is not merely slower, it is a different order of cost: every
        // IDeliveryService.GetAsync opens its own unit of work and resolves its parties by reading
        // the whole roster of each kind of party the row names, and a reviewed delivery always
        // names both. A page the validator caps at a hundred rows would therefore cost a hundred
        // transactions and two hundred full reads of the accounts table. This is the same
        // round-trips-must-not-scale-with-rows rule the aggregate follows, and it is the reason the
        // door on IDeliveryService is a plural one.
        var byDeliveryId = (await deliveries.ListByIdsAsync(
                [.. reviews.Select(review => review.DeliveryId).Distinct()],
                cancellationToken))
            .ToDictionary(delivery => delivery.Id);

        return
        [
            .. reviews.Select(review =>
            {
                // A delivery that vanished between the two reads - an administrator deleting a
                // terminal parcel mid-page - leaves its review unresolved rather than failing the
                // whole list. The cascade takes the review with it, so what the moderator sees on
                // the next read is one row fewer, not one row wrong.
                var delivery = byDeliveryId.GetValueOrDefault(review.DeliveryId);

                return new ReviewSummary(
                    review.Id,
                    review.DeliveryId,
                    review.Rating,
                    RatingScale.Band(review.Rating),
                    review.Text,
                    review.CreatedAt,
                    delivery?.Client,
                    delivery?.Driver);
            }),
        ];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AuthoredReviewSummary>> ListMineAsync(
        ListReviewsQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        // The author is the scope. RequireScope would do for a client, but it also hands a
        // dispatcher and an admin the unrestricted one - which on this route would quietly answer
        // "every review ever written" under a heading that says "mine".
        var clientId = accessGuard.RequireReviewAuthor();

        await ValidatorExtensions.ValidateAndThrowAsync(listValidator, query, cancellationToken);

        await using var unitOfWork = await unitOfWorkFactory.CreateAsync(cancellationToken);

        // AD-3: the narrowing reaches the WHERE clause rather than filtering a materialized page.
        var reviews = await unitOfWork.Reviews.ListAsync(
            new AccessScope(null, clientId),
            query.Offset,
            query.Limit,
            cancellationToken);

        // No party lookup at all, and nothing to strip: the type has no field a counterparty's name
        // could be written into (AD-17).
        return [.. reviews.Select(Authored)];
    }

    /// <inheritdoc />
    public async Task<AuthoredReviewSummary> UpdateAsync(
        int id,
        UpdateReviewCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        await using var unitOfWork = await unitOfWorkFactory.CreateAsync(cancellationToken);

        // AD-3: load, then guard. The row is read before the decision because the decision is about
        // the row - who wrote it - and nothing about it is disclosed unless the guard passes.
        var review = await unitOfWork.Reviews.GetByIdAsync(id, cancellationToken);

        // A missing review reaches the guard as a null that matches no client, so a caller who may
        // not touch it is refused before learning whether it exists.
        accessGuard.RequireReviewOwner(review?.ClientId);

        if (review is null)
        {
            throw NotFound(id);
        }

        // AD-23: the validator reads the state the review will hold, not the payload - so an edit
        // that changes only the rating is not refused for carrying no text. The merge trims, so the
        // string judged here is the string stored below and a create and an edit judge the same
        // content the same way.
        var merged = command.MergedOnto(review);

        await ValidatorExtensions.ValidateAndThrowAsync(stateValidator, merged, cancellationToken);

        review.Rating = merged.Rating;
        review.Text = merged.Text!;

        await unitOfWork.CommitAsync(cancellationToken);

        return Authored(review);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(int id, CancellationToken cancellationToken)
    {
        await using var unitOfWork = await unitOfWorkFactory.CreateAsync(cancellationToken);

        var review = await unitOfWork.Reviews.GetByIdAsync(id, cancellationToken);

        accessGuard.RequireReviewOwner(review?.ClientId);

        if (review is null)
        {
            throw NotFound(id);
        }

        unitOfWork.Reviews.Remove(review);

        await unitOfWork.CommitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DriverRating>> ListDriverRatingsAsync(
        IReadOnlyCollection<DriverId> driverIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(driverIds);

        // Written before the early return, not after it: an empty roster must take the same
        // decision a full one does, or the guard becomes something a caller can skip by asking
        // about nobody.
        accessGuard.RequireRole(UserRole.Dispatcher);

        if (driverIds.Count == 0)
        {
            return [];
        }

        await using var unitOfWork = await unitOfWorkFactory.CreateAsync(cancellationToken);

        return await unitOfWork.Reviews.ListDriverRatingsAsync(driverIds, cancellationToken);
    }

    private static NotFoundException NotFound(int id) =>
        new(
            ErrorCode.COMMON_NOT_FOUND,
            "No review exists with id " + id.ToString(CultureInfo.InvariantCulture) + ".");

    private static AuthoredReviewSummary Authored(Review review) =>
        new(
            review.Id,
            review.DeliveryId,
            review.Rating,
            RatingScale.Band(review.Rating),
            review.Text,
            review.CreatedAt);
}
