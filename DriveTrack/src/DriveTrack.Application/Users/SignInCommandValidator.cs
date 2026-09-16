using DriveTrack.Application.Common;
using FluentValidation;

namespace DriveTrack.Application.Users;

/// <summary>
/// FR-4's rules. Deliberately thin: anything beyond "both fields were sent" would tell an
/// unauthenticated caller something about the account they named, and the whole point of the
/// sign-in path is that a wrong email and a wrong password are indistinguishable. The one thing it
/// does bound is length: this endpoint is anonymous and the hasher is deliberately slow, so an
/// unbounded password is work a stranger can ask the server to do.
/// </summary>
public sealed class SignInCommandValidator : AbstractValidator<SignInCommand>
{
    /// <summary>Declares the rules.</summary>
    public SignInCommandValidator()
    {
        RuleFor(command => command.Email)
            .NotEmpty().WithMessage(nameof(ErrorCode.AUTH_EMAIL_INVALID))
            .MaximumLength(RegisterClientCommandValidator.EmailMaximumLength)
                .WithMessage(nameof(ErrorCode.AUTH_EMAIL_INVALID));

        RuleFor(command => command.Password)
            .NotEmpty().WithMessage(nameof(ErrorCode.AUTH_PASSWORD_TOO_WEAK))
            .MaximumLength(RegisterClientCommandValidator.PasswordMaximumLength)
                .WithMessage(nameof(ErrorCode.AUTH_PASSWORD_TOO_LONG));
    }
}
