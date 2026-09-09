using DriveTrack.Application.Common;
using DriveTrack.Application.Users;
using FluentValidation;

namespace DriveTrack.Application.Drivers;

/// <summary>
/// FR-35's rules for taking on a driver.
/// <para>
/// Every message is an <see cref="ErrorCode"/> name applied per rule, never once at the end of a
/// chain. The account bounds are shared with <see cref="RegisterClientCommandValidator"/> rather
/// than restated: the same columns are being written, and two copies of a length would be two
/// answers to one question.
/// </para>
/// </summary>
public sealed class CreateDriverCommandValidator : AbstractValidator<CreateDriverCommand>
{
    /// <summary>Matching the <c>drivers.license_number</c> column.</summary>
    public const int LicenseNumberMaximumLength = 50;

    /// <summary>Declares the rules.</summary>
    public CreateDriverCommandValidator()
    {
        RuleFor(command => command.FirstName)
            .NotEmpty().WithMessage(nameof(ErrorCode.COMMON_VALIDATION_FAILED))
            .MaximumLength(RegisterClientCommandValidator.NameMaximumLength)
                .WithMessage(nameof(ErrorCode.COMMON_VALIDATION_FAILED));

        RuleFor(command => command.LastName)
            .NotEmpty().WithMessage(nameof(ErrorCode.COMMON_VALIDATION_FAILED))
            .MaximumLength(RegisterClientCommandValidator.NameMaximumLength)
                .WithMessage(nameof(ErrorCode.COMMON_VALIDATION_FAILED));

        RuleFor(command => command.Email)
            .NotEmpty().WithMessage(nameof(ErrorCode.AUTH_EMAIL_INVALID))
            .MaximumLength(RegisterClientCommandValidator.EmailMaximumLength)
                .WithMessage(nameof(ErrorCode.AUTH_EMAIL_INVALID))
            .EmailAddress().WithMessage(nameof(ErrorCode.AUTH_EMAIL_INVALID));

        // Presence and a ceiling only. The strength policy is Identity's, configured once in
        // AddIdentityCore and reported back through the same code, so the two cannot disagree.
        RuleFor(command => command.Password)
            .NotEmpty().WithMessage(nameof(ErrorCode.AUTH_PASSWORD_TOO_WEAK))
            .MaximumLength(RegisterClientCommandValidator.PasswordMaximumLength)
                .WithMessage(nameof(ErrorCode.AUTH_PASSWORD_TOO_WEAK));

        RuleFor(command => command.LicenseNumber)
            .NotEmpty().WithMessage(nameof(ErrorCode.COMMON_VALIDATION_FAILED))
            .MaximumLength(LicenseNumberMaximumLength)
                .WithMessage(nameof(ErrorCode.COMMON_VALIDATION_FAILED));

        // Nothing about VehicleId: whether that vehicle exists and whether it is free are questions
        // about other rows, and AD-24 answers them in the one writer of drivers.vehicle_id.
    }
}
