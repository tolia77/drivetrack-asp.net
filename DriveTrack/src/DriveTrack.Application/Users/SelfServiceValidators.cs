using DriveTrack.Application.Common;
using FluentValidation;

namespace DriveTrack.Application.Users;

/// <summary>
/// FR-87's rules, held over the <em>merged</em> profile rather than over the payload (AD-23).
/// <para>
/// The phone number is the whole reason <see cref="ProfileState.IsClient"/> is on the record. DR-3
/// gives a dispatcher, a driver and an administrator no row to store a number in, so for them the
/// only legal merged value is none at all — and a caller who sent one is told so rather than having
/// the field silently dropped, which is the failure <see cref="Optional{T}"/> exists to prevent.
/// </para>
/// <para>
/// The limits and the E.164 pattern come from <see cref="RegisterClientCommandValidator"/> rather
/// than being restated: one phone rule for the whole system.
/// </para>
/// </summary>
public sealed class ProfileStateValidator : AbstractValidator<ProfileState>
{
    /// <summary>Declares the rules.</summary>
    public ProfileStateValidator()
    {
        RuleFor(state => state.FirstName)
            .NotEmpty().WithMessage(nameof(ErrorCode.COMMON_FIELD_REQUIRED))
            .MaximumLength(RegisterClientCommandValidator.NameMaximumLength)
                .WithMessage(nameof(ErrorCode.AUTH_FIRST_NAME_TOO_LONG));

        RuleFor(state => state.LastName)
            .NotEmpty().WithMessage(nameof(ErrorCode.COMMON_FIELD_REQUIRED))
            .MaximumLength(RegisterClientCommandValidator.NameMaximumLength)
                .WithMessage(nameof(ErrorCode.AUTH_LAST_NAME_TOO_LONG));

        // A client must end up with a number: clearing one is not an operation FR-3 leaves open, so
        // a present-null is a 422 rather than an empty string in the column.
        RuleFor(state => state.PhoneNumber)
            .NotEmpty().WithMessage(nameof(ErrorCode.AUTH_PHONE_NUMBER_INVALID))
            .MaximumLength(RegisterClientCommandValidator.PhoneNumberMaximumLength)
                .WithMessage(nameof(ErrorCode.AUTH_PHONE_NUMBER_INVALID))
            .Matches(RegisterClientCommandValidator.PhoneNumberPattern)
                .WithMessage(nameof(ErrorCode.AUTH_PHONE_NUMBER_INVALID))
            .When(state => state.IsClient);

        // And nobody else may end up with one. Same code as the rules above, because the caller's
        // next move is the same either way: stop sending that field.
        RuleFor(state => state.PhoneNumber)
            .Null().WithMessage(nameof(ErrorCode.AUTH_PHONE_NUMBER_INVALID))
            .When(state => !state.IsClient);
    }
}

/// <summary>
/// FR-88's rules. Presence and bounds only on the two passwords, plus the confirmation: the strength
/// policy itself is Identity's, configured once in <c>AddIdentityCore</c> and reported back through
/// the same code, so the two cannot disagree.
/// <para>
/// Whether the current password is <em>right</em> is not a question a validator can answer — only a
/// hash is stored (FR-8) — so that check lives in the service, against the repository, and raises
/// <see cref="ErrorCode.AUTH_CURRENT_PASSWORD_INCORRECT"/>.
/// </para>
/// </summary>
public sealed class ChangePasswordCommandValidator : AbstractValidator<ChangePasswordCommand>
{
    /// <summary>Declares the rules.</summary>
    public ChangePasswordCommandValidator()
    {
        // Neither rule is the strength code: an absent current password is a field the caller left
        // out, and one over the ceiling is long rather than weak. AUTH_PASSWORD_TOO_WEAK names the
        // policy - eight characters, both cases, a digit - which is advice about neither.
        RuleFor(command => command.CurrentPassword)
            .NotEmpty().WithMessage(nameof(ErrorCode.COMMON_FIELD_REQUIRED))
            .MaximumLength(RegisterClientCommandValidator.PasswordMaximumLength)
                .WithMessage(nameof(ErrorCode.AUTH_PASSWORD_TOO_LONG));

        RuleFor(command => command.NewPassword)
            .NotEmpty().WithMessage(nameof(ErrorCode.AUTH_PASSWORD_TOO_WEAK))
            .MaximumLength(RegisterClientCommandValidator.PasswordMaximumLength)
                .WithMessage(nameof(ErrorCode.AUTH_PASSWORD_TOO_LONG));

        RuleFor(command => command.NewPasswordConfirmation)
            .Equal(command => command.NewPassword)
            .WithMessage(nameof(ErrorCode.AUTH_PASSWORD_CONFIRMATION_MISMATCH));
    }
}

/// <summary>
/// FR-92's shape rules — the same three checks, and the same code, as every other address in the
/// system. Whether the address is <em>free</em> is asked once, in <see cref="EmailChange"/>, because
/// it is a question about the whole roster rather than about this request.
/// </summary>
public sealed class ChangeEmailCommandValidator : AbstractValidator<ChangeEmailCommand>
{
    /// <summary>Declares the rules.</summary>
    public ChangeEmailCommandValidator()
    {
        RuleFor(command => command.Email)
            .NotEmpty().WithMessage(nameof(ErrorCode.AUTH_EMAIL_INVALID))
            .MaximumLength(RegisterClientCommandValidator.EmailMaximumLength)
                .WithMessage(nameof(ErrorCode.AUTH_EMAIL_INVALID))
            .EmailAddress().WithMessage(nameof(ErrorCode.AUTH_EMAIL_INVALID));
    }
}
