namespace DriveTrack.Domain.Reviews;

/// <summary>
/// How a rating reads as a judgement (FR-98): favourable, neutral or unfavourable.
/// <para>
/// A band rather than a colour, and that is AD-18/AD-28 rather than fastidiousness. "Four and above
/// is good" is a product rule, and a component that chose <c>text-success</c> for it would be a
/// place where a product rule lived in markup — invisible to every test of the rule, and quietly
/// different on the next screen that renders a rating.
/// </para>
/// </summary>
public enum RatingBand
{
    /// <summary>The driver is well thought of.</summary>
    Favourable,

    /// <summary>Neither praise nor complaint.</summary>
    Neutral,

    /// <summary>The driver is poorly thought of.</summary>
    Unfavourable,
}

/// <summary>
/// The rating scale itself (FR-67, FR-98): its bounds, the value a form starts on, and the
/// thresholds that turn a number into a <see cref="RatingBand"/>.
/// <para>
/// Domain rather than Application, because both users of it are: a single review's own rating and
/// the average derived from many. One scale, read the same way by a stored row and by an aggregate
/// that never existed as a row — which is the whole of the original system's named defect, where
/// the rating was stored per review and aggregated nowhere.
/// </para>
/// <para>
/// <see cref="Minimum"/> and <see cref="Maximum"/> restate the <c>CK_Reviews_Rating</c> check
/// constraint. The column is the authority — a race cannot slip a 6 past it — and stating the bound
/// here is what lets a validator refuse one as a 422 naming the field instead of letting PostgreSQL
/// answer it (NFR-2, NFR-4).
/// </para>
/// </summary>
public static class RatingScale
{
    /// <summary>The lowest rating a review may carry.</summary>
    public const int Minimum = 1;

    /// <summary>The highest rating a review may carry.</summary>
    public const int Maximum = 5;

    /// <summary>
    /// What an unfilled form offers. The top of the scale rather than the middle: a review is
    /// written by a client who chose to write one, and the form must not put a complaint in front
    /// of somebody who only wanted to say thank you.
    /// </summary>
    public const int Default = 5;

    /// <summary>At and above this, a rating reads as favourable.</summary>
    public const double FavourableFrom = 4;

    /// <summary>At and above this — and below <see cref="FavourableFrom"/> — a rating reads as neutral.</summary>
    public const double NeutralFrom = 3;

    /// <summary>
    /// The band a rating falls in.
    /// </summary>
    /// <param name="rating">
    /// A stored review's whole rating, or an average of several — which is why the parameter is a
    /// <see cref="double"/> rather than an <see cref="int"/>. An aggregate of 3.9 is not a 4, and a
    /// scale that could only judge whole numbers would have to round before it judged.
    /// </param>
    /// <returns>The judgement, total over the whole of the real line.</returns>
    public static RatingBand Band(double rating)
    {
        if (rating >= FavourableFrom)
        {
            return RatingBand.Favourable;
        }

        if (rating >= NeutralFrom)
        {
            return RatingBand.Neutral;
        }

        // Everything below the neutral threshold, including values the scale does not permit at
        // all: a band is a reading of a number rather than a second place the bounds are enforced.
        return RatingBand.Unfavourable;
    }
}
