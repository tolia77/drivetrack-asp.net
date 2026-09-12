using DriveTrack.Application.Common;
using DriveTrack.Application.Reviews;
using DriveTrack.Domain.Reviews;
using FluentValidation.Results;

namespace DriveTrack.Application.Tests.Reviews;

/// <summary>
/// The validation rows of story 7.2's edge-case matrix, asserted against the two validators that
/// carry them.
/// <para>
/// Asserted here rather than over HTTP because that is where the rules live and where it is
/// cheapest to be exhaustive: 0, 6, both ends of the scale, an empty comment and a 2001-character
/// one are six assertions against a validator and six container starts against the API. The
/// integration suite still drives one of each end to end, which is what proves the rules are
/// actually reached.
/// </para>
/// <para>
/// Every case states the <em>trimmed</em> text, because trimming is what the service does before it
/// validates: the validator judges the string that will reach the column, and a rule judged against
/// anything else would accept three spaces as a comment and store nothing.
/// </para>
/// <para>
/// Both validators are exercised, and that is not duplication for its own sake. They judge
/// different things — a payload and AD-23's merged state — and an edit is the path where a rule
/// silently going missing has no other symptom: the create path would still refuse a 6, so a screen
/// test would keep passing while <c>PUT</c> wrote one.
/// </para>
/// </summary>
public class ReviewValidationTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void A_rating_at_either_end_of_the_scale_is_accepted(int rating)
    {
        // The bounds are inclusive, and saying so from both sides is what stops an off-by-one
        // turning the two most common verdicts into refusals nobody can explain.
        Assert.Empty(Create(rating, "добре"));
        Assert.Empty(Update(rating, "добре"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(-1)]
    public void A_rating_off_the_scale_is_refused_naming_the_field(int rating)
    {
        // FR-67, and the same answer whichever half of the system catches it: the validator's 422
        // here, CK_Reviews_Rating's 422 through PERSISTENCE_CHECK_VIOLATION if a path ever reached
        // the column without it (NFR-2).
        Assert.Contains(nameof(CreateReviewCommand.Rating), Fields(Create(rating, "добре")));
        Assert.Contains(nameof(ErrorCode.REVIEW_RATING_OUT_OF_RANGE), Keys(Create(rating, "добре")));

        Assert.Contains(nameof(ReviewState.Rating), Fields(Update(rating, "добре")));
        Assert.Contains(nameof(ErrorCode.REVIEW_RATING_OUT_OF_RANGE), Keys(Update(rating, "добре")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void A_review_with_no_text_is_refused_naming_the_field(string? text)
    {
        // An empty box is a refusal to state rather than a value to guess at, which is why the
        // command's Text is nullable and why both shapes land on the same rule.
        Assert.Contains(nameof(CreateReviewCommand.Text), Fields(Create(RatingScale.Default, text)));
        Assert.Contains(
            nameof(ErrorCode.REVIEW_TEXT_REQUIRED),
            Keys(Create(RatingScale.Default, text)));
    }

    [Fact]
    public void A_review_of_nothing_but_spaces_is_refused_as_empty_on_both_write_paths()
    {
        // The raw string a caller sent, not a pre-trimmed one: the trim under test is the
        // contract's, so a test that trimmed its own input would assert nothing about it. A
        // whitespace review that passed would be a verdict nobody can read and nobody wrote.
        Assert.Contains(
            nameof(ErrorCode.REVIEW_TEXT_REQUIRED),
            Keys(new CreateReviewCommandValidator()
                .Validate(new CreateReviewCommand(DeliveryId: 7, RatingScale.Default, "   ").Trimmed())
                .Errors));

        Assert.Contains(
            nameof(ErrorCode.REVIEW_TEXT_REQUIRED),
            Keys(new ReviewStateValidator()
                .Validate(Merged(Optional<string>.Present("   ")))
                .Errors));
    }

    [Fact]
    public void A_review_of_exactly_the_column_width_with_trailing_whitespace_is_accepted_on_both_write_paths()
    {
        // The boundary the two paths used to disagree at: a create trimmed before validating and an
        // edit trimmed only on the way to the column, so this exact string was stored by one and
        // refused by the other with REVIEW_TEXT_TOO_LONG. Both now judge what they store.
        var text = new string('я', CreateReviewCommandValidator.TextMaximumLength) + " \r\n";

        Assert.Empty(new CreateReviewCommandValidator()
            .Validate(new CreateReviewCommand(DeliveryId: 7, RatingScale.Default, text).Trimmed())
            .Errors);

        var merged = Merged(Optional<string>.Present(text));

        Assert.Empty(new ReviewStateValidator().Validate(merged).Errors);

        // And what the service will store is the trimmed string rather than the one that was sent:
        // the merge is what the assignment reads, so the column never sees the whitespace.
        Assert.Equal(CreateReviewCommandValidator.TextMaximumLength, merged.Text!.Length);
    }

    [Fact]
    public void A_review_of_exactly_the_column_width_is_accepted()
    {
        var text = new string('я', CreateReviewCommandValidator.TextMaximumLength);

        Assert.Empty(Create(RatingScale.Default, text));
        Assert.Empty(Update(RatingScale.Default, text));
    }

    [Fact]
    public void A_review_one_character_wider_than_the_column_is_refused_before_the_commit()
    {
        // The validator's limit is ReviewConfiguration's, which is what turns a truncation
        // PostgreSQL would raise into a 422 naming the field (NFR-4).
        var text = new string('я', CreateReviewCommandValidator.TextMaximumLength + 1);

        Assert.Contains(nameof(CreateReviewCommand.Text), Fields(Create(RatingScale.Default, text)));
        Assert.Contains(nameof(ErrorCode.REVIEW_TEXT_TOO_LONG), Keys(Create(RatingScale.Default, text)));

        Assert.Contains(nameof(ReviewState.Text), Fields(Update(RatingScale.Default, text)));
        Assert.Contains(nameof(ErrorCode.REVIEW_TEXT_TOO_LONG), Keys(Update(RatingScale.Default, text)));
    }

    [Fact]
    public void An_edit_that_changes_only_the_rating_is_judged_against_the_stored_text()
    {
        // AD-23's whole point, and the regression this class exists to catch: a validator handed
        // the payload would refuse this edit for carrying no text, when the review it is applied to
        // has plenty. MergedOnto is what fills the absent half in, and it is internal, so this
        // assembly is the only one that can say so.
        var stored = new Review
        {
            DeliveryId = 7,
            ClientId = new Domain.Identity.ClientId(3),
            Rating = 2,
            Text = "було погано",
            CreatedAt = DateTimeOffset.UnixEpoch,
        };

        var merged = new UpdateReviewCommand(Optional<int>.Present(5), Optional<string>.Absent)
            .MergedOnto(stored);

        Assert.Equal(5, merged.Rating);
        Assert.Equal("було погано", merged.Text);
        Assert.Empty(new ReviewStateValidator().Validate(merged).Errors);
    }

    [Fact]
    public void An_edit_cannot_empty_a_review_by_sending_nothing_in_the_box()
    {
        // The other arm of AD-23: absent leaves the text alone, present-and-null is a caller
        // clearing it - and a review with no text is a row the column forbids, so the rule refuses
        // it rather than letting PostgreSQL answer.
        var stored = new Review
        {
            DeliveryId = 7,
            ClientId = new Domain.Identity.ClientId(3),
            Rating = 4,
            Text = "було добре",
            CreatedAt = DateTimeOffset.UnixEpoch,
        };

        var merged = new UpdateReviewCommand(
                Optional<int>.Absent,
                Optional<string>.Present(null))
            .MergedOnto(stored);

        Assert.Equal(4, merged.Rating);
        Assert.Contains(
            nameof(ErrorCode.REVIEW_TEXT_REQUIRED),
            Keys(new ReviewStateValidator().Validate(merged).Errors));
    }

    [Fact]
    public void The_review_page_is_bounded_like_every_other_list_in_the_product()
    {
        // NFR-27: a limit of two million is a 422 naming the field rather than a query the database
        // is asked to run, and a limit of zero is a refusal rather than "give me nothing".
        var validator = new ListReviewsQueryValidator();

        Assert.Empty(validator.Validate(new ListReviewsQuery(0, 1)).Errors);
        Assert.Empty(validator
            .Validate(new ListReviewsQuery(0, ListReviewsQueryValidator.MaximumLimit))
            .Errors);

        Assert.NotEmpty(validator.Validate(new ListReviewsQuery(-1, 10)).Errors);
        Assert.NotEmpty(validator.Validate(new ListReviewsQuery(0, 0)).Errors);
        Assert.NotEmpty(validator
            .Validate(new ListReviewsQuery(0, ListReviewsQueryValidator.MaximumLimit + 1))
            .Errors);
    }

    /// <summary>
    /// AD-23's merge of one edited field onto a stored review, which is the only thing
    /// <see cref="ReviewStateValidator"/> is ever handed — and the place the edit path's trim lives.
    /// </summary>
    private static ReviewState Merged(Optional<string> text) =>
        new UpdateReviewCommand(Optional<int>.Absent, text).MergedOnto(new Review
        {
            DeliveryId = 7,
            ClientId = new Domain.Identity.ClientId(3),
            Rating = RatingScale.Default,
            Text = "було добре",
            CreatedAt = DateTimeOffset.UnixEpoch,
        });

    private static IReadOnlyList<ValidationFailure> Create(int rating, string? text) =>
        new CreateReviewCommandValidator()
            .Validate(new CreateReviewCommand(DeliveryId: 7, rating, text))
            .Errors;

    private static IReadOnlyList<ValidationFailure> Update(int rating, string? text) =>
        new ReviewStateValidator().Validate(new ReviewState(rating, text)).Errors;

    /// <summary>The offending property names, deduplicated.</summary>
    private static string[] Fields(IEnumerable<ValidationFailure> failures) =>
    [
        .. failures.Select(failure => failure.PropertyName).Distinct(StringComparer.Ordinal),
    ];

    /// <summary>
    /// The message keys of a refusal. A validator's message is a resource key rather than a
    /// sentence, and the key is the half a field name cannot show — it is what tells "too long"
    /// from "missing" on a form that only ever sees one offending field.
    /// </summary>
    private static string[] Keys(IEnumerable<ValidationFailure> failures) =>
    [
        .. failures.Select(failure => failure.ErrorMessage).Distinct(StringComparer.Ordinal),
    ];
}
