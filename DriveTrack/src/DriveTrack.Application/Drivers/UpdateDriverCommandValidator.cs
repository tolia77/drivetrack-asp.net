using DriveTrack.Application.Common;
using DriveTrack.Application.Users;
using FluentValidation;

namespace DriveTrack.Application.Drivers;

/// <summary>
/// FR-37's rules, applied to the state the driver will hold rather than to the payload.
/// <para>
/// This validator is only ever handed <see cref="UpdateDriverCommand.MergedOnto"/>'s result, in
/// which every field is present — which is what lets the rule below read the value without asking
/// whether the caller sent it.
/// </para>
/// <para>
/// The name bounds are shared with <see cref="RegisterClientCommandValidator"/> rather than
/// restated: the same columns are being written, and two copies of a length would be two answers to
/// one question.
/// </para>
/// <para>
/// There is no rule about <c>VehicleId</c>: a null is FR-38's clear rather than a missing value,
/// and whether a non-null id names a vehicle that exists and is free is a question about other
/// rows, answered in the one writer of <c>drivers.vehicle_id</c> (AD-24).
/// </para>
/// <para>
/// The property name is overridden so a field error names <c>LicenseNumber</c> rather than
/// <c>LicenseNumber.Value</c>: the adapter turns that name into the one the caller's payload used,
/// and a form can only attach a message to an input it actually has (NFR-4).
/// </para>
/// </summary>
public sealed class UpdateDriverCommandValidator : AbstractValidator<UpdateDriverCommand>
{
    /// <summary>Declares the rules.</summary>
    public UpdateDriverCommandValidator()
    {
        RuleFor(command => command.FirstName.Value)
            .NotEmpty().WithMessage(nameof(ErrorCode.COMMON_VALIDATION_FAILED))
            .MaximumLength(RegisterClientCommandValidator.NameMaximumLength)
                .WithMessage(nameof(ErrorCode.COMMON_VALIDATION_FAILED))
            .OverridePropertyName(nameof(UpdateDriverCommand.FirstName));

        RuleFor(command => command.LastName.Value)
            .NotEmpty().WithMessage(nameof(ErrorCode.COMMON_VALIDATION_FAILED))
            .MaximumLength(RegisterClientCommandValidator.NameMaximumLength)
                .WithMessage(nameof(ErrorCode.COMMON_VALIDATION_FAILED))
            .OverridePropertyName(nameof(UpdateDriverCommand.LastName));

        RuleFor(command => command.LicenseNumber.Value)
            .NotEmpty().WithMessage(nameof(ErrorCode.COMMON_VALIDATION_FAILED))
            .MaximumLength(CreateDriverCommandValidator.LicenseNumberMaximumLength)
                .WithMessage(nameof(ErrorCode.COMMON_VALIDATION_FAILED))
            .OverridePropertyName(nameof(UpdateDriverCommand.LicenseNumber));
    }
}
