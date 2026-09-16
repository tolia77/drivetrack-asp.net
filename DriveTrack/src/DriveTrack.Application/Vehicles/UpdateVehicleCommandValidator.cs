using DriveTrack.Application.Common;
using FluentValidation;

namespace DriveTrack.Application.Vehicles;

/// <summary>
/// FR-42's rules, and deliberately the same rules as <see cref="CreateVehicleCommandValidator"/>:
/// a vehicle that could be created is a vehicle that may exist, so an edit that produces the same
/// state must be accepted and an edit that produces a state a create would refuse must not be.
/// <para>
/// This validator is only ever handed <see cref="UpdateVehicleCommand.MergedOnto"/>'s result, in
/// which every field is present. That is AD-23's rule about validation, and it is what lets the
/// rules below read the value without asking whether the caller sent it.
/// </para>
/// <para>
/// Each rule overrides its property name so a field error names <c>LicensePlate</c> rather than
/// <c>LicensePlate.Value</c>: the adapter turns that name into the one the caller's payload used,
/// and a form can only attach a message to an input it actually has (NFR-4).
/// </para>
/// </summary>
public sealed class UpdateVehicleCommandValidator : AbstractValidator<UpdateVehicleCommand>
{
    /// <summary>Declares the rules.</summary>
    public UpdateVehicleCommandValidator()
    {
        RuleFor(command => command.Model.Value)
            .NotEmpty().WithMessage(nameof(ErrorCode.COMMON_FIELD_REQUIRED))
            .MaximumLength(CreateVehicleCommandValidator.ModelMaximumLength)
                .WithMessage(nameof(ErrorCode.FLEET_MODEL_TOO_LONG))
            .OverridePropertyName(nameof(UpdateVehicleCommand.Model));

        RuleFor(command => command.LicensePlate.Value)
            .NotEmpty().WithMessage(nameof(ErrorCode.COMMON_FIELD_REQUIRED))
            .MaximumLength(CreateVehicleCommandValidator.LicensePlateMaximumLength)
                .WithMessage(nameof(ErrorCode.FLEET_LICENSE_PLATE_TOO_LONG))
            .OverridePropertyName(nameof(UpdateVehicleCommand.LicensePlate));

        RuleFor(command => command.CapacityKg.Value)
            .GreaterThan(0m).WithMessage(nameof(ErrorCode.FLEET_CAPACITY_NOT_POSITIVE))
            .LessThanOrEqualTo(CreateVehicleCommandValidator.CapacityMaximum)
                .WithMessage(nameof(ErrorCode.FLEET_CAPACITY_TOO_LARGE))
            .OverridePropertyName(nameof(UpdateVehicleCommand.CapacityKg));

        RuleFor(command => command.Mileage.Value)
            .GreaterThanOrEqualTo(0).WithMessage(nameof(ErrorCode.FLEET_MILEAGE_NEGATIVE))
            .OverridePropertyName(nameof(UpdateVehicleCommand.Mileage));

        // No rule for NextMaintenanceDate: it is nullable on the row, so every value the wire can
        // carry - a date, or null for "none scheduled" - is one the vehicle may hold.
    }
}
