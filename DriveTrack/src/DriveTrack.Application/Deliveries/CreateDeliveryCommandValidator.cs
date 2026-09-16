using DriveTrack.Application.Common;
using FluentValidation;

namespace DriveTrack.Application.Deliveries;

/// <summary>
/// FR-14, FR-16, FR-100 and FR-102's rules for a new delivery, applied to the state the row will
/// hold.
/// <para>
/// Two of these rules are also check constraints — <c>ck_deliveries_package_weight_kg</c> and
/// <c>ck_deliveries_delivery_window</c> — and that is not duplication. The constraint is the race
/// backstop and surfaces through <c>PostgresConstraintTranslator</c> as
/// <c>PERSISTENCE_CHECK_VIOLATION</c> with no field list at all, because SQLSTATE 23514 carries a
/// constraint name and not a property. The validator is what turns the same refusal into a 422 the
/// form can attach to the input that produced it (NFR-4).
/// </para>
/// <para>
/// The lengths match the columns (<c>package_details</c> and <c>delivery_notes</c> 1000) and the
/// weight's upper bound matches the magnitude <c>numeric(10, 3)</c> can hold, so a value the
/// database would truncate or overflow on is refused here first, by name.
/// </para>
/// <para>
/// Scale is deliberately not policed. <c>numeric(10, 3)</c> rounds a weight with more than three
/// decimals rather than refusing it, so 12.5004 is stored as 12.5 and nobody is told — an accepted
/// rounding of a figure nobody weighs to a tenth of a gram. The one case that is not merely
/// cosmetic is a weight under 0.0005, which rounds to zero and is refused by
/// <c>ck_deliveries_package_weight_kg</c> with no field name attached; that is a 422 carrying the
/// constraint's own generic message rather than the friendly one this validator gives, and it is
/// left that way
/// because a scale rule here would be a second, narrower definition of "a weight" than the column
/// has.
/// </para>
/// </summary>
public sealed class CreateDeliveryCommandValidator : AbstractValidator<CreateDeliveryCommand>
{
    /// <summary>Matching the <c>deliveries.package_details</c> column.</summary>
    public const int PackageDetailsMaximumLength = 1000;

    /// <summary>Matching the <c>deliveries.delivery_notes</c> column.</summary>
    public const int DeliveryNotesMaximumLength = 1000;

    /// <summary>
    /// The largest weight <c>numeric(10, 3)</c> can hold. Stated so an over-wide value is a 422
    /// naming the field rather than a numeric overflow at the commit.
    /// </summary>
    public const decimal PackageWeightMaximum = 9_999_999.999m;

    /// <summary>Declares the rules.</summary>
    public CreateDeliveryCommandValidator()
    {
        RuleFor(command => command.Pickup)
            .NotNull().WithMessage(nameof(ErrorCode.COMMON_FIELD_REQUIRED));

        // A separate rule rather than a chained SetValidator, so the coordinate rules run against
        // the value and the presence rule runs against its absence. FluentValidation skips a child
        // validator for a null property, so a missing location is reported once, above.
        RuleFor(command => command.Pickup!)
            .SetValidator(new LocationInputValidator())
            .OverridePropertyName(nameof(CreateDeliveryCommand.Pickup));

        RuleFor(command => command.Dropoff)
            .NotNull().WithMessage(nameof(ErrorCode.COMMON_FIELD_REQUIRED));

        RuleFor(command => command.Dropoff!)
            .SetValidator(new LocationInputValidator())
            .OverridePropertyName(nameof(CreateDeliveryCommand.Dropoff));

        RuleFor(command => command.PackageDetails)
            .NotEmpty().WithMessage(nameof(ErrorCode.COMMON_FIELD_REQUIRED))
            .MaximumLength(PackageDetailsMaximumLength)
                .WithMessage(nameof(ErrorCode.DELIVERY_PACKAGE_DETAILS_TOO_LONG));

        // Bounded only. No format rule and no required note: FR-17 makes notes free text, and a
        // guess about their shape is how a legitimate instruction gets refused.
        RuleFor(command => command.DeliveryNotes)
            .MaximumLength(DeliveryNotesMaximumLength)
                .WithMessage(nameof(ErrorCode.DELIVERY_NOTES_TOO_LONG));

        RuleFor(command => command.PackageWeightKg)
            .GreaterThan(0m).WithMessage(nameof(ErrorCode.DELIVERY_PACKAGE_WEIGHT_NOT_POSITIVE))
            .LessThanOrEqualTo(PackageWeightMaximum)
                .WithMessage(nameof(ErrorCode.DELIVERY_PACKAGE_WEIGHT_TOO_LARGE));

        // FR-100: either bound may stand alone and both may be absent; only an inverted pair is
        // refused, which is exactly what the check constraint says. The message names the latest
        // bound because that is the field a dispatcher would move to fix it.
        RuleFor(command => command.WindowLatestAt)
            .Must((command, latest) => IsOrderedWindow(command.WindowEarliestAt, latest))
                .WithMessage(nameof(ErrorCode.DELIVERY_WINDOW_ENDS_BEFORE_IT_STARTS));
    }

    /// <summary>
    /// FR-100's window rule, in one place because <see cref="UpdateDeliveryCommandValidator"/> has
    /// to apply exactly the same one: a window a create would refuse must not be reachable through
    /// an edit, and a window a create would accept must not be refused by one.
    /// </summary>
    /// <param name="earliest">The earliest bound, or null.</param>
    /// <param name="latest">The latest bound, or null.</param>
    /// <returns>True when the pair is one the row may hold.</returns>
    public static bool IsOrderedWindow(DateTimeOffset? earliest, DateTimeOffset? latest) =>
        earliest is not { } from || latest is not { } to || from < to;
}
