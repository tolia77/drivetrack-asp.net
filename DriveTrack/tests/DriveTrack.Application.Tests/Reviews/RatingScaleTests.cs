using DriveTrack.Domain.Reviews;

namespace DriveTrack.Application.Tests.Reviews;

/// <summary>
/// FR-98's thresholds, asserted where they live.
/// <para>
/// The point of the class under test is that "four and above is good" is a product rule in Domain
/// rather than a class name chosen in markup (AD-18). A rule in markup is a rule no test can reach;
/// these six cases are what that decision buys.
/// </para>
/// <para>
/// The boundaries are stated from both sides on purpose. A band function is only ever wrong at its
/// edges, and an inclusive bound written exclusively — 4.0 reading as neutral — is a defect that
/// never shows up in the middle of a range and is invisible to a screen test.
/// </para>
/// </summary>
public class RatingScaleTests
{
    [Theory]
    [InlineData(5)]
    [InlineData(4)]
    public void Four_and_above_reads_as_favourable(double rating)
    {
        // Inclusive at 4: an average of exactly four is three fives and a one, which is a driver
        // people are happy with rather than a borderline one.
        Assert.Equal(RatingBand.Favourable, RatingScale.Band(rating));
    }

    [Theory]
    [InlineData(3.9)]
    [InlineData(3)]
    public void Three_up_to_four_reads_as_neutral(double rating)
    {
        // The half a whole-number scale cannot express, and the reason Band takes a double: 3.9 is
        // not a 4, and a function that rounded before it judged would call it one.
        Assert.Equal(RatingBand.Neutral, RatingScale.Band(rating));
    }

    [Theory]
    [InlineData(2.9)]
    [InlineData(1)]
    public void Below_three_reads_as_unfavourable(double rating)
    {
        Assert.Equal(RatingBand.Unfavourable, RatingScale.Band(rating));
    }

    [Fact]
    public void The_scale_the_form_offers_is_the_scale_the_column_permits()
    {
        // The constants restate CK_Reviews_Rating. The column is the authority - a race cannot slip
        // a 6 past it - and this is what makes the validator's 422 and the constraint's refusal the
        // same answer to the same question (NFR-2).
        Assert.Equal(1, RatingScale.Minimum);
        Assert.Equal(5, RatingScale.Maximum);

        // And the value an unfilled form opens on: the top of the scale, because a review is
        // written by somebody who chose to write one and the form must not pre-load a complaint.
        Assert.Equal(RatingScale.Maximum, RatingScale.Default);
        Assert.InRange(RatingScale.Default, RatingScale.Minimum, RatingScale.Maximum);
    }
}
