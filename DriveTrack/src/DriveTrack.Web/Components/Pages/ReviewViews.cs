using System.Globalization;
using DriveTrack.Domain.Identity;
using DriveTrack.Domain.Reviews;

namespace DriveTrack.Web.Components.Pages;

/// <summary>
/// The decisions the reviews screen and the driver roster take about a rating, lifted out of
/// markup so they can be read back.
/// <para>
/// AD-18 is why the class names are here rather than in a <c>class="@(...)"</c> expression: a
/// threshold written into markup is a product rule living where no test of the rule would look.
/// <see cref="RatingScale.Band"/> in Domain decides which band a number falls in; this only says
/// which class each band wears, and the class resolves to a <c>--dt-rating-*</c> token. No literal
/// colour, and no second copy of "four and above is good" (AD-28, NFR-29).
/// </para>
/// <para>
/// The role predicates say what the screen draws, never what a caller may do.
/// <c>IAccessGuard</c> inside <c>IReviewService</c> refuses whatever this decided to render, and it
/// runs whether or not a button was ever put on the page (FR-12). They are lifted out of markup for
/// the same reason the class names are: a static render dispatches no events, so a rule written
/// inside an <c>@if</c> is a rule no test in this solution can reach.
/// </para>
/// </summary>
internal static class ReviewViews
{
    /// <summary>The class a rating nobody has given wears — muted, and never one of the three bands.</summary>
    public const string NoRatingClass = "dt-rating dt-rating-none";

    /// <summary>The class for a band.</summary>
    /// <param name="band">The judgement <see cref="RatingScale.Band"/> reached.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A band with no class. Total over the enum like the navigation table: a fourth band added
    /// without a decision about how it looks fails here rather than rendering unstyled.
    /// </exception>
    public static string ClassFor(RatingBand band) => band switch
    {
        RatingBand.Favourable => "dt-rating dt-rating-favourable",
        RatingBand.Neutral => "dt-rating dt-rating-neutral",
        RatingBand.Unfavourable => "dt-rating dt-rating-unfavourable",
        _ => throw new ArgumentOutOfRangeException(
            nameof(band),
            band,
            "No rating class is defined for this band."),
    };

    /// <summary>
    /// The rating this screen is actually showing: the raw average narrowed to the one decimal
    /// place a reader ever sees.
    /// <para>
    /// <see cref="RatingScale.Band"/> judges exactly the number it is handed and rounds nothing —
    /// an aggregate of 3.96 is not a four to it, and that is the Domain rule. What is decided here
    /// is a different question: which number this screen is talking about. A page that displayed
    /// 3.96 as <c>4</c> while asking Domain about 3.96 would be asking about a number the reader
    /// cannot see, and the four would come out wearing the middling colour. So the display value is
    /// settled first, once, and both <see cref="Format(double)"/> and
    /// <see cref="ClassFor(double)"/> then speak about that one.
    /// </para>
    /// </summary>
    /// <param name="rating">The score, as the aggregate produced it.</param>
    public static double Rounded(double rating) =>
        Math.Round(rating, 1, MidpointRounding.AwayFromZero);

    /// <summary>The class for a rating, whether a single review's or an average of many.</summary>
    /// <param name="rating">The score.</param>
    public static string ClassFor(double rating) => ClassFor(RatingScale.Band(Rounded(rating)));

    /// <summary>
    /// A rating as a reader sees it: whole numbers without a decimal part, averages to one place.
    /// <para>
    /// Formatted against the current culture rather than the invariant one (NFR-15), so 3.5 reads
    /// as <c>3,5</c> the way every other number on a Ukrainian screen does.
    /// </para>
    /// </summary>
    /// <param name="rating">The score.</param>
    public static string Format(double rating) =>
        Rounded(rating).ToString("0.#", CultureInfo.CurrentCulture);

    /// <summary>
    /// True when this caller writes reviews — a client, and nobody else (FR-62).
    /// <para>
    /// The screen agrees with <c>IAccessGuard.RequireReviewAuthor</c> rather than deciding
    /// anything. An administrator is the row a reader will take for a mistake: the PRD retired
    /// FR-97, so they moderate reviews and never author one, and offering them a compose action
    /// would be offering one that can only refuse.
    /// </para>
    /// </summary>
    /// <param name="role">The caller's single role (AD-4).</param>
    public static bool Authors(UserRole role) => role == UserRole.Client;

    /// <summary>
    /// True when this caller reads the whole collection with both parties named (FR-64): a
    /// dispatcher or an administrator.
    /// </summary>
    /// <param name="role">The caller's single role.</param>
    public static bool ReadsEveryReview(UserRole role) =>
        role is UserRole.Dispatcher or UserRole.Admin;

    /// <summary>
    /// True when this caller may change somebody else's review (FR-65, FR-66): an administrator
    /// only. A dispatcher reads the collection and never writes to it.
    /// </summary>
    /// <param name="role">The caller's single role.</param>
    public static bool Moderates(UserRole role) => role == UserRole.Admin;
}
