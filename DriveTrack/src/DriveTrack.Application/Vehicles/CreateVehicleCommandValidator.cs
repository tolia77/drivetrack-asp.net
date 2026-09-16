using DriveTrack.Application.Common;
using FluentValidation;

namespace DriveTrack.Application.Vehicles;

/// <summary>
/// FR-40 and FR-41's rules for a vehicle's own fields, applied to the state the row will hold.
/// <para>
/// Every message is an <see cref="ErrorCode"/> name applied per rule. The bounds match the columns
/// (<c>model</c> 100, <c>license_plate</c> 20, <c>capacity_kg</c> numeric(10,2)), so a value the
/// database would refuse is refused here first, with a field name attached (NFR-4).
/// </para>
/// </summary>
public sealed class CreateVehicleCommandValidator : AbstractValidator<CreateVehicleCommand>
{
    /// <summary>Matching the <c>vehicles.model</c> column.</summary>
    public const int ModelMaximumLength = 100;

    /// <summary>Matching the <c>vehicles.license_plate</c> column.</summary>
    public const int LicensePlateMaximumLength = 20;

    /// <summary>
    /// The largest capacity <c>numeric(10, 2)</c> can hold. Stated so an over-wide value is a 422
    /// naming the field rather than a numeric overflow at the commit.
    /// </summary>
    public const decimal CapacityMaximum = 99_999_999.99m;

    /// <summary>Declares the rules.</summary>
    public CreateVehicleCommandValidator()
    {
        RuleFor(command => command.Model)
            .NotEmpty().WithMessage(nameof(ErrorCode.COMMON_FIELD_REQUIRED))
            .MaximumLength(ModelMaximumLength).WithMessage(nameof(ErrorCode.FLEET_MODEL_TOO_LONG));

        // Bounded and required, and nothing else: a plate format rule is exactly the kind of
        // guess that made the original refuse legitimate plates.
        RuleFor(command => command.LicensePlate)
            .NotEmpty().WithMessage(nameof(ErrorCode.COMMON_FIELD_REQUIRED))
            .MaximumLength(LicensePlateMaximumLength)
                .WithMessage(nameof(ErrorCode.FLEET_LICENSE_PLATE_TOO_LONG));

        RuleFor(command => command.CapacityKg)
            .GreaterThan(0m).WithMessage(nameof(ErrorCode.FLEET_CAPACITY_NOT_POSITIVE))
            .LessThanOrEqualTo(CapacityMaximum)
                .WithMessage(nameof(ErrorCode.FLEET_CAPACITY_TOO_LARGE));

        // An odometer does not run backwards. Zero is legal - a vehicle can arrive new.
        RuleFor(command => command.Mileage)
            .GreaterThanOrEqualTo(0).WithMessage(nameof(ErrorCode.FLEET_MILEAGE_NEGATIVE));

        // Nothing about NextMaintenanceDate beyond its nullability: no requirement constrains when
        // servicing may be due, and a rule invented here would refuse a date a dispatcher meant.
    }
}
