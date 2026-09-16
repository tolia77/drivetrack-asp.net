using DriveTrack.Application.Common;
using DriveTrack.Domain.Reviews;
using FluentValidation;

namespace DriveTrack.Application.Reviews;

/// <summary>
/// FR-67's scale and FR-62's text, over a review being written.
/// <para>
/// Both rules are judged over the <em>trimmed</em> text, because trimming is what the service
/// stores: a comment of exactly 2000 characters with a trailing newline would otherwise be refused
/// for being one character too long when the string that reaches the column fits, and <c>"   "</c>
/// would be accepted as content when what lands is empty. The trim belongs to the two contracts —
/// <see cref="CreateReviewCommand.Trimmed"/> and <see cref="UpdateReviewCommand.MergedOnto"/> — so
/// both write paths judge the same content the same way; this class states the rules and the caller
/// states which string they are about.
/// </para>
/// <para>
/// The bounds are <see cref="RatingScale"/>'s rather than two literals, and that is AD-18: the
/// scale is a product rule in Domain, read here and by the band the screen renders. They also
/// restate <c>CK_Reviews_Rating</c> — the column is the authority and a race cannot slip a 6 past
/// it, but stating the bound here is what makes the answer a 422 naming the field either way
/// (NFR-2, NFR-4).
/// </para>
/// <para>
/// There is no rule about the rating being a whole number, and nothing is missing: it is an
/// <see cref="int"/>, so a fractional value cannot be carried as far as here — the adapter refuses
/// it at the model binder.
/// </para>
/// <para>
/// Discovered by <c>AddValidatorsFromAssembly</c>, so there is no registration to add.
/// </para>
/// </summary>
public sealed class CreateReviewCommandValidator : AbstractValidator<CreateReviewCommand>
{
    /// <summary>The longest comment the <c>reviews.text</c> column holds.</summary>
    public const int TextMaximumLength = 2000;

    /// <summary>Declares the rules.</summary>
    public CreateReviewCommandValidator()
    {
        RuleFor(command => command.Rating)
            .InclusiveBetween(RatingScale.Minimum, RatingScale.Maximum)
                .WithMessage(nameof(ErrorCode.REVIEW_RATING_OUT_OF_RANGE));

        RuleFor(command => command.Text)
            .NotEmpty().WithMessage(nameof(ErrorCode.REVIEW_TEXT_REQUIRED))
            .MaximumLength(TextMaximumLength).WithMessage(nameof(ErrorCode.REVIEW_TEXT_TOO_LONG));
    }
}

/// <summary>
/// The same two rules, applied to the state the review will hold rather than to the payload.
/// <para>
/// AD-23: this validator is only ever handed <c>UpdateReviewCommand.MergedOnto</c>'s result, in
/// which both fields are filled in from the stored row — which is what lets an edit that changes
/// only the rating pass without carrying the text again.
/// </para>
/// <para>
/// A second validator rather than a shared one over a shared type, because the two commands are
/// different things: a create names a delivery and an edit cannot. The rules themselves are stated
/// once — the bounds are <see cref="RatingScale"/>'s and the length is
/// <see cref="CreateReviewCommandValidator.TextMaximumLength"/> — so there is one answer to each
/// question even though there are two places that ask.
/// </para>
/// </summary>
public sealed class ReviewStateValidator : AbstractValidator<ReviewState>
{
    /// <summary>Declares the rules.</summary>
    public ReviewStateValidator()
    {
        RuleFor(state => state.Rating)
            .InclusiveBetween(RatingScale.Minimum, RatingScale.Maximum)
                .WithMessage(nameof(ErrorCode.REVIEW_RATING_OUT_OF_RANGE));

        RuleFor(state => state.Text)
            .NotEmpty().WithMessage(nameof(ErrorCode.REVIEW_TEXT_REQUIRED))
            .MaximumLength(CreateReviewCommandValidator.TextMaximumLength)
                .WithMessage(nameof(ErrorCode.REVIEW_TEXT_TOO_LONG));
    }
}

/// <summary>
/// NFR-27's "validated rather than passed through unchecked", over the review list.
/// <para>
/// Every message is an <see cref="ErrorCode"/> name applied per rule, for the reason
/// <c>ListDeliveriesQueryValidator</c> states: a limit of two million is a 422 naming the field
/// rather than a query the database is asked to run.
/// </para>
/// </summary>
public sealed class ListReviewsQueryValidator : AbstractValidator<ListReviewsQuery>
{
    /// <summary>The largest page a caller may ask for, which is also the default page size.</summary>
    public const int MaximumLimit = 100;

    /// <summary>Declares the rules.</summary>
    public ListReviewsQueryValidator()
    {
        // The two paging codes rather than a code per query: Offset and Limit are the same two
        // values on every list in the system, no screen offers a box for either, and a caller who
        // typed them into a URL is served by one sentence naming the bound they broke.
        RuleFor(query => query.Offset)
            .GreaterThanOrEqualTo(0).WithMessage(nameof(ErrorCode.COMMON_PAGING_OFFSET_INVALID));

        RuleFor(query => query.Limit)
            .GreaterThan(0).WithMessage(nameof(ErrorCode.COMMON_PAGING_LIMIT_INVALID))
            .LessThanOrEqualTo(MaximumLimit)
                .WithMessage(nameof(ErrorCode.COMMON_PAGING_LIMIT_INVALID));
    }
}
