using DriveTrack.Application.Common;
using FluentValidation;

namespace DriveTrack.Application.Deliveries;

/// <summary>
/// The same rules <see cref="CreateDeliveryCommandValidator"/> applies, over the fields a client
/// actually sends (FR-89, FR-102).
/// <para>
/// A second validator rather than a shared base or a cast, because the two commands are different
/// shapes and FluentValidation is typed to the shape. What is <em>not</em> duplicated is the
/// numbers: the three bounds are read from the create validator's own constants and
/// <see cref="LocationInputValidator"/> is the same instance-per-rule the create path uses, so a
/// column that widens widens for both callers at once. A client's 422 must name the same fields a
/// dispatcher's does — a weight of zero is refused by name on both paths or the form on one of them
/// has nothing to attach the refusal to (NFR-4).
/// </para>
/// <para>
/// Nothing here says anything about a driver, a client or a window. There is no rule to write: the
/// command has no field for any of them, which is the point of it being its own type.
/// </para>
/// </summary>
public sealed class RequestDeliveryCommandValidator : AbstractValidator<RequestDeliveryCommand>
{
    /// <summary>Declares the rules.</summary>
    public RequestDeliveryCommandValidator()
    {
        RuleFor(command => command.Pickup)
            .NotNull().WithMessage(nameof(ErrorCode.COMMON_VALIDATION_FAILED));

        // A separate rule rather than a chained SetValidator, for the reason the create path states
        // it that way: FluentValidation skips a child validator for a null property, so the
        // coordinate rules run against the value and the presence rule runs against its absence.
        RuleFor(command => command.Pickup!)
            .SetValidator(new LocationInputValidator())
            .OverridePropertyName(nameof(RequestDeliveryCommand.Pickup));

        RuleFor(command => command.Dropoff)
            .NotNull().WithMessage(nameof(ErrorCode.COMMON_VALIDATION_FAILED));

        RuleFor(command => command.Dropoff!)
            .SetValidator(new LocationInputValidator())
            .OverridePropertyName(nameof(RequestDeliveryCommand.Dropoff));

        RuleFor(command => command.PackageDetails)
            .NotEmpty().WithMessage(nameof(ErrorCode.COMMON_VALIDATION_FAILED))
            .MaximumLength(CreateDeliveryCommandValidator.PackageDetailsMaximumLength)
                .WithMessage(nameof(ErrorCode.COMMON_VALIDATION_FAILED));

        // Bounded only, as on the create path: FR-17 makes notes free text, and a guess about their
        // shape is how a legitimate instruction gets refused.
        RuleFor(command => command.DeliveryNotes)
            .MaximumLength(CreateDeliveryCommandValidator.DeliveryNotesMaximumLength)
                .WithMessage(nameof(ErrorCode.COMMON_VALIDATION_FAILED));

        RuleFor(command => command.PackageWeightKg)
            .GreaterThan(0m).WithMessage(nameof(ErrorCode.COMMON_VALIDATION_FAILED))
            .LessThanOrEqualTo(CreateDeliveryCommandValidator.PackageWeightMaximum)
                .WithMessage(nameof(ErrorCode.COMMON_VALIDATION_FAILED));
    }
}
