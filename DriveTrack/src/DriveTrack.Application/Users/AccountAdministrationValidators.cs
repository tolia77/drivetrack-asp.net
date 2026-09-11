using DriveTrack.Application.Common;
using FluentValidation;

namespace DriveTrack.Application.Users;

/// <summary>
/// FR-46's rules, held over the <em>merged</em> client account rather than over the payload (AD-23).
/// <para>
/// The difference matters. A payload carrying only <c>{"firstName": "Олена"}</c> mentions no phone
/// number, and a validator reading the payload would either wave that through or refuse a perfectly
/// valid edit for a field the caller never touched. Validating the merged state asks the only
/// question worth asking: is the row still legal once this change lands?
/// </para>
/// <para>
/// The limits and the E.164 pattern come from <see cref="RegisterClientCommandValidator"/> rather
/// than being restated: one phone rule for the whole system, so a number a client could register
/// with is a number an administrator can also type.
/// </para>
/// </summary>
public sealed class ClientAccountStateValidator : AbstractValidator<ClientAccountState>
{
    /// <summary>Declares the rules.</summary>
    public ClientAccountStateValidator()
    {
        RuleFor(state => state.FirstName)
            .NotEmpty().WithMessage(nameof(ErrorCode.COMMON_VALIDATION_FAILED))
            .MaximumLength(RegisterClientCommandValidator.NameMaximumLength)
                .WithMessage(nameof(ErrorCode.COMMON_VALIDATION_FAILED));

        RuleFor(state => state.LastName)
            .NotEmpty().WithMessage(nameof(ErrorCode.COMMON_VALIDATION_FAILED))
            .MaximumLength(RegisterClientCommandValidator.NameMaximumLength)
                .WithMessage(nameof(ErrorCode.COMMON_VALIDATION_FAILED));

        RuleFor(state => state.PhoneNumber)
            .NotEmpty().WithMessage(nameof(ErrorCode.AUTH_PHONE_NUMBER_INVALID))
            .MaximumLength(RegisterClientCommandValidator.PhoneNumberMaximumLength)
                .WithMessage(nameof(ErrorCode.AUTH_PHONE_NUMBER_INVALID))
            .Matches(RegisterClientCommandValidator.PhoneNumberPattern)
                .WithMessage(nameof(ErrorCode.AUTH_PHONE_NUMBER_INVALID));

        // FR-92. The same three checks, and the same code, as every other address in the system.
        // Whether it is *free* is asked once, in EmailChange, because that is a question about the
        // whole roster rather than about this row.
        RuleFor(state => state.Email)
            .NotEmpty().WithMessage(nameof(ErrorCode.AUTH_EMAIL_INVALID))
            .MaximumLength(RegisterClientCommandValidator.EmailMaximumLength)
                .WithMessage(nameof(ErrorCode.AUTH_EMAIL_INVALID))
            .EmailAddress().WithMessage(nameof(ErrorCode.AUTH_EMAIL_INVALID));

        // Null is the unchanged case and has no rules to break. Everything else is a password the
        // caller is actually setting, so it is bounded here and judged for strength by Identity's
        // own policy - the same policy, reported with the same code, as at registration.
        RuleFor(state => state.Password)
            .NotEmpty().WithMessage(nameof(ErrorCode.AUTH_PASSWORD_TOO_WEAK))
            .MaximumLength(RegisterClientCommandValidator.PasswordMaximumLength)
                .WithMessage(nameof(ErrorCode.AUTH_PASSWORD_TOO_WEAK))
            .When(state => state.Password is not null);
    }
}

/// <inheritdoc cref="ClientAccountStateValidator" />
public sealed class DispatcherAccountStateValidator : AbstractValidator<DispatcherAccountState>
{
    /// <summary>Declares the rules.</summary>
    public DispatcherAccountStateValidator()
    {
        RuleFor(state => state.FirstName)
            .NotEmpty().WithMessage(nameof(ErrorCode.COMMON_VALIDATION_FAILED))
            .MaximumLength(RegisterClientCommandValidator.NameMaximumLength)
                .WithMessage(nameof(ErrorCode.COMMON_VALIDATION_FAILED));

        RuleFor(state => state.LastName)
            .NotEmpty().WithMessage(nameof(ErrorCode.COMMON_VALIDATION_FAILED))
            .MaximumLength(RegisterClientCommandValidator.NameMaximumLength)
                .WithMessage(nameof(ErrorCode.COMMON_VALIDATION_FAILED));

        RuleFor(state => state.Password)
            .NotEmpty().WithMessage(nameof(ErrorCode.AUTH_PASSWORD_TOO_WEAK))
            .MaximumLength(RegisterClientCommandValidator.PasswordMaximumLength)
                .WithMessage(nameof(ErrorCode.AUTH_PASSWORD_TOO_WEAK))
            .When(state => state.Password is not null);
    }
}

/// <summary>
/// FR-49's rules for opening a dispatcher account. Not a merge: there is nothing stored yet, so
/// every field is required and the command is what gets validated.
/// <para>
/// There is no phone number. FR-49 lists name, email and password, and DR-3 gives a dispatcher no
/// subtype row for a phone number to live in.
/// </para>
/// </summary>
public sealed class CreateDispatcherCommandValidator : AbstractValidator<CreateDispatcherCommand>
{
    /// <summary>Declares the rules.</summary>
    public CreateDispatcherCommandValidator()
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

        RuleFor(command => command.Password)
            .NotEmpty().WithMessage(nameof(ErrorCode.AUTH_PASSWORD_TOO_WEAK))
            .MaximumLength(RegisterClientCommandValidator.PasswordMaximumLength)
                .WithMessage(nameof(ErrorCode.AUTH_PASSWORD_TOO_WEAK));
    }
}
