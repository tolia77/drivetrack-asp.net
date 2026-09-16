using FluentValidation;

namespace DriveTrack.Application.Common;

/// <summary>
/// A point as a caller sends one: two nullable numbers and nothing else.
/// <para>
/// The obvious shape would have been <see cref="MapLocation"/> itself, and it is exactly the wrong
/// one on the way in. <see cref="MapLocation"/>'s constructor throws
/// <see cref="ArgumentOutOfRangeException"/> for a coordinate no map could show, and a constructor
/// that throws during model binding lands in the suppressed model state and leaves as a 500 — so a
/// latitude of 91 would answer "unexpected error" where NFR-4 promises a 422 naming the field.
/// Binding to this pair instead lets <see cref="LocationInputValidator"/> refuse the same values
/// with a field name attached, and <see cref="MapLocation"/> stays the shape that leaves.
/// </para>
/// <para>
/// The two are nullable so an omitted coordinate is absent rather than zero: <c>0, 0</c> is a real
/// point in the Atlantic, and a payload that forgot its longitude must not quietly become one.
/// </para>
/// </summary>
/// <param name="Latitude">Latitude in decimal degrees, or null when the caller sent none.</param>
/// <param name="Longitude">Longitude in decimal degrees, or null when the caller sent none.</param>
public sealed record LocationInput(double? Latitude, double? Longitude);

/// <summary>
/// The ranges <see cref="MapLocation"/> enforces, restated where a failure can carry a field name
/// (NFR-4). The two are deliberately the same rule in two places: this one refuses politely, and
/// the constructor refuses absolutely, so a coordinate that reached the domain without passing
/// here would still not become a marker nobody can find.
/// </summary>
public sealed class LocationInputValidator : AbstractValidator<LocationInput>
{
    /// <summary>Declares the rules.</summary>
    public LocationInputValidator()
    {
        // Each range is written as a positive test on a value that is present, so a null is
        // reported once - by NotNull - rather than twice by two rules that disagree about whose
        // job it was. The comparison is a positive range rather than two negations for the reason
        // MapLocation's is a negated one: NaN fails every comparison, so `value >= -90` already
        // rejects it instead of letting it through as "not greater than 90".
        RuleFor(location => location.Latitude)
            .NotNull().WithMessage(nameof(ErrorCode.COMMON_FIELD_REQUIRED))
            .Must(latitude => latitude is not { } value || (value >= -90 && value <= 90))
                .WithMessage(nameof(ErrorCode.COMMON_COORDINATE_OUT_OF_RANGE));

        RuleFor(location => location.Longitude)
            .NotNull().WithMessage(nameof(ErrorCode.COMMON_FIELD_REQUIRED))
            .Must(longitude => longitude is not { } value || (value >= -180 && value <= 180))
                .WithMessage(nameof(ErrorCode.COMMON_COORDINATE_OUT_OF_RANGE));
    }
}
