using DriveTrack.Application.Common;
using FluentValidation;

namespace DriveTrack.Application.Deliveries;

/// <summary>
/// FR-104's address search, as a request rather than a loose string.
/// <para>
/// Nullable, because the wire can omit it and the query has to carry that absence as far as the
/// refusal — the same shape every other command here takes for the same reason (AD-23's sibling
/// argument on a required field).
/// </para>
/// </summary>
/// <param name="Query">What the dispatcher typed.</param>
public sealed record SearchPlacesQuery(string? Query);

/// <summary>
/// AD-9's explicit validation over the address search.
/// <para>
/// The three-character floor is not cosmetic. Every keystroke in the box would otherwise be a
/// request to a public geocoding service, which is both wasteful and the sort of traffic such a
/// service refuses outright — so the refusal happens here, before any outbound call, and arrives as
/// a 422 naming the field rather than as an unexplained empty list.
/// </para>
/// </summary>
public sealed class SearchPlacesQueryValidator : AbstractValidator<SearchPlacesQuery>
{
    /// <summary>The shortest query that may reach the geocoder.</summary>
    public const int MinimumLength = 3;

    /// <summary>Declares the rules.</summary>
    public SearchPlacesQueryValidator() =>
        RuleFor(query => query.Query)
            .Must(IsLongEnough).WithMessage(nameof(ErrorCode.DELIVERY_PLACE_QUERY_TOO_SHORT));

    /// <summary>
    /// True when the query carries at least <see cref="MinimumLength"/> characters that are not
    /// whitespace. Blank is refused by the same rule as too short: three spaces are not a search.
    /// </summary>
    private static bool IsLongEnough(string? query) =>
        !string.IsNullOrWhiteSpace(query) && query.Trim().Length >= MinimumLength;
}
