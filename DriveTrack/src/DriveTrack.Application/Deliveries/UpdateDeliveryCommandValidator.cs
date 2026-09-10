using DriveTrack.Application.Common;
using FluentValidation;

namespace DriveTrack.Application.Deliveries;

/// <summary>
/// FR-22's rules, and deliberately the same rules as <see cref="CreateDeliveryCommandValidator"/>:
/// a delivery that could be created is a delivery that may exist, so an edit producing the same
/// state must be accepted and an edit producing a state a create would refuse must not be.
/// <para>
/// This validator is only ever handed <see cref="UpdateDeliveryCommand.MergedOnto"/>'s result, in
/// which every field is present. That is AD-23's rule about validation, and it is what lets the
/// rules below read the value without asking whether the caller sent it — and what makes the
/// window rule correct, since an edit that moves only the earliest bound has to be judged against
/// the latest bound already stored.
/// </para>
/// <para>
/// Each rule overrides its property name so a field error names <c>PackageWeightKg</c> rather than
/// <c>PackageWeightKg.Value</c>: the adapter turns that name into the one the caller's payload
/// used, and a form can only attach a message to an input it actually has (NFR-4).
/// </para>
/// </summary>
public sealed class UpdateDeliveryCommandValidator : AbstractValidator<UpdateDeliveryCommand>
{
    /// <summary>Declares the rules.</summary>
    public UpdateDeliveryCommandValidator()
    {
        // Present-null is refused rather than treated as a clear: a delivery has to be collected
        // somewhere and delivered somewhere, so "no pickup" is not a state the row may hold.
        RuleFor(command => command.Pickup.Value)
            .NotNull().WithMessage(nameof(ErrorCode.COMMON_VALIDATION_FAILED))
            .OverridePropertyName(nameof(UpdateDeliveryCommand.Pickup));

        RuleFor(command => command.Pickup.Value!)
            .SetValidator(new LocationInputValidator())
            .OverridePropertyName(nameof(UpdateDeliveryCommand.Pickup));

        RuleFor(command => command.Dropoff.Value)
            .NotNull().WithMessage(nameof(ErrorCode.COMMON_VALIDATION_FAILED))
            .OverridePropertyName(nameof(UpdateDeliveryCommand.Dropoff));

        RuleFor(command => command.Dropoff.Value!)
            .SetValidator(new LocationInputValidator())
            .OverridePropertyName(nameof(UpdateDeliveryCommand.Dropoff));

        RuleFor(command => command.PackageDetails.Value)
            .NotEmpty().WithMessage(nameof(ErrorCode.COMMON_VALIDATION_FAILED))
            .MaximumLength(CreateDeliveryCommandValidator.PackageDetailsMaximumLength)
                .WithMessage(nameof(ErrorCode.COMMON_VALIDATION_FAILED))
            .OverridePropertyName(nameof(UpdateDeliveryCommand.PackageDetails));

        // No NotEmpty: the notes column is nullable, so a present-null is a genuine clear and
        // every value the wire can carry is one the delivery may hold (FR-17).
        RuleFor(command => command.DeliveryNotes.Value)
            .MaximumLength(CreateDeliveryCommandValidator.DeliveryNotesMaximumLength)
                .WithMessage(nameof(ErrorCode.COMMON_VALIDATION_FAILED))
            .OverridePropertyName(nameof(UpdateDeliveryCommand.DeliveryNotes));

        RuleFor(command => command.PackageWeightKg.Value)
            .GreaterThan(0m).WithMessage(nameof(ErrorCode.COMMON_VALIDATION_FAILED))
            .LessThanOrEqualTo(CreateDeliveryCommandValidator.PackageWeightMaximum)
                .WithMessage(nameof(ErrorCode.COMMON_VALIDATION_FAILED))
            .OverridePropertyName(nameof(UpdateDeliveryCommand.PackageWeightKg));

        // Judged over the merged pair, which is the whole reason the merge feeds the validator:
        // an edit that sends only a new earliest bound has to be checked against the latest bound
        // the row already carries, and the payload alone does not know it.
        RuleFor(command => command.WindowLatestAt.Value)
            .Must((command, latest) => CreateDeliveryCommandValidator.IsOrderedWindow(
                command.WindowEarliestAt.Value,
                latest))
                .WithMessage(nameof(ErrorCode.COMMON_VALIDATION_FAILED))
            .OverridePropertyName(nameof(UpdateDeliveryCommand.WindowLatestAt));
    }
}
