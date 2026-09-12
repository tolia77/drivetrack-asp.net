using DriveTrack.Application.Common;
using DriveTrack.Application.Deliveries;
using DriveTrack.Domain.Identity;
using DriveTrack.Domain.Reviews;

namespace DriveTrack.Application.Reviews;

/// <summary>
/// What a client sends to write a review (FR-62, FR-67).
/// <para>
/// The author is not a field, and never can be: it is <c>IAccessGuard.RequireReviewAuthor</c>'s
/// answer. An author the caller could name is an author the caller could forge.
/// </para>
/// </summary>
/// <param name="DeliveryId">The delivery being reviewed. One review per delivery (DR-6).</param>
/// <param name="Rating">The score, 1 to 5 (FR-67).</param>
/// <param name="Text">
/// The written comment. Nullable because a caller can leave a box empty, and an empty box is a
/// refusal to state rather than a value to guess at — the validator turns it into a 422 naming the
/// field.
/// </param>
public sealed record CreateReviewCommand(int DeliveryId, int Rating, string? Text)
{
    /// <summary>
    /// The command as it will actually be stored: the comment with the caller's stray whitespace
    /// taken off.
    /// <para>
    /// The validator has to judge this rather than the payload, or <c>"   "</c> passes as content
    /// and lands as nothing, and a comment of exactly the column's width with a trailing newline is
    /// refused for being one character too long when the string that reaches the column fits. It is
    /// a method here rather than a line in the service so that the edit path can apply the same one
    /// — the two paths judging the same content differently is the defect this exists to prevent.
    /// </para>
    /// </summary>
    internal CreateReviewCommand Trimmed() => this with { Text = Text?.Trim() };
}

/// <summary>
/// What an author or a moderator sends to change a review (FR-65).
/// <para>
/// AD-23: both fields are <see cref="Optional{T}"/>, so an edit that only changes the rating leaves
/// the text alone rather than clearing it. Neither field is clearable — a review with no text and a
/// review with no rating are both rows the schema forbids — which is why the merged state below is
/// what the validator judges rather than this.
/// </para>
/// <para>
/// The delivery is deliberately absent. A review is keyed one-to-one on the delivery it is about;
/// moving one to another delivery is deleting it and writing another.
/// </para>
/// </summary>
/// <param name="Rating">The new score, or absent to leave it alone.</param>
/// <param name="Text">The new comment, or absent to leave it alone.</param>
public sealed record UpdateReviewCommand(Optional<int> Rating, Optional<string> Text)
{
    /// <summary>
    /// The command with every absent field filled in from the stored row: the state the review will
    /// hold afterwards.
    /// <para>
    /// AD-23 makes this the thing the validator reads. Validating the payload instead would refuse
    /// an edit that only changes the rating, because it carries no text.
    /// </para>
    /// <para>
    /// The text is trimmed here, exactly as <see cref="CreateReviewCommand.Trimmed"/> trims a
    /// create's. The merged state is what the validator judges <em>and</em> what the service
    /// stores, so trimming anywhere later would let the two write paths judge the same content
    /// differently — a comment of exactly the column's width with a trailing newline accepted on
    /// create and refused on edit.
    /// </para>
    /// </summary>
    /// <param name="review">The stored row the edit will be applied to.</param>
    internal ReviewState MergedOnto(Review review)
    {
        ArgumentNullException.ThrowIfNull(review);

        return new ReviewState(Rating.Or(review.Rating), Text.Or(review.Text)?.Trim());
    }
}

/// <summary>
/// The state a review will hold once an edit is applied — AD-23's merged shape, and the only thing
/// <c>ReviewStateValidator</c> is ever handed.
/// </summary>
/// <param name="Rating">The score the row will carry.</param>
/// <param name="Text">The comment the row will carry, already trimmed by the merge that built this.</param>
public sealed record ReviewState(int Rating, string? Text);

/// <summary>
/// NFR-27's paging over a review list, expressed as a request rather than as two loose integers —
/// the same shape <see cref="ListDeliveriesQuery"/> takes, because the rule is the same rule.
/// <para>
/// There is no scope here. The caller does not get to say whose rows it wants: that answer comes
/// from the guard and reaches the repository as a separate argument (AD-3).
/// </para>
/// </summary>
/// <param name="Offset">Rows to skip. Zero is the first page.</param>
/// <param name="Limit">Rows to take, at most <see cref="ListReviewsQueryValidator.MaximumLimit"/>.</param>
public sealed record ListReviewsQuery(int Offset, int Limit);

/// <summary>
/// A review as dispatch and moderation read one (FR-64, FR-65): the row, the judgement it carries,
/// and both parties to the delivery it is about.
/// <para>
/// AD-17: a distinct type from <see cref="AuthoredReviewSummary"/> rather than the same type with
/// the parties nulled out. The client who wrote a review must never learn which driver carried
/// their parcel, and the only way to make that impossible to regress by editing a mapping is for
/// the shape they receive to have no field a party could be written into.
/// </para>
/// </summary>
/// <param name="Id">The review row's id.</param>
/// <param name="DeliveryId">The delivery reviewed.</param>
/// <param name="Rating">The score, 1 to 5.</param>
/// <param name="Band">
/// How that score reads (FR-98), decided by <see cref="RatingScale.Band"/> in Domain rather than by
/// a class name in markup (AD-18).
/// </param>
/// <param name="Text">The written comment.</param>
/// <param name="CreatedAt">When it was written, at offset zero (AD-13).</param>
/// <param name="Client">The client who wrote it, or null when the delivery names none.</param>
/// <param name="Driver">The driver it is about, or null when the delivery was never assigned.</param>
public sealed record ReviewSummary(
    int Id,
    int DeliveryId,
    int Rating,
    RatingBand Band,
    string Text,
    DateTimeOffset CreatedAt,
    DeliveryParty? Client,
    DeliveryParty? Driver);

/// <summary>
/// A review as the person who wrote it reads one (FR-63), and as a moderator gets it back after an
/// edit: everything <see cref="ReviewSummary"/> carries, minus both parties.
/// <para>
/// AD-17 again, and this is the type the rule is actually about. There is no field here a driver's
/// name could be put in and no <c>driverId</c> to correlate on, so a client reading their own
/// reviews cannot learn who carried their parcel however the mapping is later edited.
/// </para>
/// </summary>
/// <param name="Id">The review row's id.</param>
/// <param name="DeliveryId">
/// The delivery reviewed — the caller's own, by construction: they could not have written this
/// otherwise. It is what lets their own list say which parcel each verdict is about.
/// </param>
/// <param name="Rating">The score, 1 to 5.</param>
/// <param name="Band">How that score reads (FR-98).</param>
/// <param name="Text">The written comment.</param>
/// <param name="CreatedAt">When it was written, at offset zero (AD-13).</param>
public sealed record AuthoredReviewSummary(
    int Id,
    int DeliveryId,
    int Rating,
    RatingBand Band,
    string Text,
    DateTimeOffset CreatedAt);

/// <summary>
/// One driver's standing, derived on demand from the reviews of the deliveries they carried
/// (FR-98, DR-18).
/// <para>
/// Never a column on <c>drivers</c>. A stored average is a second answer that goes stale the moment
/// a review is written, edited or deleted, and the original system's named defect is precisely that
/// ratings existed per review and were aggregated nowhere — a stored copy would have been the same
/// defect with a cache in front of it.
/// </para>
/// <para>
/// A driver with no reviews has no row here at all, rather than a row of zeroes. Absence is what
/// the roster renders its "no value" text for; a zero would sort an unrated driver below every
/// rated one and read as a complaint nobody made.
/// </para>
/// </summary>
/// <param name="DriverId">The driver row the reviews were reached through.</param>
/// <param name="Average">The mean of every rating, unrounded.</param>
/// <param name="ReviewCount">How many reviews it was drawn from.</param>
public sealed record DriverRating(DriverId DriverId, double Average, int ReviewCount);
