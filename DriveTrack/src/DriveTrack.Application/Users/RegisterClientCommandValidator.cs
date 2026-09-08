using DriveTrack.Application.Common;
using FluentValidation;

namespace DriveTrack.Application.Users;

/// <summary>
/// FR-3's rules for opening a client account.
/// <para>
/// Every message is an <see cref="ErrorCode"/> <em>name</em>, never a sentence: the adapter resolves
/// it through the same localized catalogue as <c>error.code</c>, so a field message is Ukrainian by
/// construction (NFR-14) and nothing a validator writes can reach a user untranslated.
/// <c>ErrorContractTests</c> closes that catalogue in both directions, which is what makes the
/// convention checkable rather than customary.
/// </para>
/// </summary>
public sealed class RegisterClientCommandValidator : AbstractValidator<RegisterClientCommand>
{
    /// <summary>
    /// E.164: a plus, a non-zero country digit, then seven to fourteen more. Anchored, so
    /// <c>044 123 45 67</c> — the national form a Ukrainian visitor will reach for first — is
    /// rejected rather than half-accepted.
    /// </summary>
    public const string PhoneNumberPattern = @"^\+[1-9]\d{7,14}$";

    /// <summary>The longest a stored name may be, matching the column (FR-1).</summary>
    public const int NameMaximumLength = 100;

    /// <summary>Matching Identity's own <c>email</c> column.</summary>
    public const int EmailMaximumLength = 256;

    /// <summary>Matching the <c>clients.phone_number</c> column.</summary>
    public const int PhoneNumberMaximumLength = 32;

    /// <summary>
    /// A ceiling on the password, which has no column of its own because only its hash is stored.
    /// It is here because registration and sign-in are anonymous endpoints and the hasher is
    /// deliberately slow: an unbounded password is work an unauthenticated caller can ask for.
    /// </summary>
    public const int PasswordMaximumLength = 128;

    /// <summary>Declares the rules.</summary>
    public RegisterClientCommandValidator()
    {
        // WithMessage applies to the rule immediately before it, not to the chain, so every rule
        // states its own key. A single trailing WithMessage would leave the earlier rules carrying
        // FluentValidation's own English sentence - which the adapter would then fail to resolve.
        RuleFor(command => command.FirstName)
            .NotEmpty().WithMessage(nameof(ErrorCode.COMMON_VALIDATION_FAILED))
            .MaximumLength(NameMaximumLength).WithMessage(nameof(ErrorCode.COMMON_VALIDATION_FAILED));

        RuleFor(command => command.LastName)
            .NotEmpty().WithMessage(nameof(ErrorCode.COMMON_VALIDATION_FAILED))
            .MaximumLength(NameMaximumLength).WithMessage(nameof(ErrorCode.COMMON_VALIDATION_FAILED));

        RuleFor(command => command.Email)
            .NotEmpty().WithMessage(nameof(ErrorCode.AUTH_EMAIL_INVALID))
            .MaximumLength(EmailMaximumLength).WithMessage(nameof(ErrorCode.AUTH_EMAIL_INVALID))
            .EmailAddress().WithMessage(nameof(ErrorCode.AUTH_EMAIL_INVALID));

        RuleFor(command => command.PhoneNumber)
            .NotEmpty().WithMessage(nameof(ErrorCode.AUTH_PHONE_NUMBER_INVALID))
            .MaximumLength(PhoneNumberMaximumLength)
                .WithMessage(nameof(ErrorCode.AUTH_PHONE_NUMBER_INVALID))
            .Matches(PhoneNumberPattern).WithMessage(nameof(ErrorCode.AUTH_PHONE_NUMBER_INVALID));

        // Presence only. The strength policy itself is Identity's, configured once in
        // AddIdentityCore and reported back through the same code, so the two cannot disagree.
        RuleFor(command => command.Password)
            .NotEmpty().WithMessage(nameof(ErrorCode.AUTH_PASSWORD_TOO_WEAK))
            .MaximumLength(PasswordMaximumLength).WithMessage(nameof(ErrorCode.AUTH_PASSWORD_TOO_WEAK));

        RuleFor(command => command.PasswordConfirmation)
            .Equal(command => command.Password)
            .WithMessage(nameof(ErrorCode.AUTH_PASSWORD_CONFIRMATION_MISMATCH));
    }
}
